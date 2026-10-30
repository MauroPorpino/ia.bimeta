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
/// Falha no meio e unidade autorizada. O item guarda a intenção ANTES de chamar o Conversations; se algo falhar, a conversa fica no
/// modo anterior e repetir a operação (ou o job de reconciliação) conclui sem duplicar efeito (BOUNDARIES.md §6). A unidade de uma
/// fila de destino só vale se o número pelo qual o cliente fala a autoriza, consultada ao Channels a cada pedido (ADR 0008).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueRecoveryAndUnitTests(PostgresFixture banco)
{
    private sealed record Cena(
        CenarioDeConversas C,
        CenarioDeConversas Fora,
        Guid Matriz,
        Guid Filial,
        Guid SetorDaFilial,
        Guid OutraFilial,
        ServiceQueueView FilaDaMatriz,
        ServiceQueueView FilaDaFilial,
        ServiceQueueView FilaDoSetor,
        ServiceQueueView FilaDaOutraFilial,
        ChannelInstanceId Canal,
        Agente Ana);

    /// <summary>O número autoriza a matriz e a filial; a outra filial existe e tem fila, mas o número NÃO a autoriza.</summary>
    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var setor = await c.CriarSetorAsync(filial, "Coleta");
        var outraFilial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Outra filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial });
        var filaDaMatriz = await c.CriarFilaAsync("Matriz", unidade: matriz);
        var filaDaFilial = await c.CriarFilaAsync("Filial", unidade: filial);
        var filaDoSetor = await c.CriarFilaAsync("Filial - coleta", unidade: filial, setor: setor);
        var filaDaOutra = await c.CriarFilaAsync("Outra filial", unidade: outraFilial);
        var ana = await c.AgenteNaFilaAsync(banco, filaDaMatriz.Id);
        return new Cena(c, c.ComCanaisIndisponiveis(), matriz, filial, setor, outraFilial, filaDaMatriz, filaDaFilial, filaDoSetor, filaDaOutra, canal, ana);
    }

    private static async Task<QueueItemView> AtribuidaAsync(Cena s, Guid unidade)
    {
        var sessao = await s.C.NovaConversaAsync(unidade: unidade, canal: s.Canal);
        var fila = unidade == s.Matriz ? s.FilaDaMatriz : s.FilaDaFilial;
        var item = await s.C.NaFilaAsync(sessao, fila.Id);
        return (await s.C.AssumirAsync(item.Id, s.Ana.Contexto)).Item;
    }

    // Reconciliação

    [Fact]
    public async Task O_job_conclui_a_atribuicao_que_ficou_pendente_sem_duplicar_o_efeito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcjob");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var pedido = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        Assert.Equal(1, await c.ReconciliarAsync());

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), (depois.Status, depois.AssignedUserId));
        Assert.Null(depois.Pending);
        Assert.Equal(ana.Id, (await c.SessaoAsync(sessao))!.ResponderRef);
        Assert.Equal(pedido, (await c.HistoricoDoItemAsync(item.Id))!.Last().OperationId);

        // Rodar de novo (ou o usuário repetir o pedido) não faz nada.
        Assert.Equal(0, await c.ReconciliarAsync());
        Assert.Equal(OperationOutcome.AlreadyApplied, (await c.AssumirAsync(item.Id, ana.Contexto, pedido)).Outcome);
        Assert.Equal(2, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
        Assert.Equal(2, (await c.HistoricoDoItemAsync(item.Id))!.Count);
    }

    [Fact]
    public async Task Se_o_processo_caiu_depois_do_Conversations_a_reconciliacao_so_fecha_o_item_sem_nova_transicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcdepois");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        // O Conversations já aplicou a transição (com o identificador da operação), mas o ContactCenter caiu antes de concluir.
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana.Id, await c.VersaoDaSessaoAsync(sessao), operacao)).Outcome);
        Assert.Equal(3L, await c.VersaoDaSessaoAsync(sessao));

        Assert.Equal(1, await c.ReconciliarAsync());

        Assert.Equal((QueueItemStatus.Assigned, ana.Id), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.AssignedUserId));
        Assert.Equal(3L, await c.VersaoDaSessaoAsync(sessao));
        Assert.Equal(2, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    [Fact]
    public async Task O_job_so_roda_como_sistema_e_so_retoma_o_que_esta_pendente_ha_tempo_suficiente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcregras");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ReconciliarAsync(ana.Contexto));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ReconciliarAsync(c.GestorDeFilas()));
        Assert.Equal(0, await c.ReconciliarAsync(idadeMinima: TimeSpan.FromHours(1)));
        await Assert.ThrowsAsync<ContactCenterValidationException>(() => c.ReconciliarAsync(lote: 0));
        Assert.NotNull((await c.ItemAsync(item.Id))!.Pending);

        Assert.Equal(1, await c.ReconciliarAsync());
        Assert.Null((await c.ItemAsync(item.Id))!.Pending);
    }

    [Fact]
    public async Task Se_a_sessao_terminou_enquanto_a_operacao_esperava_o_job_encerra_o_item_com_o_registro_no_historico()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcencerrada");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
        await c.EncerrarSessaoAsync(sessao);

        Assert.Equal(1, await c.ReconciliarAsync());

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Closed, "session-closed", null), (depois.Status, depois.CloseReason, depois.Pending));
        Assert.Equal(HistoryKind.SessionClosed, (await c.HistoricoDoItemAsync(item.Id))!.Last().Kind);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.session-closed", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task O_destino_que_saiu_da_fila_entre_a_intencao_e_a_conclusao_cancela_a_operacao_e_nada_muda_na_conversa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rcsaiu");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var beto = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        var basis = await c.BaseDaSessaoAsync(sessao);
        await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            (await itens.FindItemAsync(item.Id, default))!.BeginTransferToQueue(filaB.Id, beto.Id, null, null, Guid.NewGuid(), null, "transfer", DateTimeOffset.UtcNow, basis: basis);
            await itens.SaveChangesAsync(default);
            return 0;
        });
        await c.RemoverAtendenteAsync(filaB.Id, beto.Id);

        Assert.Equal(0, await c.ReconciliarAsync());

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((filaA.Id, QueueItemStatus.Waiting, null), (depois.QueueId, depois.Status, depois.Pending));
        Assert.Equal(2L, await c.VersaoDaSessaoAsync(sessao));
        Assert.Single(await c.AuditoriaAsync("contact-center.item.transfer", item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
    }

    // Unidade e canal (ADR 0008)

    [Fact]
    public async Task Transferir_para_fila_de_unidade_autorizada_pelo_numero_encaminha_a_sessao_e_deixa_o_rastro_nos_dois_modulos()
    {
        var s = await MontarAsync("unautoriza");
        var item = await AtribuidaAsync(s, s.Matriz);

        var transferido = await s.C.TransferirParaFilaAsync(item.Id, s.FilaDaFilial.Id, null, s.Ana.Contexto);

        Assert.Equal(OperationOutcome.Completed, transferido.Outcome);
        Assert.Equal((s.FilaDaFilial.Id, s.Filial, QueueItemStatus.Waiting), (transferido.Item.QueueId, transferido.Item.BusinessUnitId, transferido.Item.Status));
        var sessao = (await s.C.SessaoAsync(item.SessionId))!;
        Assert.Equal((s.Filial, (Guid?)null, HandlingMode.HumanAssisted, (Guid?)null), (sessao.BusinessUnitId, sessao.DepartmentId, sessao.HandlingMode, sessao.ResponderRef));
        var rastro = Assert.Single((await s.C.RastroAsync(item.SessionId))!);
        Assert.Equal((s.Matriz, s.Filial, "queue-transfer"), (rastro.FromBusinessUnitId, rastro.ToBusinessUnitId, rastro.Reason));
        var evento = Assert.IsType<SessionTransferredV1>(Assert.Single(s.C.PayloadsDe(ConversationEventTypes.SessionTransferred, item.SessionId)));
        Assert.Equal((s.Matriz, s.Filial), (evento.FromBusinessUnitId, evento.ToBusinessUnitId));
        Assert.Equal(HistoryKind.TransferredQueue, (await s.C.HistoricoDoItemAsync(item.Id))!.Last().Kind);
    }

    [Fact]
    public async Task Transferir_para_fila_de_setor_leva_a_unidade_e_o_setor_e_o_item_acompanha()
    {
        var s = await MontarAsync("unsetor");
        var item = await AtribuidaAsync(s, s.Matriz);

        var transferido = await s.C.TransferirParaFilaAsync(item.Id, s.FilaDoSetor.Id, null, s.Ana.Contexto);

        Assert.Equal((s.Filial, s.SetorDaFilial), (transferido.Item.BusinessUnitId, transferido.Item.DepartmentId));
        var sessao = (await s.C.SessaoAsync(item.SessionId))!;
        Assert.Equal((s.Filial, (Guid?)s.SetorDaFilial), (sessao.BusinessUnitId, sessao.DepartmentId));
    }

    [Fact]
    public async Task Transferir_para_fila_de_unidade_que_o_numero_nao_autoriza_e_recusado_e_a_conversa_fica_onde_estava()
    {
        var s = await MontarAsync("unnaoautoriza");
        var item = await AtribuidaAsync(s, s.Matriz);
        var versao = await s.C.VersaoDaSessaoAsync(item.SessionId);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.TransferirParaFilaAsync(item.Id, s.FilaDaOutraFilial.Id, null, s.Ana.Contexto));

        Assert.Equal(ContactCenterRule.UnitNotAuthorizedForChannel, recusa.Rule);
        var depois = (await s.C.ItemAsync(item.Id))!;
        Assert.Equal((s.FilaDaMatriz.Id, s.Matriz, QueueItemStatus.Assigned, s.Ana.Id), (depois.QueueId, depois.BusinessUnitId, depois.Status, depois.AssignedUserId));
        Assert.Null(depois.Pending);
        var sessao = (await s.C.SessaoAsync(item.SessionId))!;
        Assert.Equal((s.Matriz, s.Ana.Id, versao), (sessao.BusinessUnitId, sessao.ResponderRef, sessao.StateVersion));
        Assert.Empty((await s.C.RastroAsync(item.SessionId))!);
        Assert.Equal(0, s.C.EventosDe(ConversationEventTypes.SessionTransferred, item.SessionId));
        var negada = Assert.Single(await s.C.AuditoriaAsync("contact-center.item.transfer", item.Id.ToString()));
        Assert.Equal((AuditOutcome.Denied, s.Ana.Id), (negada.Outcome, negada.ActorUserId));
    }

    [Fact]
    public async Task A_IA_nao_escolhe_unidade_por_fora_da_regra_o_pedido_de_atendimento_passa_pela_mesma_validacao()
    {
        var s = await MontarAsync("unia");
        var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);

        // O sistema (o Agents) pede uma fila de OUTRA unidade, até a que o número não autoriza: a fila não atende a conversa.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.PedirAtendimentoAsync(sessao, s.FilaDaOutraFilial.Id));

        Assert.Equal(ContactCenterRule.QueueDoesNotCoverSession, recusa.Rule);
        Assert.Equal((HandlingMode.AiAutonomous, s.Matriz), ((await s.C.SessaoAsync(sessao))!.HandlingMode, (await s.C.SessaoAsync(sessao))!.BusinessUnitId));
        Assert.Empty(await s.C.ItensAsync());
    }

    [Fact]
    public async Task Com_o_Channels_indisponivel_a_transferencia_de_unidade_fica_pendente_nada_muda_e_repetir_depois_conclui()
    {
        var s = await MontarAsync("unfora");
        var item = await AtribuidaAsync(s, s.Matriz);
        var versao = await s.C.VersaoDaSessaoAsync(item.SessionId);
        var pedido = Guid.NewGuid();

        // Sem confirmar a autorização do número, a unidade não é aplicada nem recusada: a operação espera, com a causa anotada.
        var pendente = await s.Fora.TransferirParaFilaAsync(item.Id, s.FilaDaFilial.Id, null, s.Ana.Contexto, pedido);

        Assert.Equal(OperationOutcome.Pending, pendente.Outcome);
        Assert.Equal((PendingKind.TransferQueue, 1), (pendente.Item.Pending!.Kind, pendente.Item.Pending.Attempts));
        Assert.False(string.IsNullOrEmpty(pendente.Item.Pending.LastError));
        var lido = (await s.C.ItemAsync(item.Id))!;
        Assert.Equal((s.FilaDaMatriz.Id, s.Matriz, QueueItemStatus.Assigned), (lido.QueueId, lido.BusinessUnitId, lido.Status));
        var sessao = (await s.C.SessaoAsync(item.SessionId))!;
        Assert.Equal((s.Matriz, s.Ana.Id, versao), (sessao.BusinessUnitId, sessao.ResponderRef, sessao.StateVersion));
        Assert.Equal(0, s.Fora.EventosDe(ConversationEventTypes.SessionTransferred, item.SessionId));
        Assert.Equal(0, s.C.EventosDe(ConversationEventTypes.SessionTransferred, item.SessionId));

        // Enquanto está pendente, outro pedido na mesma conversa é conflito.
        await Assert.ThrowsAsync<QueueItemConflictException>(() => s.C.DevolverAFilaAsync(item.Id, s.Ana.Contexto));

        // O Channels volta: o mesmo pedido conclui, uma vez só.
        var concluida = await s.C.TransferirParaFilaAsync(item.Id, s.FilaDaFilial.Id, null, s.Ana.Contexto, pedido);

        Assert.Equal(OperationOutcome.Completed, concluida.Outcome);
        Assert.Equal((s.FilaDaFilial.Id, s.Filial, QueueItemStatus.Waiting, null), (concluida.Item.QueueId, concluida.Item.BusinessUnitId, concluida.Item.Status, concluida.Item.Pending));
        Assert.Equal(s.Filial, (await s.C.SessaoAsync(item.SessionId))!.BusinessUnitId);
        Assert.Equal(1, s.C.EventosDe(ConversationEventTypes.SessionTransferred, item.SessionId));
        Assert.Equal(1, (await s.C.HistoricoDoItemAsync(item.Id))!.Count(h => h.Kind == HistoryKind.TransferredQueue));
    }

    [Fact]
    public async Task A_reconciliacao_tambem_respeita_a_autorizacao_do_numero_e_cancela_a_operacao_recusada()
    {
        var s = await MontarAsync("unrec");
        var item = await AtribuidaAsync(s, s.Matriz);
        var basis = await s.C.BaseDaSessaoAsync(item.SessionId);
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            (await itens.FindItemAsync(item.Id, default))!.BeginTransferToQueue(
                s.FilaDaOutraFilial.Id, null, s.OutraFilial, null, Guid.NewGuid(), null, "transfer", DateTimeOffset.UtcNow, basis: basis);
            await itens.SaveChangesAsync(default);
            return 0;
        });

        Assert.Equal(0, await s.C.ReconciliarAsync());

        var depois = (await s.C.ItemAsync(item.Id))!;
        Assert.Equal((s.FilaDaMatriz.Id, s.Matriz, s.Ana.Id, null), (depois.QueueId, depois.BusinessUnitId, depois.AssignedUserId, depois.Pending));
        Assert.Equal(s.Matriz, (await s.C.SessaoAsync(item.SessionId))!.BusinessUnitId);
        Assert.Equal(0, s.C.EventosDe(ConversationEventTypes.SessionTransferred, item.SessionId));
    }

    [Fact]
    public async Task A_operacao_que_nao_conclui_recua_e_nao_impede_o_job_de_chegar_as_pendencias_seguintes()
    {
        var s = await MontarAsync("unfome");

        // Três transferências de unidade, as mais antigas, que o Channels indisponível não deixa concluir...
        var emFalha = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var sessao = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);
            var item = await s.C.NaFilaAsync(sessao, s.FilaDaMatriz.Id);
            var inicio = DateTimeOffset.UtcNow.AddMinutes(-10 + i);
            var basis = await s.C.BaseDaSessaoAsync(sessao);
            await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
            {
                var itens = sp.GetRequiredService<IQueueItemRepository>();
                (await itens.FindItemAsync(item.Id, default))!.BeginTransferToQueue(s.FilaDaFilial.Id, null, s.Filial, null, Guid.NewGuid(), null, "transfer", inicio, basis: basis);
                await itens.SaveChangesAsync(default);
                return 0;
            });
            emFalha.Add(item.Id);
        }

        // ...e uma atribuição mais nova, que concluiria sem o Channels.
        var sessaoBoa = await s.C.NovaConversaAsync(unidade: s.Matriz, canal: s.Canal);
        var boa = await s.C.NaFilaAsync(sessaoBoa, s.FilaDaMatriz.Id);
        await s.C.GravarIntencaoDeAssumirAsync(boa.Id, s.Ana.Id);

        // O job pega uma pendência por ciclo: a que falha recua e deixa de ser a primeira da fila, em vez de ocupar o lote para sempre.
        for (var ciclo = 0; ciclo < 4; ciclo++)
        {
            await s.Fora.ReconciliarAsync(lote: 1);
        }

        var concluida = (await s.C.ItemAsync(boa.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Ana.Id, null), (concluida.Status, concluida.AssignedUserId, concluida.Pending));
        foreach (var id in emFalha)
        {
            var pendente = (await s.C.ItemAsync(id))!.Pending;
            Assert.NotNull(pendente);
            Assert.Equal(1, pendente.Attempts);
        }
    }

    [Fact]
    public async Task Transferir_entre_filas_da_organizacao_inteira_nao_mexe_na_unidade_da_sessao()
    {
        var s = await MontarAsync("unorg");
        var geral = await s.C.CriarFilaAsync("Geral");
        var item = await AtribuidaAsync(s, s.Matriz);

        var transferido = await s.C.TransferirParaFilaAsync(item.Id, geral.Id, null, s.Ana.Contexto);

        Assert.Equal((geral.Id, s.Matriz), (transferido.Item.QueueId, transferido.Item.BusinessUnitId));
        Assert.Equal(s.Matriz, (await s.C.SessaoAsync(item.SessionId))!.BusinessUnitId);
        Assert.Empty((await s.C.RastroAsync(item.SessionId))!);
    }
}
