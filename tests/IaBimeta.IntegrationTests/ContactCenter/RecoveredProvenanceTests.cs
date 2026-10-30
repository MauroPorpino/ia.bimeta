using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A intenção pendente guarda a PROVENIÊNCIA da operação (o ator, a origem e o instante do pedido). Quando o job de reconciliação conclui ou
/// desfaz a intenção depois de uma queda, a auditoria diz o que o autor pediu, e não o que o job é (sem ator, origem do Worker, hora da
/// recuperação). PostgreSQL real; os pontos de interrupção são determinísticos.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RecoveredProvenanceTests(PostgresFixture banco)
{
    private const string OrigemDoPedido = "session";
    private const string OrigemDoJob = "worker-reconciler";

    /// <summary>O mesmo usuário e as mesmas permissões, mas com a origem de uma requisição de usuário (diferente da do job que recupera depois).</summary>
    private static RequestContext ComOrigem(RequestContext contexto, string origem) => new(contexto.TenantId, origem, contexto.Principal);

    private static Task<int> ReconciliarComoJobAsync(CenarioDeConversas c) =>
        c.ComoAsync(new RequestContext(c.Tenant.TenantId, OrigemDoJob), sp => sp.GetRequiredService<QueueItemService>().ReconcilePendingAsync(TimeSpan.Zero, 50));

    private static Task LiberarRecuoAsync(CenarioDeConversas c, Guid item) => c.ComoAsync(c.ContextoDeSistema, async sp =>
    {
        await sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
            .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET pending_next_attempt_at = NULL WHERE id = {item}");
        return 0;
    });

    private static async Task<QueueItemOperationResult> AtribuirComFalhaAsync(
        CenarioDeConversas c, RequestContext contexto, Guid item, Guid usuario, Guid pedido, Action<Interceptador<IConversationGateway>> falha) =>
        await c.ComoAsync(contexto, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            falha(controle);
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).AssignAsync(item, usuario, pedido);
        });

    private async Task<(CenarioDeConversas C, ServiceQueueView Fila, Agente Supervisor, Agente Ana, QueueItemView Item)> CenaAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var fila = await c.CriarFilaAsync();
        var supervisor = await c.AgenteNaFilaAsync(banco, fila.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        return (c, fila, supervisor, ana, item);
    }

    [Fact]
    public async Task A_operacao_confirmada_no_Conversations_e_concluida_depois_pelo_job_e_auditada_com_o_ator_a_origem_e_a_hora_do_pedido()
    {
        var (c, _, supervisor, ana, item) = await CenaAsync("provconclui");
        var pedido = Guid.NewGuid();
        var antes = DateTimeOffset.UtcNow;

        // O supervisor atribui a conversa à Ana. O Conversations CONFIRMA, e o processo perde a resposta antes de o ContactCenter concluir.
        var interrompida = await AtribuirComFalhaAsync(
            c, ComOrigem(supervisor.Contexto, OrigemDoPedido), item.Id, ana.Id, pedido,
            g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")));
        var depoisDoPedido = DateTimeOffset.UtcNow;
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        Assert.Equal(ana.Id, (await c.SessaoAsync(item.SessionId))!.ResponderRef); // confirmado no Conversations
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(item.Id))!.Status); // e ainda não concluído no ContactCenter
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));

        // Mais tarde, o job (outro contexto, outra origem) conclui a intenção.
        await Task.Delay(100);
        await LiberarRecuoAsync(c, item.Id);
        Assert.Equal(1, await ReconciliarComoJobAsync(c));

        var atual = (await c.ItemAsync(item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), (atual.Status, atual.AssignedUserId));
        var evento = Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));

        // A auditoria é a do PEDIDO: o supervisor, a origem dele e a hora em que pediu; nada do job.
        Assert.Equal((AuditOutcome.Success, supervisor.Id, OrigemDoPedido), (evento.Outcome, evento.ActorUserId, evento.ActorOrigin));
        Assert.NotEqual(OrigemDoJob, evento.ActorOrigin);
        Assert.InRange(evento.OccurredAt, antes, depoisDoPedido);
        // O rastro do item já dizia quem pediu, e continua dizendo.
        Assert.Equal(supervisor.Id, (await c.HistoricoDoItemAsync(item.Id))!.Last(h => h.Kind == HistoryKind.Assigned).ActorUserId);
    }

    [Fact]
    public async Task A_intencao_desfeita_depois_pelo_job_deixa_a_negacao_com_o_ator_a_origem_e_a_hora_do_pedido()
    {
        var (c, fila, supervisor, ana, item) = await CenaAsync("provcancela");
        var pedido = Guid.NewGuid();
        var antes = DateTimeOffset.UtcNow;

        // O Conversations não responde: nada foi aplicado, e a intenção do supervisor fica pendente.
        var interrompida = await AtribuirComFalhaAsync(
            c, ComOrigem(supervisor.Contexto, OrigemDoPedido), item.Id, ana.Id, pedido,
            g => g.Substituir(nameof(IConversationGateway.FenceAsync), _ => Task.FromException<FenceHandlingResult>(new InvalidOperationException("fora do ar"))));
        var depoisDoPedido = DateTimeOffset.UtcNow;
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        // A Ana sai da fila; o job retoma e desfaz a intenção (o destino já não é da fila).
        Assert.True(await c.RemoverAtendenteAsync(fila.Id, ana.Id));
        await Task.Delay(100);
        await LiberarRecuoAsync(c, item.Id);
        Assert.Equal(0, await ReconciliarComoJobAsync(c));
        Assert.Null((await c.ItemAsync(item.Id))!.Pending);

        var negacao = Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));
        Assert.Equal((AuditOutcome.Denied, supervisor.Id, OrigemDoPedido), (negacao.Outcome, negacao.ActorUserId, negacao.ActorOrigin));
        Assert.InRange(negacao.OccurredAt, antes, depoisDoPedido);
    }

    [Fact]
    public async Task A_intencao_pedida_pelo_sistema_guarda_a_origem_e_nenhum_ator_e_a_recuperacao_nao_inventa_um()
    {
        var (c, _, _, ana, item) = await CenaAsync("provsistema");
        var origemDoAgente = "agents";
        var antes = DateTimeOffset.UtcNow;

        // Quem pede é um componente de sistema (sem usuário), com a origem dele; a conclusão vem depois do job.
        var interrompida = await c.ComoAsync(new RequestContext(c.Tenant.TenantId, origemDoAgente), async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            controle.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu"));
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor).AssignAsync(item.Id, ana.Id, Guid.NewGuid());
        });
        var depoisDoPedido = DateTimeOffset.UtcNow;
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        await Task.Delay(100);
        await LiberarRecuoAsync(c, item.Id);
        Assert.Equal(1, await ReconciliarComoJobAsync(c));

        var evento = Assert.Single(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));
        Assert.Equal((AuditOutcome.Success, (Guid?)null, origemDoAgente), (evento.Outcome, evento.ActorUserId, evento.ActorOrigin));
        Assert.InRange(evento.OccurredAt, antes, depoisDoPedido);
    }
}
