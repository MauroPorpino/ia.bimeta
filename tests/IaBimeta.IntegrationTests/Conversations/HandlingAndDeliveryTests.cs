using IaBimeta.Audit.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Estado de atendimento (máquina de modos com StateVersion e idempotência por TransitionId), encerramento, status
/// monotônico de envio e entrega, falha definitiva sem reprocessamento de IA e reenvio por ação autorizada do atendente
/// (CONVERSATION-LIFECYCLE.md §11), com PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HandlingAndDeliveryTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    /// <summary>Uma sessão com uma resposta da IA já aceita (e, portanto, uma mensagem de saída para receber status).</summary>
    private async Task<(CenarioDeConversas C, IngestInboundMessageResult Entrada, Guid Saida)> ComRespostaAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var r = await c.EntrarAsync(Contato, "Pergunta");
        var turno = Guid.NewGuid();
        var inicio = await c.IniciarTurnoAsync(r.SessionId, turno);
        var resposta = await c.ResponderAsync(r.SessionId, turno, inicio, r.ConversationId);
        return (c, r, resposta.MessageId!.Value);
    }

    private static RequestContext Atendente(CenarioDeConversas c, Guid? organizacao = null) =>
        c.Usuario(organizacao ?? c.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);

    // Transições de modo

    [Fact]
    public async Task A_transicao_para_atendimento_humano_muda_modo_responsavel_e_versao_e_publica_o_evento()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "transicao");
        var r = await c.EntrarAsync(Contato);
        var atendente = Guid.NewGuid();
        var transicao = Guid.NewGuid();

        var resultado = await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, atendente, 1, transicao);

        Assert.Equal((TransitionOutcome.Applied, (long?)2), (resultado.Outcome, resultado.StateVersion));
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((HandlingMode.HumanAssisted, atendente, 2L), (sessao.HandlingMode, sessao.ResponderRef, sessao.StateVersion));
        var evento = Assert.IsType<HandlingModeChangedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.HandlingModeChanged, r.SessionId)));
        Assert.Equal((HandlingMode.AiAutonomous, HandlingMode.HumanAssisted, atendente, 2L, transicao), (evento.From, evento.To, evento.ResponderRef, evento.StateVersion, evento.TransitionId));
    }

    [Fact]
    public async Task Atendimento_humano_pendente_na_fila_nao_tem_responsavel_e_pode_voltar_para_a_IA()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "fila");
        var r = await c.EntrarAsync(Contato);

        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, null, 1)).Outcome);
        var naFila = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)null), (naFila.HandlingMode, naFila.ResponderRef));

        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(r.SessionId, HandlingMode.AiAutonomous, null, 2)).Outcome);
        var devolvida = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((HandlingMode.AiAutonomous, 3L), (devolvida.HandlingMode, devolvida.StateVersion));
        Assert.Equal(BeginTurnOutcome.Granted, (await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task Repetir_a_transicao_pelo_mesmo_TransitionId_nao_muda_nada_nem_publica_de_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idemtransicao");
        var r = await c.EntrarAsync(Contato);
        var atendente = Guid.NewGuid();
        var transicao = Guid.NewGuid();

        var primeira = await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, atendente, 1, transicao);
        var repetida = await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, atendente, 1, transicao);

        Assert.Equal(TransitionOutcome.Applied, primeira.Outcome);
        Assert.Equal((TransitionOutcome.AlreadyApplied, (long?)2), (repetida.Outcome, repetida.StateVersion));
        Assert.Equal(2, (await c.SessaoAsync(r.SessionId))!.StateVersion);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, r.SessionId));
    }

    [Fact]
    public async Task Transicao_com_versao_esperada_antiga_e_recusada_e_nada_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "versaoantiga");
        var r = await c.EntrarAsync(Contato);
        await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1);

        var atrasada = await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1);

        Assert.Equal((TransitionOutcome.StaleState, (long?)2), (atrasada.Outcome, atrasada.StateVersion));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, r.SessionId));
    }

    [Fact]
    public async Task Dez_transicoes_simultaneas_a_partir_da_mesma_versao_aplicam_uma_so()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "corridatransicao");
        var r = await c.EntrarAsync(Contato);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.NewGuid(), 1)));

        Assert.Equal(1, resultados.Count(x => x.Outcome == TransitionOutcome.Applied));
        Assert.Equal(9, resultados.Count(x => x.Outcome == TransitionOutcome.StaleState));
        Assert.Equal(2, (await c.SessaoAsync(r.SessionId))!.StateVersion);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, r.SessionId));
    }

    [Fact]
    public async Task Vinte_repeticoes_simultaneas_do_mesmo_TransitionId_aplicam_uma_so_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "corridaidem");
        var r = await c.EntrarAsync(Contato);
        var atendente = Guid.NewGuid();
        var transicao = Guid.NewGuid();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, atendente, 1, transicao)));

        Assert.Equal(1, resultados.Count(x => x.Outcome == TransitionOutcome.Applied));
        Assert.Equal(19, resultados.Count(x => x.Outcome == TransitionOutcome.AlreadyApplied));
        Assert.Equal(2, (await c.SessaoAsync(r.SessionId))!.StateVersion);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, r.SessionId));
    }

    [Fact]
    public async Task Modo_nao_suportado_e_combinacoes_invalidas_sao_recusados_sem_alterar_a_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "invalida");
        var r = await c.EntrarAsync(Contato);
        var humano = Guid.NewGuid();

        Assert.Equal(TransitionOutcome.ModeNotSupported, (await c.TransicionarAsync(r.SessionId, HandlingMode.AiSupervised, null, 1)).Outcome);
        Assert.Equal(TransitionOutcome.InvalidTransition, (await c.TransicionarAsync(r.SessionId, HandlingMode.AiAutonomous, humano, 1)).Outcome); // IA com responsável humano
        Assert.Equal(TransitionOutcome.InvalidTransition, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, Guid.Empty, 1)).Outcome);
        Assert.Equal(TransitionOutcome.InvalidTransition, (await c.TransicionarAsync(r.SessionId, (HandlingMode)99, null, 1)).Outcome);
        Assert.Equal(TransitionOutcome.InvalidTransition, (await c.TransicionarAsync(r.SessionId, HandlingMode.AiAutonomous, null, 1)).Outcome); // já é esse o estado
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, humano, 1, Guid.Empty));

        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((HandlingMode.AiAutonomous, 1L), (sessao.HandlingMode, sessao.StateVersion));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.HandlingModeChanged, r.SessionId));
    }

    // Encerramento

    [Fact]
    public async Task Encerrar_a_sessao_avanca_a_versao_publica_o_evento_e_e_idempotente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "encerra");
        var r = await c.EntrarAsync(Contato);

        var primeira = await c.EncerrarSessaoAsync(r.SessionId);
        var segunda = await c.EncerrarSessaoAsync(r.SessionId);

        Assert.Equal((true, true, (long?)2), (primeira.SessionFound, primeira.Closed, primeira.StateVersion));
        Assert.Equal((true, false, (long?)2), (segunda.SessionFound, segunda.Closed, segunda.StateVersion));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.SessionClosed, r.SessionId));
        Assert.Equal(TransitionOutcome.SessionClosed, (await c.TransicionarAsync(r.SessionId, HandlingMode.HumanAssisted, null, 2)).Outcome);
        Assert.False((await c.EncerrarSessaoAsync(Guid.NewGuid())).SessionFound);
    }

    // Status de entrega

    [Fact]
    public async Task O_status_de_entrega_so_avanca_e_o_que_nao_avanca_e_ignorado()
    {
        var (c, _, saida) = await ComRespostaAsync("entrega");

        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Queued)).Outcome);
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Sending)).Outcome);
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Sent)).Outcome);
        Assert.Equal(DeliveryStatusOutcome.Applied, (await c.EntregaAsync(saida, DeliveryState.Delivered)).Outcome);
        var regressao = await c.EntregaAsync(saida, DeliveryState.Sent); // chegou fora de ordem
        var igual = await c.EntregaAsync(saida, DeliveryState.Delivered);
        var lida = await c.EntregaAsync(saida, DeliveryState.Read);
        var falhaDepoisDeLida = await c.EntregaAsync(saida, DeliveryState.Failed, "tardia");

        Assert.Equal((DeliveryStatusOutcome.Ignored, (DeliveryState?)DeliveryState.Delivered), (regressao.Outcome, regressao.CurrentState));
        Assert.Equal(DeliveryStatusOutcome.Ignored, igual.Outcome);
        Assert.Equal((DeliveryStatusOutcome.Applied, (DeliveryState?)DeliveryState.Read), (lida.Outcome, lida.CurrentState));
        Assert.Equal(DeliveryStatusOutcome.Ignored, falhaDepoisDeLida.Outcome); // depois de enviada, a mensagem não "falha"
    }

    [Fact]
    public async Task Status_de_entrega_de_mensagem_inexistente_ou_de_entrada_e_recusado()
    {
        var (c, entrada, _) = await ComRespostaAsync("entregaerrada");

        Assert.Equal(DeliveryStatusOutcome.MessageNotFound, (await c.EntregaAsync(Guid.NewGuid(), DeliveryState.Sent)).Outcome);
        Assert.Equal(DeliveryStatusOutcome.NotAnOutboundMessage, (await c.EntregaAsync(entrada.MessageId, DeliveryState.Delivered)).Outcome);
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntregaAsync(entrada.MessageId, (DeliveryState)99));
    }

    [Fact]
    public async Task O_status_de_uma_sessao_so_e_aplicado_no_proprio_tenant()
    {
        var a = await ComRespostaAsync("a");
        var b = await CenarioDeConversas.CriarAsync(banco, "b");

        Assert.Equal(DeliveryStatusOutcome.MessageNotFound, (await b.EntregaAsync(a.Saida, DeliveryState.Delivered)).Outcome);
        var mensagem = (await a.C.HistoricoAsync(a.Entrada.SessionId))!.Items.Last();
        Assert.Equal(DeliveryState.Requested, mensagem.DeliveryState);
    }

    [Fact]
    public async Task Falha_definitiva_de_envio_marca_a_sessao_sinaliza_o_ContactCenter_e_nao_dispara_nova_IA()
    {
        var (c, r, saida) = await ComRespostaAsync("falha");
        await c.EntregaAsync(saida, DeliveryState.Queued);
        await c.EntregaAsync(saida, DeliveryState.Sending);

        var falha = await c.EntregaAsync(saida, DeliveryState.Failed, "recipient-unreachable");
        var repetida = await c.EntregaAsync(saida, DeliveryState.Failed, "recipient-unreachable");
        var voltar = await c.EntregaAsync(saida, DeliveryState.Sent);

        Assert.Equal((DeliveryStatusOutcome.Applied, (DeliveryState?)DeliveryState.Failed), (falha.Outcome, falha.CurrentState));
        Assert.Equal(DeliveryStatusOutcome.Ignored, repetida.Outcome);
        Assert.Equal(DeliveryStatusOutcome.Ignored, voltar.Outcome); // falha definitiva é terminal
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.True(sessao.ReplyDeliveryFailed);
        Assert.Equal(1, sessao.AnsweredThroughSeq); // COV-3: a cobertura lógica não muda
        Assert.False(sessao.HasActiveTurn); // e não nasce turno de IA
        var evento = Assert.IsType<OutboundMessageFailedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.OutboundMessageFailed, r.SessionId)));
        Assert.Equal((saida, 1L, "recipient-unreachable"), (evento.MessageId, evento.CoveredThroughSeq, evento.Reason));
        // Nenhum evento que o Agents consuma para abrir turno foi publicado por causa da falha.
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.InboundMessageRegistered, r.SessionId));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.HandlingModeChanged, r.SessionId));
    }

    [Fact]
    public async Task Falha_definitiva_exige_motivo_curto_e_um_turno_seguinte_ve_a_falha_no_snapshot()
    {
        var (c, r, saida) = await ComRespostaAsync("falhamotivo");

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntregaAsync(saida, DeliveryState.Failed));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntregaAsync(saida, DeliveryState.Failed, "Texto livre com espaços e Maiúsculas"));
        Assert.False((await c.SessaoAsync(r.SessionId))!.ReplyDeliveryFailed);

        await c.EntregaAsync(saida, DeliveryState.Dead, "retries-exhausted");
        await c.EntrarAsync(Contato, "Alguém aí?");
        var inicio = await c.IniciarTurnoAsync(r.SessionId, Guid.NewGuid());

        Assert.True(inicio.Snapshot!.ReplyDeliveryFailed);
        Assert.Equal(DeliveryState.Dead, inicio.Snapshot.LastReplyDeliveryState);
    }

    // Reenvio por ação do atendente

    [Fact]
    public async Task O_atendente_autorizado_pede_o_reenvio_de_uma_resposta_com_falha_definitiva_com_vinculo_e_auditoria()
    {
        var (c, r, saida) = await ComRespostaAsync("reenvio");
        await c.EntregaAsync(saida, DeliveryState.Failed, "template-rejected");
        var atendente = Atendente(c);
        var pedido = Guid.NewGuid();

        var resultado = await c.PedirReenvioAsync(saida, pedido, atendente);

        Assert.Equal(ResendOutcome.Requested, resultado.Outcome);
        var novo = (await c.HistoricoAsync(r.SessionId))!.Items.Single(m => m.Id == resultado.NewMessageId);
        Assert.Equal(saida, novo.ResendOfMessageId);
        Assert.Equal((MessageDirection.Outbound, MessageAuthor.Human, DeliveryState.Requested), (novo.Direction, novo.Author, novo.DeliveryState!.Value));
        Assert.Equal(atendente.Principal!.UserId, novo.AuthorRef); // o autor é o usuário do contexto, não um parâmetro
        Assert.Equal("Sim, são 8 horas de jejum.", novo.Text);

        var evento = c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId).Cast<OutboundMessageRequestedV1>().Single(e => e.ResendOfMessageId == saida);
        Assert.Equal((resultado.NewMessageId, pedido), (evento.MessageId, evento.OperationId)); // a chave do efeito externo deriva do pedido

        // Reenviar não cria turno de IA nem muda a cobertura.
        var sessao = (await c.SessaoAsync(r.SessionId))!;
        Assert.Equal((1L, false), (sessao.AnsweredThroughSeq, sessao.HasActiveTurn));

        // COV-7.2: a trilha de auditoria registra quem pediu.
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var auditoria = await escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Events
            .SingleAsync(e => e.Operation == "conversations.message.resend" && e.ResourceId == resultado.NewMessageId.ToString());
        Assert.Equal("message", auditoria.ResourceType);
    }

    [Fact]
    public async Task O_mesmo_RequestId_devolve_o_mesmo_reenvio_e_outro_pedido_enquanto_ha_um_ativo_e_recusado()
    {
        var (c, r, saida) = await ComRespostaAsync("reenvioidem");
        await c.EntregaAsync(saida, DeliveryState.Failed, "template-rejected");
        var atendente = Atendente(c);
        var pedido = Guid.NewGuid();

        var primeiro = await c.PedirReenvioAsync(saida, pedido, atendente);
        var repetido = await c.PedirReenvioAsync(saida, pedido, atendente);
        var outroPedido = await c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente);

        Assert.Equal(ResendOutcome.Requested, primeiro.Outcome);
        Assert.Equal((ResendOutcome.AlreadyRequested, primeiro.NewMessageId), (repetido.Outcome, repetido.NewMessageId));
        Assert.Equal(ResendOutcome.ResendInProgress, outroPedido.Outcome);
        Assert.Equal(1, (await c.HistoricoAsync(r.SessionId))!.Items.Count(m => m.ResendOfMessageId == saida));
        // O mesmo pedido não vale para outra mensagem.
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.PedirReenvioAsync(r.MessageId, pedido, atendente));
    }

    [Fact]
    public async Task Dez_pedidos_simultaneos_de_reenvio_geram_uma_so_solicitacao_de_saida()
    {
        var (c, r, saida) = await ComRespostaAsync("corridareenvio");
        await c.EntregaAsync(saida, DeliveryState.Failed, "template-rejected");
        var atendente = Atendente(c);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente)));

        Assert.Equal(1, resultados.Count(x => x.Outcome == ResendOutcome.Requested));
        Assert.Equal(9, resultados.Count(x => x.Outcome == ResendOutcome.ResendInProgress));
        Assert.Equal(1, (await c.HistoricoAsync(r.SessionId))!.Items.Count(m => m.ResendOfMessageId == saida));
        Assert.Equal(1, c.PayloadsDe(ConversationEventTypes.OutboundMessageRequested, r.SessionId).Cast<OutboundMessageRequestedV1>().Count(e => e.ResendOfMessageId == saida));
    }

    [Fact]
    public async Task Resultado_ambiguo_ou_mensagem_sem_falha_definitiva_nunca_e_reenviado()
    {
        var (c, r, saida) = await ComRespostaAsync("ambiguo");
        var atendente = Atendente(c);

        Assert.Equal(ResendOutcome.NotDefinitiveFailure, (await c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente)).Outcome); // ainda Requested
        await c.EntregaAsync(saida, DeliveryState.Sending);
        await c.EntregaAsync(saida, DeliveryState.SendUnknown);
        Assert.Equal(ResendOutcome.NotDefinitiveFailure, (await c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente)).Outcome); // EFX-3: SendUnknown
        Assert.Equal(ResendOutcome.NotDefinitiveFailure, (await c.PedirReenvioAsync(r.MessageId, Guid.NewGuid(), atendente)).Outcome); // mensagem do cliente

        Assert.Single((await c.HistoricoAsync(r.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);
    }

    [Fact]
    public async Task Reenvio_exige_usuario_autenticado_com_gerencia_e_o_contexto_de_sistema_nao_basta()
    {
        var (c, r, saida) = await ComRespostaAsync("reenvioauth");
        await c.EntregaAsync(saida, DeliveryState.Failed, "template-rejected");
        var org2 = (await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização")).Id;

        Assert.Equal(ResendOutcome.NotAuthorized, (await c.PedirReenvioAsync(saida, Guid.NewGuid())).Outcome); // contexto de sistema: nenhum componente automático reenvia
        Assert.Equal(ResendOutcome.NotAuthorized, (await c.PedirReenvioAsync(saida, Guid.NewGuid(), c.Usuario(c.OrganizacaoId, Permission.ConversationRead))).Outcome);
        Assert.Equal(ResendOutcome.MessageNotFound, (await c.PedirReenvioAsync(saida, Guid.NewGuid(), Atendente(c, org2))).Outcome); // sem leitura, responde como inexistente
        Assert.Equal(ResendOutcome.MessageNotFound, (await c.PedirReenvioAsync(Guid.NewGuid(), Guid.NewGuid(), Atendente(c))).Outcome);
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.PedirReenvioAsync(saida, Guid.Empty, Atendente(c)));

        Assert.Single((await c.HistoricoAsync(r.SessionId))!.Items, m => m.Direction == MessageDirection.Outbound);
    }

    [Fact]
    public async Task Reenvio_em_sessao_encerrada_e_recusado_e_depois_de_nova_falha_um_novo_pedido_e_aceito()
    {
        var (c, r, saida) = await ComRespostaAsync("reenviofluxo");
        await c.EntregaAsync(saida, DeliveryState.Failed, "template-rejected");
        var atendente = Atendente(c);

        var primeiro = await c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente);
        await c.EntregaAsync(primeiro.NewMessageId!.Value, DeliveryState.Failed, "template-rejected"); // o reenvio também falhou, definitivamente
        var segundo = await c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente); // agora não há reenvio ativo
        await c.EncerrarSessaoAsync(r.SessionId);
        var terceiro = await c.PedirReenvioAsync(saida, Guid.NewGuid(), atendente);

        Assert.Equal(ResendOutcome.Requested, primeiro.Outcome);
        Assert.Equal(ResendOutcome.Requested, segundo.Outcome);
        Assert.NotEqual(primeiro.NewMessageId, segundo.NewMessageId);
        Assert.Equal(ResendOutcome.SessionClosed, terceiro.Outcome);
    }
}
