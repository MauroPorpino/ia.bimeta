using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.ContactCenter;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O handoff para o atendente humano pelo ContactCenter REAL, as falhas do modelo (transitórias e permanentes, sem fallback), a ausência de configuração e a política de mídia (MED-2), com
/// PostgreSQL real. O modelo SUGERE o handoff, o Agents valida e executa pelo contrato: o item entra na fila e, sem atendente, o atendimento fica pendente com o histórico.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentsHandoffAndFailureTests(PostgresFixture banco)
{
    private const string AvisoDeTransferencia = "Vou transferir você para um atendente. Um momento, por favor.";

    // O handoff decidido pela IA

    [Fact]
    public async Task Decisao_de_handoff_do_modelo_avisa_o_cliente_cria_o_item_na_fila_e_o_atendimento_fica_pendente_sem_a_IA()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghand");
        var fila = await a.FilaDeAtendimentoAsync();
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Transferir("price"));
        var entrada = await a.EntrarAsync("mauro", "Quanto custa o hemograma?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);

        // O item entrou na fila (sem atendente, pendente), com o motivo da IA e o TurnId como chave de idempotência.
        var item = Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal((QueueItemStatus.Waiting, fila, entrada.SessionId), (item.Status, item.QueueId, item.SessionId));
        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        var historico = Assert.Single((await a.C.HistoricoDoItemAsync(item.Id))!);
        Assert.Equal(("ai-decision.price", turno.Id), (historico.Reason, historico.OperationId));
        Assert.Equal((AiTurnState.Completed, "handoff:handoff-price"), (turno.State, turno.EndReason));

        // O estado da conversa é do Conversations: humano pendente, sem responsável. O aviso do tenant saiu antes (uma resposta determinística, sem modelo).
        var sessao = (await a.C.SessaoAsync(entrada.SessionId))!;
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)null), (sessao.HandlingMode, sessao.ResponderRef));
        var aviso = Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal((AvisoDeTransferencia, ReplyKind.Informative), (aviso.Text, aviso.ReplyKind));
        Assert.Equal(1, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Depois_do_handoff_o_cliente_escreve_de_novo_e_a_IA_nao_responde_nem_chama_o_modelo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghand2");
        await a.FilaDeAtendimentoAsync();
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Transferir("customer_request"));
        var entrada = await a.EntrarAsync("mauro", "Quero falar com uma pessoa");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.HandedOff, Assert.Single(await a.ExecutarAsync()).Outcome);

        // O cliente escreve de novo: o atendimento já é humano. O turno aberto pelo evento é cancelado no BeginTurn, e nenhuma resposta automática sai.
        await a.EntrarAsync("mauro", "Alô? Tem alguém aí?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        var segunda = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Cancelled, "not-ai-responder"), (segunda.Outcome, segunda.Code));
        Assert.Equal(1, a.ChamadasAoModelo);
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)); // só o aviso de transferência
    }

    [Fact]
    public async Task Sem_fila_que_atenda_o_handoff_e_recusado_de_vez_o_turno_morre_com_o_motivo_e_a_conversa_continua_com_a_IA()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghand3");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Transferir("coverage"));
        var entrada = await a.EntrarAsync("mauro", "O convênio cobre?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Dead, resultado.Outcome);
        Assert.StartsWith("handoff-refused-", resultado.Code);
        Assert.Equal(HandlingMode.AiAutonomous, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        Assert.Empty(await a.ItensDaFilaAsync());

        // O cliente NÃO foi avisado de uma transferência que as regras recusam (a verificação antecede o aviso): a conversa não recebeu promessa nenhuma.
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.False((await a.UnicoTurnoAsync(entrada.SessionId)).NoticeDone);
    }

    [Fact]
    public async Task Fila_pausada_e_recusa_transitoria_o_turno_espera_o_recuo_e_so_repete_o_pedido_sem_nova_chamada_ao_modelo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghand4");
        var fila = await a.FilaDeAtendimentoAsync();
        await a.C.PausarAsync(fila);
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Transferir("schedule"));
        var entrada = await a.EntrarAsync("mauro", "Vocês abrem sábado?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeira = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Retrying, primeira.Outcome);
        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Ready, 1, "ai-decision.schedule", false), (turno.State, turno.Attempts, turno.HandoffReason, turno.NoticeDone));
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId)); // fila pausada: nada de aviso por agora

        // A fila volta e o recuo passa: o mesmo pedido (mesmo TurnId) conclui, sem nova chamada ao modelo. Agora as regras aceitam, e o aviso sai UMA vez, antes do pedido.
        await a.C.RetomarAsync(fila);
        a.Avancar(TimeSpan.FromSeconds(6));
        var segunda = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.HandedOff, segunda.Outcome);
        Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal(1, a.ChamadasAoModelo);
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    // Falhas do modelo

    [Fact]
    public async Task Falha_transitoria_tenta_de_novo_com_recuo_e_na_segunda_chamada_responde()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agtrans");
        a.Roteiro = _ => a.ChamadasAoModelo == 1
            ? a.Falha(AiErrorKind.Transient, "http-503")
            : a.Sucesso(CenarioDeAgentes.Responder("Agora foi."));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeira = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Retrying, primeira.Outcome);
        var esperando = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Ready, 1), (esperando.State, esperando.Attempts));
        Assert.Empty(await a.ExecutarAsync()); // o recuo ainda não passou

        a.Avancar(TimeSpan.FromSeconds(6));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal("Agora foi.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
        Assert.Equal(2, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Tentativas_esgotadas_o_turno_morre_e_o_cliente_vai_para_o_atendente_sem_trocar_de_provedor()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agesgota");
        await a.FilaDeAtendimentoAsync();
        a.Roteiro = _ => a.Falha(AiErrorKind.Transient, "http-503");
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        Assert.Equal(TurnRunOutcome.Retrying, Assert.Single(await a.ExecutarAsync()).Outcome);
        a.Avancar(TimeSpan.FromSeconds(6));
        Assert.Equal(TurnRunOutcome.Retrying, Assert.Single(await a.ExecutarAsync()).Outcome);
        a.Avancar(TimeSpan.FromSeconds(11));
        var ultima = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Dead, ultima.Outcome);
        Assert.StartsWith("attempts-exhausted-", ultima.Code);
        Assert.Equal(3, a.ChamadasAoModelo); // exatamente MaxTurnAttempts, no MESMO provedor
        Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Dead, HandoffReasons.AiProcessingFailed), (turno.State, turno.HandoffReason));
    }

    [Fact]
    public async Task Credencial_invalida_e_falha_permanente_sem_nova_tentativa_e_o_atendente_assume()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agcred");
        await a.FilaDeAtendimentoAsync();
        a.Roteiro = _ => a.Falha(AiErrorKind.InvalidCredentials, "http-401", AiUsageCertainty.NoneIncurred);
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-invalid-credentials"), (resultado.Outcome, resultado.Code));
        Assert.Equal(1, a.ChamadasAoModelo);
        Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    [Fact]
    public async Task Cota_do_provedor_esgotada_e_permanente_mesmo_que_o_provedor_sugira_esperar()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agcota");
        await a.FilaDeAtendimentoAsync();
        a.Roteiro = _ => a.Falha(AiErrorKind.QuotaOrBilling, "credit_balance_exhausted");
        await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-quota-or-billing"), (resultado.Outcome, resultado.Code));
        Assert.Equal(1, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Tenant_sem_IA_configurada_transfere_ao_atendente_e_o_modelo_nunca_e_chamado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agsemia", configurarIA: false);
        await a.FilaDeAtendimentoAsync();
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-not-configured"), (resultado.Outcome, resultado.Code));
        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    [Fact]
    public async Task Saida_invalida_do_modelo_nao_executa_nada_e_conta_como_falha_da_tentativa()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aginval");
        a.Roteiro = _ => a.ChamadasAoModelo == 1
            ? a.Sucesso("{\"action\":\"apagar_tudo\",\"reply_text\":\"\",\"handoff_reason\":\"none\"}")
            : a.Sucesso(CenarioDeAgentes.Responder("Resposta válida."));
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeira = Assert.Single(await a.ExecutarAsync());

        // Ação fora da lista fechada: nada é executado e nenhuma resposta sai.
        Assert.Equal(TurnRunOutcome.Retrying, primeira.Outcome);
        Assert.StartsWith("invalid-output-", primeira.Code);
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Empty(await a.ItensDaFilaAsync());

        a.Avancar(TimeSpan.FromSeconds(6));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal("Resposta válida.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
    }

    [Fact]
    public async Task Sem_definicao_de_agente_publicada_a_IA_nao_responde_e_transfere()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agsemdef", publicarAgente: false);
        await a.FilaDeAtendimentoAsync();
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Dead, "ai-not-configured"), (resultado.Outcome, resultado.Code));
        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    // Mídia (MED-1 e MED-2): o modelo nunca vê conteúdo que não está pronto

    [Fact]
    public async Task Imagem_pendente_espera_o_prazo_sem_chamar_o_modelo_e_depois_vai_para_o_atendente_com_o_motivo_content_unavailable()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agimg");
        var fila = await a.FilaDeAtendimentoAsync();
        var entrada = await a.EntrarAsync("mauro", null, MessageKind.Image);
        Assert.Equal(ContentState.Pending, entrada.ContentState);
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var espera = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.WaitingForContent, espera.Outcome);
        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal(AiTurnState.Scheduled, turno.State);
        Assert.NotNull(turno.ContentWaitStartedAt);
        Assert.Equal(0, a.ChamadasAoModelo);

        // Esgotado o prazo adicional da imagem, o atendente assume (a IA não improvisa sobre uma imagem que não viu).
        a.Avancar(TimeSpan.FromSeconds(11));
        var fim = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.HandedOff, "content-unavailable"), (fim.Outcome, fim.Code));
        var item = Assert.Single(await a.ItensDaFilaAsync());
        Assert.Equal((fila, entrada.SessionId), (item.QueueId, item.SessionId));
        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(entrada.SessionId))!.HandlingMode);
    }

    [Fact]
    public async Task Audio_sem_transcricao_recebe_a_resposta_informativa_do_tenant_sem_modelo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agaudio", textoDeAudio: "Recebi seu áudio, mas só leio texto por enquanto. Pode escrever?");
        var entrada = await a.EntrarAsync("mauro", null, MessageKind.Audio);
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.WaitingForContent, Assert.Single(await a.ExecutarAsync()).Outcome);

        a.Avancar(TimeSpan.FromSeconds(11));
        var fim = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.InformativeReply, "informative-reply"), (fim.Outcome, fim.Code));
        var resposta = Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(("Recebi seu áudio, mas só leio texto por enquanto. Pode escrever?", ReplyKind.Informative), (resposta.Text, resposta.ReplyKind));
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Texto_junto_de_audio_pendente_o_modelo_nunca_ve_o_audio_nem_o_texto_a_politica_vale_para_o_grupo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agmisto");
        var entrada = await a.EntrarAsync("mauro", "Segue meu pedido", MessageKind.Text);
        await a.EntrarAsync("mauro", null, MessageKind.Audio);
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.WaitingForContent, Assert.Single(await a.ExecutarAsync()).Outcome);

        a.Avancar(TimeSpan.FromSeconds(11));
        var fim = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.InformativeReply, fim.Outcome);
        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
    }

    [Fact]
    public async Task Conteudo_que_fica_pronto_a_tempo_acorda_o_turno_e_o_modelo_ve_so_o_texto_pronto()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agpronto");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Entendi o documento."));
        var entrada = await a.EntrarAsync("mauro", null, MessageKind.Image);
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.WaitingForContent, Assert.Single(await a.ExecutarAsync()).Outcome);
        Assert.Equal(0, a.ChamadasAoModelo);

        // Quem processa a mídia informa o texto pronto; o evento acorda o turno que esperava, sem esperar o recuo.
        await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<IConversationIngestion>().SetMessageContentAsync(new SetMessageContentCommand(entrada.MessageId, "Pedido de hemograma completo")));
        await a.RelayAsync();
        var fim = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Replied, fim.Outcome);
        var pedido = Assert.Single(a.PedidosAoModelo);
        Assert.Contains("Pedido de hemograma completo", pedido.Messages[^1].Text);
        Assert.Equal("Entendi o documento.", Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)).Text);
    }
}
