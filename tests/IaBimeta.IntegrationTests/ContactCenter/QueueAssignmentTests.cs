using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// Atribuição, transferência entre atendentes e filas, devolução à fila e à IA, e a disputa por uma conversa. O estado de atendimento
/// é conferido no Conversations (o responsável e a <c>StateVersion</c>) e o rastro, no histórico do ContactCenter. Uma conversa pode
/// esperar sem atendente, mas nunca tem dois responsáveis.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueAssignmentTests(PostgresFixture banco)
{
    [Fact]
    public async Task O_atendente_da_fila_assume_a_conversa_e_o_Conversations_passa_a_ter_ele_como_responsavel()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atassume");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var pedido = Guid.NewGuid();

        var resultado = await c.AssumirAsync(item.Id, ana.Contexto, pedido);

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), (resultado.Item.Status, resultado.Item.AssignedUserId));
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, ana.Id, 3L), (estado.HandlingMode, estado.ResponderRef, estado.StateVersion));
        var historico = (await c.HistoricoDoItemAsync(item.Id))!;
        Assert.Equal(new[] { HistoryKind.Entered, HistoryKind.Assigned }, historico.Select(h => h.Kind).ToArray());
        var assumiu = historico[1];
        Assert.Equal(((Guid?)null, (Guid?)ana.Id, (Guid?)ana.Id, pedido, 3L), (assumiu.FromUserId, assumiu.ToUserId, assumiu.ActorUserId, assumiu.OperationId, assumiu.StateVersionAfter));
        var evento = Assert.IsType<HandlingModeChangedV1>(c.PayloadsDe(ConversationEventTypes.HandlingModeChanged, sessao).Last());
        Assert.Equal((HandlingMode.HumanAssisted, ana.Id, pedido), (evento.To, evento.ResponderRef, evento.TransitionId));
        var auditoria = Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));
        Assert.Equal((AuditOutcome.Success, ana.Id), (auditoria.Outcome, auditoria.ActorUserId));
    }

    [Fact]
    public async Task Repetir_o_mesmo_pedido_de_assumir_nao_repete_o_efeito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atrepete");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedido = Guid.NewGuid();

        var primeiro = await c.AssumirAsync(item.Id, ana.Contexto, pedido);
        var segundo = await c.AssumirAsync(item.Id, ana.Contexto, pedido);
        var outroPedido = await c.AssumirAsync(item.Id, ana.Contexto);

        Assert.Equal((OperationOutcome.Completed, OperationOutcome.AlreadyApplied, OperationOutcome.AlreadyApplied), (primeiro.Outcome, segundo.Outcome, outroPedido.Outcome));
        Assert.Equal(2, (await c.HistoricoDoItemAsync(item.Id))!.Count);
        Assert.Equal(2, c.EventosDe(ConversationEventTypes.HandlingModeChanged, item.SessionId));
    }

    [Fact]
    public async Task Outro_atendente_nao_assume_conversa_que_ja_e_de_alguem_e_a_conversa_nao_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atjaatribuida");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirAsync(item.Id, beto.Contexto));

        Assert.Equal(QueueItemConflict.AlreadyAssigned, conflito.Conflict);
        Assert.Equal(ana.Id, (await c.ItemAsync(item.Id))!.AssignedUserId);
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task Varios_atendentes_assumindo_ao_mesmo_tempo_so_um_vence_e_a_conversa_nunca_tem_dois_responsaveis()
    {
        for (var rodada = 0; rodada < 4; rodada++)
        {
            var c = await CenarioDeConversas.CriarAsync(banco, $"atdisputa{rodada}");
            var fila = await c.CriarFilaAsync();
            var agentes = new List<Agente>();
            for (var i = 0; i < 8; i++)
            {
                agentes.Add(await c.AgenteNaFilaAsync(banco, fila.Id));
            }

            var sessao = await c.NovaConversaAsync();
            var item = await c.NaFilaAsync(sessao, fila.Id);

            // Todos disparam juntos, cada um em uma "requisição" (escopo, conexão e contexto) própria.
            using var largada = new ManualResetEventSlim(false);
            var disputas = agentes.Select(a => Task.Run(async () =>
            {
                largada.Wait();
                try
                {
                    return (Agente: a, Resultado: (QueueItemOperationResult?)await c.AssumirAsync(item.Id, a.Contexto), Erro: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Agente: a, Resultado: null, Erro: ex);
                }
            })).ToList();
            largada.Set();
            var resultados = await Task.WhenAll(disputas);

            var vencedores = resultados.Where(r => r.Resultado is { Outcome: OperationOutcome.Completed }).ToList();
            var vencedor = Assert.Single(vencedores).Agente;
            // Os outros recebem conflito (nunca um segundo sucesso, nunca erro inesperado).
            foreach (var perdedor in resultados.Where(r => r.Agente != vencedor))
            {
                var conflito = Assert.IsType<QueueItemConflictException>(perdedor.Erro);
                Assert.Equal(QueueItemConflict.AlreadyAssigned, conflito.Conflict);
            }

            // O ContactCenter e o Conversations concordam: um responsável só, o vencedor.
            Assert.Equal(vencedor.Id, (await c.ItemAsync(item.Id))!.AssignedUserId);
            Assert.Equal(vencedor.Id, (await c.SessaoAsync(sessao))!.ResponderRef);
            Assert.Equal(new[] { HistoryKind.Entered, HistoryKind.Assigned }, (await c.HistoricoDoItemAsync(item.Id))!.Select(h => h.Kind).ToArray());
            Assert.Equal(2, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
            Assert.Equal(3L, await c.VersaoDaSessaoAsync(sessao));
            Assert.Null((await c.ItemAsync(item.Id))!.Pending);
        }
    }

    [Fact]
    public async Task O_mesmo_atendente_clicando_duas_vezes_ao_mesmo_tempo_com_o_mesmo_pedido_so_atribui_uma_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atduplo");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var pedido = Guid.NewGuid();

        using var largada = new ManualResetEventSlim(false);
        var cliques = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            largada.Wait();
            try
            {
                return (Resultado: (QueueItemOperationResult?)await c.AssumirAsync(item.Id, ana.Contexto, pedido), Erro: (Exception?)null);
            }
            catch (QueueItemConflictException ex)
            {
                // Uma tentativa que perde a disputa pela versão do item, enquanto outra com o mesmo pedido a conclui, é só conflito.
                return (Resultado: null, Erro: ex);
            }
        })).ToList();
        largada.Set();
        var resultados = await Task.WhenAll(cliques);

        Assert.Contains(resultados, r => r.Resultado is { Outcome: OperationOutcome.Completed or OperationOutcome.AlreadyApplied });
        Assert.DoesNotContain(resultados, r => r.Resultado is { Outcome: OperationOutcome.Pending });
        Assert.Equal(ana.Id, (await c.ItemAsync(item.Id))!.AssignedUserId);
        Assert.Equal(new[] { HistoryKind.Entered, HistoryKind.Assigned }, (await c.HistoricoDoItemAsync(item.Id))!.Select(h => h.Kind).ToArray());
        Assert.Equal(2, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    [Fact]
    public async Task O_supervisor_atribui_uma_conversa_em_espera_a_um_atendente_da_fila_e_um_atendente_comum_nao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atsupervisor");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);

        // Um atendente comum não atribui conversa em espera a outro: nega, audita e nada muda.
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.AtribuirAsync(item.Id, beto.Id, ana.Contexto));
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(item.Id))!.Status);
        var negada = Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));
        Assert.Equal((AuditOutcome.Denied, ana.Id), (negada.Outcome, negada.ActorUserId));

        var atribuida = await c.AtribuirAsync(item.Id, ana.Id, supervisor.Contexto);

        Assert.Equal((OperationOutcome.Completed, ana.Id), (atribuida.Outcome, atribuida.Item.AssignedUserId));
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
        Assert.Equal(supervisor.Id, (await c.HistoricoDoItemAsync(item.Id))!.Last().ActorUserId);
    }

    [Fact]
    public async Task Atribuir_a_quem_nao_esta_na_fila_e_recusado_e_nada_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atnaomembro");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var forasteiro = await c.NovoAgenteAsync(banco);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.AtribuirAsync(item.Id, forasteiro.Id, supervisor.Contexto));

        Assert.Equal(ContactCenterRule.TargetNotQueueMember, recusa.Rule);
        Assert.Equal((QueueItemStatus.Waiting, null), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.AssignedUserId));
        Assert.Null((await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task O_responsavel_passa_a_conversa_para_outro_atendente_e_o_rastro_guarda_de_quem_para_quem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atreatribui");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var carla = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        // Quem não é o responsável nem supervisiona não mexe na conversa de outro.
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.AtribuirAsync(item.Id, carla.Id, beto.Contexto));
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);

        var passada = await c.AtribuirAsync(item.Id, beto.Id, ana.Contexto);

        Assert.Equal(beto.Id, passada.Item.AssignedUserId);
        Assert.Equal(beto.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
        var rastro = (await c.HistoricoDoItemAsync(item.Id))!.Last();
        Assert.Equal((HistoryKind.Reassigned, ana.Id, beto.Id, ana.Id), (rastro.Kind, rastro.FromUserId, rastro.ToUserId, rastro.ActorUserId));
    }

    [Fact]
    public async Task Devolver_a_fila_deixa_a_conversa_esperando_sem_responsavel_e_outro_atendente_pode_assumi_la()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atdevolve");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.DevolverAFilaAsync(item.Id, beto.Contexto));
        var devolvida = await c.DevolverAFilaAsync(item.Id, ana.Contexto);

        Assert.Equal((QueueItemStatus.Waiting, null), (devolvida.Item.Status, devolvida.Item.AssignedUserId));
        Assert.Null((await c.SessaoAsync(item.SessionId))!.ResponderRef);
        var novo = await c.AssumirAsync(item.Id, beto.Contexto);
        Assert.Equal(beto.Id, novo.Item.AssignedUserId);
        Assert.Equal(
            new[] { HistoryKind.Entered, HistoryKind.Assigned, HistoryKind.Released, HistoryKind.Assigned },
            (await c.HistoricoDoItemAsync(item.Id))!.Select(h => h.Kind).ToArray());
    }

    [Fact]
    public async Task Devolver_a_conversa_em_espera_e_repeticao_sem_efeito()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atdevolveespera");
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);

        var resultado = await c.DevolverAFilaAsync(item.Id, supervisor.Contexto);

        Assert.Equal(OperationOutcome.AlreadyApplied, resultado.Outcome);
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!);
    }

    [Fact]
    public async Task Devolver_a_IA_volta_o_modo_da_conversa_encerra_o_item_e_a_IA_volta_a_poder_responder()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atdevolveia");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.DevolverAIAAsync(item.Id, beto.Contexto));
        Assert.Equal(HandlingMode.HumanAssisted, (await c.SessaoAsync(sessao))!.HandlingMode);

        var devolvido = await c.DevolverAIAAsync(item.Id, ana.Contexto);

        Assert.Equal((QueueItemStatus.Closed, "returned-to-ai"), (devolvido.Item.Status, devolvido.Item.CloseReason));
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.AiAutonomous, (Guid?)null, 4L), (estado.HandlingMode, estado.ResponderRef, estado.StateVersion));
        Assert.Equal(BeginTurnOutcome.Granted, (await c.IniciarTurnoAsync(sessao, Guid.NewGuid())).Outcome);
        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirAsync(item.Id, beto.Contexto));
        Assert.Equal(QueueItemConflict.ItemClosed, conflito.Conflict);
    }

    [Fact]
    public async Task Transferir_para_outra_fila_da_mesma_organizacao_volta_a_espera_na_fila_nova()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atfila");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        var transferido = await c.TransferirParaFilaAsync(item.Id, filaB.Id, null, ana.Contexto);

        Assert.Equal((filaB.Id, QueueItemStatus.Waiting, null), (transferido.Item.QueueId, transferido.Item.Status, transferido.Item.AssignedUserId));
        Assert.Null((await c.SessaoAsync(sessao))!.ResponderRef);
        var rastro = (await c.HistoricoDoItemAsync(item.Id))!.Last();
        Assert.Equal((HistoryKind.TransferredQueue, filaA.Id, filaB.Id, ana.Id), (rastro.Kind, rastro.FromQueueId, rastro.ToQueueId, rastro.ActorUserId));
        Assert.Single(await c.AuditoriaAsync("contact-center.item.transfer", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Transferir_com_atendente_de_destino_atribui_a_ele_e_o_destino_precisa_estar_na_fila_nova()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atfilauser");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var supervisor = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var beto = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var soDaA = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);

        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.TransferirParaFilaAsync(item.Id, filaB.Id, soDaA.Id, supervisor.Contexto));
        Assert.Equal(ContactCenterRule.TargetNotQueueMember, recusa.Rule);
        Assert.Equal(filaA.Id, (await c.ItemAsync(item.Id))!.QueueId);

        var transferido = await c.TransferirParaFilaAsync(item.Id, filaB.Id, beto.Id, supervisor.Contexto);

        Assert.Equal((filaB.Id, QueueItemStatus.Assigned, beto.Id), (transferido.Item.QueueId, transferido.Item.Status, transferido.Item.AssignedUserId));
        Assert.Equal(beto.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task Transferir_para_fila_pausada_ou_de_outra_organizacao_ou_a_mesma_fila_e_recusado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atfilarecusa");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var filaA = await c.CriarFilaAsync("Fila A");
        var pausada = await c.CriarFilaAsync("Pausada");
        var daOutra = await c.CriarFilaAsync("Da outra", organizacao: outra.Id);
        await c.PausarAsync(pausada.Id);
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        Assert.Equal(ContactCenterRule.QueuePaused, (await Assert.ThrowsAsync<ContactCenterRuleException>(() => c.TransferirParaFilaAsync(item.Id, pausada.Id, null, ana.Contexto))).Rule);
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.TransferirParaFilaAsync(item.Id, daOutra.Id, null, ana.Contexto));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => c.TransferirParaFilaAsync(item.Id, Guid.NewGuid(), null, ana.Contexto));
        Assert.Equal(QueueItemConflict.NothingToChange, (await Assert.ThrowsAsync<QueueItemConflictException>(() => c.TransferirParaFilaAsync(item.Id, filaA.Id, null, ana.Contexto))).Conflict);

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((filaA.Id, ana.Id), (depois.QueueId, depois.AssignedUserId));
    }

    [Fact]
    public async Task Quem_nao_e_o_responsavel_nem_supervisor_nao_transfere_a_conversa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "attransfnega");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.TransferirParaFilaAsync(item.Id, filaB.Id, null, beto.Contexto));

        Assert.Equal((filaA.Id, ana.Id), ((await c.ItemAsync(item.Id))!.QueueId, (await c.ItemAsync(item.Id))!.AssignedUserId));
        var negada = Assert.Single(await c.AuditoriaAsync("contact-center.item.transfer", item.Id.ToString()));
        Assert.Equal((AuditOutcome.Denied, beto.Id), (negada.Outcome, negada.ActorUserId));
    }

    [Fact]
    public async Task Operacao_com_outro_pedido_em_andamento_na_mesma_conversa_e_conflito_e_so_o_mesmo_pedido_a_conclui()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atemandamento");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var pedidoDaAna = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        // O estado do item mostra a operação pendente; a conversa continua como estava (humano pendente, sem responsável).
        var pendente = (await c.ItemAsync(item.Id))!;
        Assert.Equal((PendingKind.Assign, ana.Id), (pendente.Pending!.Kind, pendente.Pending.UserId));
        Assert.Null((await c.SessaoAsync(item.SessionId))!.ResponderRef);

        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => c.AssumirAsync(item.Id, beto.Contexto));
        Assert.Equal(QueueItemConflict.AlreadyAssigned, conflito.Conflict);
        Assert.Null((await c.SessaoAsync(item.SessionId))!.ResponderRef);

        // A Ana repete o pedido dela (a requisição caiu antes de concluir) e a operação se conclui.
        var concluida = await c.AssumirAsync(item.Id, ana.Contexto, pedidoDaAna);
        Assert.Equal((OperationOutcome.Completed, ana.Id), (concluida.Outcome, concluida.Item.AssignedUserId));
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }

    [Fact]
    public async Task Mudanca_de_versao_do_estado_da_sessao_no_meio_nao_atrapalha_porque_cada_passo_relê_a_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atversao");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        var pedido = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        // Alguém muda o idioma da conversa depois da intenção: a StateVersion avança por fora do ContactCenter.
        await c.AlterarIdiomaAsync(sessao, "en-US", await c.VersaoDaSessaoAsync(sessao));
        Assert.Equal(3L, await c.VersaoDaSessaoAsync(sessao));

        var concluida = await c.AssumirAsync(item.Id, ana.Contexto, pedido);

        Assert.Equal(OperationOutcome.Completed, concluida.Outcome);
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((ana.Id, 4L), (estado.ResponderRef, estado.StateVersion));
    }

    [Fact]
    public async Task Cada_negacao_de_atribuicao_e_transferencia_fica_na_auditoria_com_o_autor_e_nada_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "atauditoria");
        var filaA = await c.CriarFilaAsync("Fila A");
        var filaB = await c.CriarFilaAsync("Fila B");
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), filaA.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.AtribuirAsync(item.Id, beto.Id, beto.Contexto));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.DevolverAFilaAsync(item.Id, beto.Contexto));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.TransferirParaFilaAsync(item.Id, filaB.Id, null, beto.Contexto));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => c.DevolverAIAAsync(item.Id, beto.Contexto));

        foreach (var operacao in new[] { "assign", "release", "transfer", "return-to-ai" })
        {
            var negada = Assert.Single(await c.AuditoriaAsync($"contact-center.item.{operacao}", item.Id.ToString()), e => e.Outcome == AuditOutcome.Denied);
            Assert.Equal(beto.Id, negada.ActorUserId);
        }

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id, filaA.Id), (depois.Status, depois.AssignedUserId, depois.QueueId));
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef);
    }
}
