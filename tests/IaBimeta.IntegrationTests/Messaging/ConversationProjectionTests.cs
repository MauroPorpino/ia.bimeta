using System.Text.Json;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Messaging;

/// <summary>
/// O primeiro processamento assíncrono durável (BOUNDARIES.md §12.12, etapa 4), de ponta a ponta sobre PostgreSQL real: o Conversations
/// grava o evento no outbox na mesma transação da mudança de estado, o relay o entrega, e o ContactCenter o consome pelo inbox
/// idempotente, mantendo a projeção da fila coerente quando o estado muda POR FORA dele. O tenant do handler vem do envelope, nunca do
/// payload.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationProjectionTests(PostgresFixture banco)
{
    private async Task<CenarioDeConversas> NovoAsync(string prefixo)
    {
        banco.RelogioDaMensageria.Sincronizar();
        return await CenarioDeConversas.CriarAsync(banco, prefixo, mensageria: true);
    }

    private sealed record Cena(CenarioDeConversas C, ServiceQueueView Fila, Agente Ana, Agente Beto, Guid Sessao, QueueItemView Item);

    /// <summary>Uma conversa que entrou na fila e foi assumida pela Ana, com o relay já rodado (as operações próprias entregues).</summary>
    private async Task<Cena> ComItemDaAnaAsync(string prefixo)
    {
        var c = await NovoAsync(prefixo);
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        await c.RodarRelayAsync();
        return new Cena(c, fila, ana, beto, sessao, (await c.ItemAsync(item.Id))!);
    }

    // O evento nasce com a mudança de estado

    [Fact]
    public async Task A_mudanca_de_estado_grava_o_evento_no_outbox_na_mesma_transacao_com_tenant_chave_e_contrato()
    {
        var c = await NovoAsync("pjoutbox");
        var sessao = (await c.EntrarAsync("5511999990201")).SessionId;
        var antes = (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Count;
        var transicao = Guid.NewGuid();

        var resultado = await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, 1, transicao);

        Assert.Equal(IaBimeta.Conversations.Contracts.Commands.TransitionOutcome.Applied, resultado.Outcome);
        var linha = (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Last();
        Assert.Equal(antes + 1, (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Count);
        Assert.Equal(
            (c.Tenant.TenantId.Value, $"session:{sessao}", ConversationEventTypes.HandlingModeChanged, 1, OutboxState.Pending),
            (linha.TenantId, linha.OrderingKey, linha.EventType, linha.EventVersion, linha.State));
        using var json = JsonDocument.Parse(linha.Payload);
        Assert.Equal(
            (sessao, "AiAutonomous", "HumanAssisted", 2L, transicao),
            (json.RootElement.GetProperty("sessionId").GetGuid(), json.RootElement.GetProperty("from").GetString(), json.RootElement.GetProperty("to").GetString(),
                json.RootElement.GetProperty("stateVersion").GetInt64(), json.RootElement.GetProperty("transitionId").GetGuid()));
    }

    [Fact]
    public async Task Mudanca_recusada_ou_repetida_nao_deixa_evento_no_outbox()
    {
        var c = await NovoAsync("pjsemevento");
        var sessao = (await c.EntrarAsync("5511999990202")).SessionId;
        var transicao = Guid.NewGuid();
        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, 1, transicao);
        var antes = (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Count;

        var defasada = await c.TransicionarAsync(sessao, HandlingMode.AiAutonomous, null, 1);          // versão esperada velha
        var repetida = await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, 1, transicao); // mesmo TransitionId

        Assert.Equal(IaBimeta.Conversations.Contracts.Commands.TransitionOutcome.StaleState, defasada.Outcome);
        Assert.Equal(IaBimeta.Conversations.Contracts.Commands.TransitionOutcome.AlreadyApplied, repetida.Outcome);
        Assert.Equal(antes, (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Count);
    }

    [Fact]
    public async Task O_publicador_em_memoria_continua_recebendo_os_eventos_depois_da_confirmacao_como_no_desenvolvimento()
    {
        var c = await NovoAsync("pjmemoria");
        var sessao = (await c.EntrarAsync("5511999990203")).SessionId;

        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, 1);

        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    // O ContactCenter consome

    [Fact]
    public async Task Os_eventos_das_proprias_operacoes_do_ContactCenter_nao_duplicam_nem_alteram_o_item_e_ficam_no_inbox()
    {
        var c = await NovoAsync("pjproprios");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.AssumirAsync(item.Id, ana.Contexto);
        var historicoAntes = (await c.HistoricoDoItemAsync(item.Id))!.Select(h => (h.Kind, h.OperationId)).ToList();

        await c.RodarRelayAsync();

        var linhas = await c.LinhasDoOutboxDaSessaoAsync(sessao);
        Assert.All(linhas, l => Assert.Equal(OutboxState.Processed, l.State));
        var transicoes = linhas.Where(l => l.EventType == ConversationEventTypes.HandlingModeChanged).ToList();
        Assert.Equal(2, transicoes.Count); // a entrada na fila e a atribuição
        foreach (var linha in transicoes)
        {
            Assert.Equal(1, await c.RegistrosDoInboxAsync(linha.Id));
        }

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), (depois.Status, depois.AssignedUserId));
        Assert.Equal(historicoAntes, (await c.HistoricoDoItemAsync(item.Id))!.Select(h => (h.Kind, h.OperationId)).ToList());
    }

    [Fact]
    public async Task O_responsavel_mudado_por_fora_no_Conversations_e_refletido_no_item_com_o_rastro_sem_autor()
    {
        var s = await ComItemDaAnaAsync("pjfora");

        // Alguém muda o responsável direto no Conversations, sem passar pelo ContactCenter.
        await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, s.Beto.Id, await s.C.VersaoDaSessaoAsync(s.Sessao));
        var evento = (await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).Last();
        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Beto.Id), (item.Status, item.AssignedUserId));
        var rastro = (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Last();
        Assert.Equal((HistoryKind.Reassigned, s.Ana.Id, s.Beto.Id, (Guid?)null, "external-change"), (rastro.Kind, rastro.FromUserId, rastro.ToUserId, rastro.ActorUserId, rastro.Reason));
        // A chave de idempotência do registro é a transição do Conversations (única por transição), não o id do evento.
        using var corpo = JsonDocument.Parse(evento.Payload);
        Assert.Equal(corpo.RootElement.GetProperty("transitionId").GetGuid(), rastro.OperationId);
        Assert.Equal(1, await s.C.RegistrosDoInboxAsync(evento.Id));
        Assert.Single(await s.C.AuditoriaAsync("contact-center.item.external-change", s.Item.Id.ToString()));
    }

    [Fact]
    public async Task O_atendimento_pendente_sem_responsavel_mudado_por_fora_devolve_o_item_a_espera()
    {
        var s = await ComItemDaAnaAsync("pjforaespera");

        await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, null, await s.C.VersaoDaSessaoAsync(s.Sessao));
        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Waiting, null), (item.Status, item.AssignedUserId));
        Assert.Equal(HistoryKind.Released, (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Last().Kind);
    }

    [Fact]
    public async Task A_conversa_devolvida_a_IA_por_fora_encerra_o_item_com_o_rastro()
    {
        var s = await ComItemDaAnaAsync("pjforaia");

        await s.C.TransicionarAsync(s.Sessao, HandlingMode.AiAutonomous, null, await s.C.VersaoDaSessaoAsync(s.Sessao));
        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Closed, "returned-to-ai", null), (item.Status, item.CloseReason, item.AssignedUserId));
        var rastro = (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Last();
        Assert.Equal((HistoryKind.ReturnedToAi, s.Ana.Id, (Guid?)null, "external-change"), (rastro.Kind, rastro.FromUserId, rastro.ActorUserId, rastro.Reason));
        Assert.Empty(await s.C.ItensAsync(fila: s.Fila.Id));
    }

    [Fact]
    public async Task A_sessao_encerrada_encerra_o_item_ativo_e_a_repeticao_do_evento_nao_faz_nada()
    {
        var s = await ComItemDaAnaAsync("pjencerra");

        await s.C.EncerrarSessaoAsync(s.Sessao);
        var evento = (await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).Last(l => l.EventType == ConversationEventTypes.SessionClosed);
        await s.C.RodarRelayAsync();

        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Closed, "session-closed"), (item.Status, item.CloseReason));
        var historico = (await s.C.HistoricoDoItemAsync(s.Item.Id))!;
        Assert.Equal((HistoryKind.SessionClosed, evento.Id), (historico.Last().Kind, historico.Last().OperationId));

        // Reentrega do mesmo evento (o relay entrega pelo menos uma vez): o inbox impede qualquer efeito novo.
        await s.C.AlterarOutboxAsync(evento.Id, l =>
        {
            l.State = OutboxState.Pending;
            l.Attempts = 0;
            l.ProcessedAt = null;
        });
        await s.C.RodarRelayAsync();

        Assert.Equal(historico.Count, (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Count);
        Assert.Equal(1, await s.C.RegistrosDoInboxAsync(evento.Id));
        Assert.Equal(OutboxState.Processed, (await s.C.LinhaDoOutboxAsync(evento.Id))!.State);
    }

    [Fact]
    public async Task A_reentrega_de_um_evento_ja_tratado_nao_repete_o_efeito_no_item()
    {
        var s = await ComItemDaAnaAsync("pjreentrega");
        await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, s.Beto.Id, await s.C.VersaoDaSessaoAsync(s.Sessao));
        var evento = (await s.C.LinhasDoOutboxDaSessaoAsync(s.Sessao)).Last();
        await s.C.RodarRelayAsync();
        var historico = (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Count;
        var versao = (await s.C.ItemAsync(s.Item.Id))!.Version;

        for (var i = 0; i < 3; i++)
        {
            await s.C.AlterarOutboxAsync(evento.Id, l =>
            {
                l.State = OutboxState.Pending;
                l.Attempts = 0;
                l.ProcessedAt = null;
            });
            await s.C.RodarRelayAsync();
        }

        Assert.Equal(historico, (await s.C.HistoricoDoItemAsync(s.Item.Id))!.Count);
        Assert.Equal(versao, (await s.C.ItemAsync(s.Item.Id))!.Version);
        Assert.Equal(1, await s.C.RegistrosDoInboxAsync(evento.Id));
    }

    [Fact]
    public async Task Evento_atrasado_com_versao_do_estado_menor_nao_regride_a_projecao()
    {
        var s = await ComItemDaAnaAsync("pjatrasado");
        // Um evento ANTIGO (versão 1 do estado) que chega depois: a projeção só avança, então ele não muda nada.
        var antigo = Guid.NewGuid();
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            var agora = sp.GetRequiredService<TimeProvider>().GetUtcNow();
            sp.GetRequiredService<IOutboxWriter>().Stage(
                db,
                new[]
                {
                    new EventEnvelope(
                        antigo, ConversationEventTypes.HandlingModeChanged, 1, s.C.Tenant.TenantId, Guid.NewGuid(), null, $"session:{s.Sessao}", agora,
                        new HandlingModeChangedV1(s.Sessao, HandlingMode.AiAutonomous, HandlingMode.HumanAssisted, s.Beto.Id, 1, Guid.NewGuid())),
                },
                s.C.Tenant.TenantId,
                agora);
            await db.SaveChangesAsync();
            return 0;
        });

        await s.C.RodarRelayAsync();

        Assert.Equal((OutboxState.Processed, s.Ana.Id), ((await s.C.LinhaDoOutboxAsync(antigo))!.State, (await s.C.ItemAsync(s.Item.Id))!.AssignedUserId));
        Assert.Equal(1, await s.C.RegistrosDoInboxAsync(antigo));
    }

    [Fact]
    public async Task Evento_de_conversa_que_nao_esta_na_fila_do_ContactCenter_e_ignorado_sem_erro()
    {
        var c = await NovoAsync("pjsemitem");
        var sessao = (await c.EntrarAsync("5511999990204")).SessionId;

        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, Guid.NewGuid(), 1);
        await c.RodarRelayAsync();

        Assert.All(await c.LinhasDoOutboxDaSessaoAsync(sessao), l => Assert.Equal(OutboxState.Processed, l.State));
        Assert.Empty(await c.ItensAsync());
    }

    [Fact]
    public async Task A_transicao_da_operacao_pendente_do_proprio_item_conclui_a_operacao_sem_nova_transicao()
    {
        var c = await NovoAsync("pjpendente");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.RodarRelayAsync();
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        // O Conversations já aplicou a transição da operação (com o identificador dela), mas o ContactCenter caiu antes de concluir.
        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, ana.Id, await c.VersaoDaSessaoAsync(sessao), operacao);
        var versao = await c.VersaoDaSessaoAsync(sessao);
        await c.RodarRelayAsync();

        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id, null), (depois.Status, depois.AssignedUserId, depois.Pending));
        Assert.Equal(versao, await c.VersaoDaSessaoAsync(sessao));
        var historico = (await c.HistoricoDoItemAsync(item.Id))!;
        Assert.Equal((HistoryKind.Assigned, operacao), (historico.Last().Kind, historico.Last().OperationId));
        Assert.Equal(1, historico.Count(h => h.Kind == HistoryKind.Assigned));
    }

    [Fact]
    public async Task Mudanca_externa_com_operacao_pendente_diferente_nao_pisa_na_operacao_que_converge_sozinha()
    {
        var c = await NovoAsync("pjpendentediferente");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id);
        await c.RodarRelayAsync();
        await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);

        await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, beto.Id, await c.VersaoDaSessaoAsync(sessao));
        await c.RodarRelayAsync();

        // O item continua com a intenção da Ana (e em espera): quem converge é a operação, relendo a sessão, e não o evento.
        var depois = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Waiting, PendingKind.Assign, ana.Id), (depois.Status, depois.Pending!.Kind, depois.Pending.UserId));
    }

    // Isolamento por tenant

    [Fact]
    public async Task O_tenant_do_handler_vem_do_envelope_um_evento_de_um_tenant_com_a_sessao_de_outro_no_payload_nao_toca_o_item_do_outro()
    {
        var a = await ComItemDaAnaAsync("pjtenanta");
        var b = await ComItemDaAnaAsync("pjtenantb");
        var versaoDeB = (await b.C.ItemAsync(b.Item.Id))!.Version;

        // Um evento gravado no outbox do tenant A, mas com a sessão do tenant B no payload (dado errado ou malicioso).
        var evento = Guid.NewGuid();
        await a.C.ComoAsync(a.C.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            var agora = sp.GetRequiredService<TimeProvider>().GetUtcNow();
            sp.GetRequiredService<IOutboxWriter>().Stage(
                db,
                new[]
                {
                    new EventEnvelope(
                        evento, ConversationEventTypes.HandlingModeChanged, 1, a.C.Tenant.TenantId, Guid.NewGuid(), null, $"session:{b.Sessao}", agora,
                        new HandlingModeChangedV1(b.Sessao, HandlingMode.HumanAssisted, HandlingMode.AiAutonomous, null, 999, Guid.NewGuid())),
                },
                a.C.Tenant.TenantId,
                agora);
            await db.SaveChangesAsync();
            return 0;
        });

        await a.C.RodarRelayAsync();

        var deB = (await b.C.ItemAsync(b.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, b.Ana.Id, versaoDeB), (deB.Status, deB.AssignedUserId, deB.Version));
        Assert.Equal(OutboxState.Processed, (await a.C.LinhaDoOutboxAsync(evento))!.State);
        // O registro de idempotência fica no tenant do envelope (o A), nunca no do payload.
        Assert.Equal(1, await a.C.RegistrosDoInboxAsync(evento));
    }

    [Fact]
    public async Task O_handler_so_roda_como_sistema_o_usuario_nao_chama_a_projecao()
    {
        var s = await ComItemDaAnaAsync("pjsistema");
        var evento = new HandlingModeChangedV1(s.Sessao, HandlingMode.HumanAssisted, HandlingMode.AiAutonomous, null, 999, Guid.NewGuid());

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.C.ComoAsync(
            s.Ana.Contexto, sp => sp.GetRequiredService<ConversationProjectionService>().ApplyHandlingModeChangedAsync(evento, Guid.NewGuid())));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.C.ComoAsync(
            s.C.GestorDeFilas(), sp => sp.GetRequiredService<ConversationProjectionService>().ApplySessionClosedAsync(new SessionClosedV1(s.Sessao, "manual"), Guid.NewGuid())));
        Assert.Equal(QueueItemStatus.Assigned, (await s.C.ItemAsync(s.Item.Id))!.Status);
    }
}
