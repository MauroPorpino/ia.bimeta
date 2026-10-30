using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A auditoria do que a projeção faz (o consumo dos eventos do Conversations) nunca diz "sucesso" para algo que a transação do inbox
/// desfez: a entrada só chega à trilha depois da confirmação, e fica guardada na mesma transação da projeção para ser entregue mesmo
/// que o processo caia entre a confirmação e a entrega. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectionAuditTests(PostgresFixture banco)
{
    private const string Consumidor = "teste.auditoria";

    private static Task<HandlerResult> ProjetarEDesfazerAsync(IServiceProvider sp, CenarioDeConversas c, Guid evento, Func<ConversationProjectionService, Task> projetar) =>
        sp.GetRequiredService<InboxGate<ContactCenterDbContext>>().RunOnceAsync(
            c.Tenant.TenantId, Consumidor, evento,
            async token =>
            {
                await projetar(sp.GetRequiredService<ConversationProjectionService>());
                return HandlerResult.Retry(reason: "TesteDesfaz"); // a transação do inbox é desfeita depois de a projeção ter rodado
            },
            default);

    [Fact]
    public async Task A_projecao_desfeita_pelo_inbox_nao_deixa_auditoria_de_encerramento_com_sucesso()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "auencerra");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var evento = Guid.NewGuid();

        var resultado = await c.ComoAsync(c.ContextoDeSistema, sp => ProjetarEDesfazerAsync(
            sp, c, evento, projecao => projecao.ApplySessionClosedAsync(new SessionClosedV1(item.SessionId, "manual"), evento)));

        Assert.Equal(HandlerOutcome.Retry, resultado.Outcome);
        Assert.Equal(QueueItemStatus.Waiting, (await c.ItemAsync(item.Id))!.Status);
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.session-closed", item.Id.ToString()));
        Assert.Equal(0, await ContarAuditoriasPendentesAsync(c));
    }

    [Fact]
    public async Task A_conclusao_da_operacao_pendente_desfeita_pelo_inbox_nao_deixa_auditoria_de_atribuicao_com_sucesso()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "auatribui");
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var operacao = await c.GravarIntencaoDeAssumirAsync(item.Id, ana.Id);
        var evento = Guid.NewGuid();

        // O evento da transição da operação pendente: a projeção a conclui (o executor audita), e o inbox desfaz tudo depois.
        await c.ComoAsync(c.ContextoDeSistema, sp => ProjetarEDesfazerAsync(
            sp, c, evento,
            projecao => projecao.ApplyHandlingModeChangedAsync(
                new HandlingModeChangedV1(item.SessionId, HandlingMode.AiAutonomous, HandlingMode.HumanAssisted, ana.Id, 999, operacao), evento)));

        Assert.Empty(await c.AuditoriaAsync("contact-center.item.assign", item.Id.ToString()));
        Assert.Equal(0, await ContarAuditoriasPendentesAsync(c));
    }

    [Fact]
    public async Task A_projecao_confirmada_entrega_a_auditoria_uma_vez_e_nao_deixa_nada_guardado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "auconfirma");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var evento = Guid.NewGuid();
        var envelope = new EventEnvelope(
            evento, ConversationEventTypes.SessionClosed, 1, c.Tenant.TenantId, Guid.NewGuid(), null, $"session:{item.SessionId}", DateTimeOffset.UtcNow,
            new SessionClosedV1(item.SessionId, "manual"));

        var resultado = await c.ComoAsync(c.ContextoDeSistema, sp =>
            sp.GetServices<IEventHandler>().Single(h => h.EventType == ConversationEventTypes.SessionClosed).HandleAsync(envelope, default));

        Assert.Equal(HandlerOutcome.Success, resultado.Outcome);
        Assert.Equal(QueueItemStatus.Closed, (await c.ItemAsync(item.Id))!.Status);
        Assert.Single(await c.AuditoriaAsync("contact-center.item.session-closed", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Equal(0, await ContarAuditoriasPendentesAsync(c));
    }

    [Fact]
    public async Task A_auditoria_guardada_que_nao_chegou_a_trilha_e_entregue_pela_reconciliacao_uma_unica_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "aurecupera");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var evento = Guid.NewGuid();

        // O inbox confirmou a projeção, e o processo caiu antes de entregar a auditoria à trilha (aqui, simplesmente não a entregamos).
        await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<InboxGate<ContactCenterDbContext>>().RunOnceAsync(
            c.Tenant.TenantId, Consumidor, evento,
            async token =>
            {
                await sp.GetRequiredService<ConversationProjectionService>().ApplySessionClosedAsync(new SessionClosedV1(item.SessionId, "manual"), evento, token);
                return HandlerResult.Success;
            },
            default));

        Assert.Equal(QueueItemStatus.Closed, (await c.ItemAsync(item.Id))!.Status);
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.session-closed", item.Id.ToString()));
        Assert.Equal(1, await ContarAuditoriasPendentesAsync(c));

        // A reconciliação entrega; rodar de novo não repete.
        Assert.Equal(1, await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IDeferredAudit>().FlushAsync(50, default)));
        Assert.Equal(0, await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IDeferredAudit>().FlushAsync(50, default)));
        Assert.Single(await c.AuditoriaAsync("contact-center.item.session-closed", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Equal(0, await ContarAuditoriasPendentesAsync(c));
    }

    [Fact]
    public async Task A_entrega_da_auditoria_nao_acontece_de_dentro_de_uma_transacao_aberta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "autransacao");
        var fila = await c.CriarFilaAsync();
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        var evento = Guid.NewGuid();

        var entregues = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            var auditoria = sp.GetRequiredService<IDeferredAudit>();
            await using var transacao = await db.Database.BeginTransactionAsync();
            auditoria.Defer(new AuditEntry("contact-center.item.session-closed", "queue-item", item.Id.ToString(), AuditOutcome.Success));
            await db.SaveChangesAsync();
            var dentro = await auditoria.FlushAsync(50, default); // a transação ainda pode ser desfeita
            await transacao.RollbackAsync();
            return dentro;
        });

        Assert.Equal(0, entregues);
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.session-closed", item.Id.ToString()));
        Assert.Equal(0, await ContarAuditoriasPendentesAsync(c));
    }

    private static Task<int> ContarAuditoriasPendentesAsync(CenarioDeConversas c) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IDeferredAudit>().PendingCountAsync(default));
}
