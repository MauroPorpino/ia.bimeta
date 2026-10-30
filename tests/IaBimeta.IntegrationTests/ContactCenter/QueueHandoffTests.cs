using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A entrada de conversas na fila e o handoff IA para humano (BOUNDARIES.md §6, ADR 0007), com PostgreSQL real: o ContactCenter
/// decide e o Conversations impõe o estado e a <c>StateVersion</c>. A IA nunca escolhe a fila: o pedido passa pela mesma validação
/// de organização, unidade, pausa e autorização de qualquer outro chamador.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueHandoffTests(PostgresFixture banco)
{
    [Fact]
    public async Task Pedir_atendimento_poe_a_conversa_na_fila_e_a_sessao_vai_para_humano_pendente_sem_responsavel()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdentrada");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var resultado = await c.PedirAtendimentoAsync(sessao, fila.Id, pedido);

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        var item = resultado.Item;
        Assert.Equal((QueueItemStatus.Waiting, fila.Id, sessao, null), (item.Status, item.QueueId, item.SessionId, item.AssignedUserId));
        Assert.Null(item.Pending);

        // O estado da conversa é do Conversations: humano pendente, sem responsável, com a versão do estado avançada.
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)null, 2L), (estado.HandlingMode, estado.ResponderRef, estado.StateVersion));

        var historico = Assert.Single((await c.HistoricoDoItemAsync(item.Id))!);
        Assert.Equal((HistoryKind.Entered, (Guid?)null, fila.Id, pedido, "ai-requested"), (historico.Kind, historico.FromQueueId, historico.ToQueueId, historico.OperationId, historico.Reason));
        Assert.Equal(2L, historico.StateVersionAfter);

        var evento = Assert.IsType<HandlingModeChangedV1>(Assert.Single(c.PayloadsDe(ConversationEventTypes.HandlingModeChanged, sessao)));
        Assert.Equal((HandlingMode.AiAutonomous, HandlingMode.HumanAssisted, (Guid?)null, pedido), (evento.From, evento.To, evento.ResponderRef, evento.TransitionId));
        Assert.Single(await c.AuditoriaAsync("contact-center.item.enter", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Depois_da_entrada_a_IA_deixa_de_responder_na_conversa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdianao");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        await c.NaFilaAsync(sessao, fila.Id);

        Assert.Equal(BeginTurnOutcome.NotAiResponder, (await c.IniciarTurnoAsync(sessao, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task Repetir_o_mesmo_pedido_devolve_o_mesmo_item_sem_mudar_nem_publicar_de_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdrepete");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var primeiro = await c.PedirAtendimentoAsync(sessao, fila.Id, pedido);
        var segundo = await c.PedirAtendimentoAsync(sessao, fila.Id, pedido);

        Assert.Equal((OperationOutcome.Completed, OperationOutcome.AlreadyApplied), (primeiro.Outcome, segundo.Outcome));
        Assert.Equal(primeiro.Item.Id, segundo.Item.Id);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
        Assert.Single((await c.HistoricoDoItemAsync(primeiro.Item.Id))!);
        Assert.Equal(2L, await c.VersaoDaSessaoAsync(sessao));
    }

    [Fact]
    public async Task Pedir_a_mesma_fila_com_outro_identificador_tambem_e_repeticao_e_outra_fila_e_conflito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdoutrafila");
        var fila = await c.CriarFilaAsync("Fila 1");
        var outra = await c.CriarFilaAsync("Fila 2");
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);

        var mesmaFila = await c.PedirAtendimentoAsync(sessao, fila.Id);
        Assert.Equal((OperationOutcome.AlreadyApplied, item.Id), (mesmaFila.Outcome, mesmaFila.Item.Id));

        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.PedirAtendimentoAsync(sessao, outra.Id));
        Assert.Equal(QueueItemConflict.ActiveItemExists, conflito.Conflict);
        Assert.Equal(item.QueueId, (await c.ItemAsync(item.Id))!.QueueId);
    }

    [Fact]
    public async Task Pedido_antigo_ja_aplicado_continua_repeticao_mesmo_depois_de_a_conversa_mudar_de_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdantigo");
        var fila = await c.CriarFilaAsync("Fila 1");
        var outra = await c.CriarFilaAsync("Fila 2");
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();
        var item = (await c.PedirAtendimentoAsync(sessao, fila.Id, pedido)).Item;
        await c.TransferirParaFilaAsync(item.Id, outra.Id, null, c.ContextoDeSistema);

        // O Agents que repete o pedido original (a resposta se perdeu) recebe a repetição, e não um conflito.
        var repetido = await c.PedirAtendimentoAsync(sessao, fila.Id, pedido);

        Assert.Equal((OperationOutcome.AlreadyApplied, outra.Id), (repetido.Outcome, repetido.Item.QueueId));
    }

    [Fact]
    public async Task Depois_de_devolver_a_IA_uma_nova_entrada_cria_outro_item_e_o_historico_de_cada_um_e_separado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdnovoitem");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var primeiro = await c.NaFilaAsync(sessao, fila.Id);
        await c.DevolverAIAAsync(primeiro.Id, c.ContextoDeSistema);

        var segundo = await c.NaFilaAsync(sessao, fila.Id);

        Assert.NotEqual(primeiro.Id, segundo.Id);
        Assert.Equal(QueueItemStatus.Closed, (await c.ItemAsync(primeiro.Id))!.Status);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(segundo.Id))!.Status);
        Assert.Equal(new[] { HistoryKind.Entered, HistoryKind.ReturnedToAi }, (await c.HistoricoDoItemAsync(primeiro.Id))!.Select(h => h.Kind).ToArray());
        Assert.Equal(new[] { HistoryKind.Entered }, (await c.HistoricoDoItemAsync(segundo.Id))!.Select(h => h.Kind).ToArray());
    }

    [Fact]
    public async Task Fila_de_outra_unidade_nao_recebe_a_conversa_e_nada_muda_na_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdunidade");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var filaDaFilial = await c.CriarFilaAsync("Filial", unidade: filial);
        var sessao = await c.NovaConversaAsync(unidade: matriz);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, filaDaFilial.Id));

        Assert.Equal(ContactCenterRule.QueueDoesNotCoverSession, recusa.Rule);
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.AiAutonomous, 1L), (estado.HandlingMode, estado.StateVersion));
        Assert.Empty(await c.ItensAsync());
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
        Assert.Single(await c.AuditoriaAsync("contact-center.item.enter", sessao.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Fila_de_setor_so_recebe_conversa_do_setor_e_conversa_sem_unidade_nao_entra_em_fila_de_unidade()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdsetor");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var coleta = await c.CriarSetorAsync(unidade, "Coleta");
        var recepcao = await c.CriarSetorAsync(unidade, "Recepção");
        var filaDaColeta = await c.CriarFilaAsync("Coleta", unidade: unidade, setor: coleta);
        var filaDaUnidade = await c.CriarFilaAsync("Unidade", unidade: unidade);
        var daRecepcao = await c.NovaConversaAsync(unidade: unidade, setor: recepcao);
        var semUnidade = await c.NovaConversaAsync();

        var outroSetor = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(daRecepcao, filaDaColeta.Id));
        var semNada = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(semUnidade, filaDaUnidade.Id));
        var certa = await c.PedirAtendimentoAsync(daRecepcao, filaDaUnidade.Id);

        Assert.Equal((ContactCenterRule.QueueDoesNotCoverSession, ContactCenterRule.QueueDoesNotCoverSession), (outroSetor.Rule, semNada.Rule));
        Assert.Equal(OperationOutcome.Completed, certa.Outcome);
    }

    [Fact]
    public async Task Fila_pausada_nao_recebe_atendimento_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdpausada");
        var fila = await c.CriarFilaAsync();
        await c.PausarAsync(fila.Id);
        var sessao = await c.NovaConversaAsync();

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, fila.Id));

        Assert.Equal(ContactCenterRule.QueuePaused, recusa.Rule);
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessao))!.HandlingMode);
    }

    [Fact]
    public async Task Conversa_que_ja_tem_atendente_humano_definido_fora_do_ContactCenter_nao_e_tirada_dele()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdjahumano");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var humano = Guid.NewGuid();
        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, humano, 1);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, fila.Id));

        Assert.Equal(ContactCenterRule.ConversationAlreadyHandledByHuman, recusa.Rule);
        Assert.Equal(humano, (await c.SessaoAsync(sessao))!.ResponderRef);
    }

    [Fact]
    public async Task Conversa_que_ja_espera_atendente_humano_sem_responsavel_entra_na_fila_sem_nova_transicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdjapendente");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync(modoInicial: HandlingMode.HumanAssisted);
        var versao = await c.VersaoDaSessaoAsync(sessao);

        var resultado = await c.PedirAtendimentoAsync(sessao, fila.Id);

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        Assert.Equal(versao, await c.VersaoDaSessaoAsync(sessao));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    [Fact]
    public async Task Sessao_encerrada_nao_entra_na_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdencerrada");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        await c.EncerrarSessaoAsync(sessao);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.PedirAtendimentoAsync(sessao, fila.Id));

        Assert.Equal(ContactCenterRule.SessionClosed, recusa.Rule);
        Assert.Empty(await c.ItensAsync());
    }

    [Fact]
    public async Task Fila_de_outra_organizacao_e_inexistente_para_a_conversa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdoutraorg");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var filaDaOutra = await c.CriarFilaAsync("Fila da outra", organizacao: outra.Id);
        var sessao = await c.NovaConversaAsync();

        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.PedirAtendimentoAsync(sessao, filaDaOutra.Id));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.PedirAtendimentoAsync(sessao, Guid.NewGuid()));
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessao))!.HandlingMode);
    }

    [Fact]
    public async Task Quem_nao_le_a_conversa_nao_a_enxerga_e_quem_nao_a_gere_e_negado_e_auditado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdautoriza");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead);
        var soLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead, Permission.ConversationRead);

        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.PedirAtendimentoAsync(sessao, fila.Id, contexto: semLeitura));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.PedirAtendimentoAsync(sessao, fila.Id, contexto: soLeitura));

        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessao))!.HandlingMode);
        Assert.Empty(await c.ItensAsync());
        var negada = Assert.Single(await c.AuditoriaAsync("contact-center.item.enter", sessao.ToString()));
        Assert.Equal((AuditOutcome.Denied, soLeitura.Principal!.UserId), (negada.Outcome, negada.ActorUserId));
    }

    [Fact]
    public async Task Quem_gere_a_conversa_na_organizacao_pode_pedir_o_atendimento_e_fica_registrado_como_autor()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdgerente");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var gerente = await c.NovoAgenteAsync(banco, papel: IaBimeta.Identity.Domain.Role.Manager);

        var resultado = await c.PedirAtendimentoAsync(sessao, fila.Id, motivo: "customer-asked", contexto: gerente.Contexto);

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        var historico = Assert.Single((await c.HistoricoDoItemAsync(resultado.Item.Id))!);
        Assert.Equal((gerente.Id, "customer-asked"), (historico.ActorUserId, historico.Reason));
    }

    [Fact]
    public async Task Pedido_sem_contexto_de_tenant_falha_fechado_e_motivo_fora_do_formato_e_recusado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdsemctx");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(
            null, sp => sp.GetRequiredService<QueueItemService>().RequestHandoffAsync(sessao, fila.Id, Guid.NewGuid(), "ai-requested")));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.PedirAtendimentoAsync(sessao, fila.Id, motivo: "Texto livre com espaços"));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.PedirAtendimentoAsync(sessao, fila.Id, Guid.Empty));
        Assert.Empty(await c.ItensAsync());
    }

    [Fact]
    public async Task Conversa_de_outro_tenant_e_inexistente_mesmo_para_o_sistema_do_outro_tenant()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hdtenanta");
        var outro = await CenarioDeConversas.CriarAsync(banco, "hdtenantb");
        var fila = await c.CriarFilaAsync();
        var filaDoOutro = await outro.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();

        // O sistema do outro tenant não alcança a sessão (nem a fila) deste.
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.PedirAtendimentoAsync(sessao, filaDoOutro.Id));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => outro.PedirAtendimentoAsync(sessao, fila.Id));
        Assert.Equal(HandlingMode.AiAutonomous, (await c.SessaoAsync(sessao))!.HandlingMode);
        Assert.Empty(await c.ItensAsync());
        Assert.Empty(await outro.ItensAsync());
    }
}
