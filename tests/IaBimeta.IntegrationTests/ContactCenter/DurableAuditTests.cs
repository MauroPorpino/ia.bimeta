using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A auditoria obrigatória de uma conclusão (inclusive a de uma requisição normal) é guardada na MESMA transação que confirma o item, o
/// histórico e o recibo; a entrega à trilha vem depois da confirmação e é idempotente. Uma queda entre a confirmação e a entrega, ou a
/// trilha fora do ar, nunca perde a auditoria nem faz a operação já confirmada falhar. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DurableAuditTests(PostgresFixture banco)
{
    private const string Operacao = "contact-center.item.assign";

    private static Task<int> PendentesAsync(CenarioDeConversas c) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IDeferredAudit>().PendingCountAsync(default));

    private static Task<int> EntregarAsync(CenarioDeConversas c) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IDeferredAudit>().FlushAsync(50, default));

    private const string WorkerOrigin = "worker-reconciler";

    /// <summary>A entrega feita pelo job de reconciliação do Worker: contexto de sistema do tenant, com a origem dele (não a de quem agiu).</summary>
    private static Task<int> EntregarComoWorkerAsync(CenarioDeConversas c) =>
        c.ComoAsync(new RequestContext(c.Tenant.TenantId, WorkerOrigin), sp => sp.GetRequiredService<IDeferredAudit>().FlushAsync(50, default));

    /// <summary>A Ana assume a conversa com a entrega da auditoria à trilha SUBSTITUÍDA por <paramref name="entrega"/>: o que o processo faz (ou não faz) depois de confirmar.</summary>
    private static Task<QueueItemOperationResult> AssumirComAuditoriaAsync(
        CenarioDeConversas c, Agente ana, Guid item, Func<IServiceProvider, IDeferredAudit, IDeferredAudit> entrega) =>
        c.ComoAsync(ana.Contexto, async sp =>
        {
            var auditoria = entrega(sp, sp.GetRequiredService<IDeferredAudit>());
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, auditoria);
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor, auditoria).ClaimAsync(item, Guid.NewGuid());
        });

    private static IDeferredAudit SemEntrega(IServiceProvider sp, IDeferredAudit real)
    {
        var (porta, controle) = Interceptador<IDeferredAudit>.Envolver(real);
        controle.Substituir(nameof(IDeferredAudit.FlushAsync), _ => Task.FromResult(0)); // o processo "morre" depois de confirmar e antes de entregar
        return porta;
    }

    private async Task<(CenarioDeConversas C, Agente Ana, QueueItemView Item)> CenaAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var fila = await c.CriarFilaAsync();
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var item = await c.NaFilaAsync(await c.NovaConversaAsync(), fila.Id);
        return (c, ana, item);
    }

    [Fact]
    public async Task A_queda_depois_de_confirmar_o_estado_e_antes_de_entregar_a_auditoria_nao_a_perde_e_a_reconciliacao_a_entrega_uma_vez()
    {
        var (c, ana, item) = await CenaAsync("auddurqueda");
        var antes = await PendentesAsync(c);

        var resultado = await AssumirComAuditoriaAsync(c, ana, item.Id, SemEntrega);

        // O estado está confirmado (item, histórico e recibo), a trilha ainda não recebeu nada, e a intenção de auditoria ESTÁ guardada.
        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        Assert.Equal((QueueItemStatus.Assigned, ana.Id), ((await c.ItemAsync(item.Id))!.Status, (await c.ItemAsync(item.Id))!.AssignedUserId));
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!, h => h.Kind == HistoryKind.Assigned);
        Assert.Empty(await c.AuditoriaAsync(Operacao, item.Id.ToString()));
        Assert.Equal(antes + 1, await PendentesAsync(c));

        // A recuperação entrega; entregar de novo não repete.
        Assert.Equal(antes + 1, await EntregarAsync(c));
        Assert.Equal(0, await EntregarAsync(c));
        Assert.Single(await c.AuditoriaAsync(Operacao, item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Equal(0, await PendentesAsync(c));
    }

    [Fact]
    public async Task A_trilha_fora_do_ar_nao_faz_a_operacao_ja_confirmada_falhar_nem_perde_a_auditoria()
    {
        var (c, ana, item) = await CenaAsync("auddurfora");

        var resultado = await AssumirComAuditoriaAsync(c, ana, item.Id, (sp, real) =>
        {
            var (escritor, controleDoEscritor) = Interceptador<IAuditRecoveryWriter>.Envolver(sp.GetRequiredService<IAuditRecoveryWriter>());
            controleDoEscritor.Substituir(nameof(IAuditRecoveryWriter.WriteRecoveredAsync), _ => Task.FromException(new InvalidOperationException("trilha fora do ar")));
            return new DeferredAudit(sp.GetRequiredService<ContactCenterDbContext>(), sp.GetRequiredService<IRequestContextAccessor>(), escritor, sp.GetRequiredService<TimeProvider>());
        });

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome); // a operação já confirmada não falha por causa da trilha
        Assert.Equal(QueueItemStatus.Assigned, (await c.ItemAsync(item.Id))!.Status);
        Assert.Empty(await c.AuditoriaAsync(Operacao, item.Id.ToString()));
        Assert.True(await PendentesAsync(c) >= 1); // a auditoria continua guardada

        Assert.True(await EntregarAsync(c) >= 1); // a trilha voltou
        Assert.Single(await c.AuditoriaAsync(Operacao, item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_queda_entre_gravar_na_trilha_e_apagar_a_linha_nao_duplica_o_evento_na_reentrega()
    {
        var (c, ana, item) = await CenaAsync("auddurdupla");
        await AssumirComAuditoriaAsync(c, ana, item.Id, SemEntrega);
        var guardadas = await PendentesAsync(c);

        // A entrega grava na trilha e o processo cai ANTES de a linha guardada ser apagada.
        var entregues = await c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var (escritor, controle) = Interceptador<IAuditRecoveryWriter>.Envolver(sp.GetRequiredService<IAuditRecoveryWriter>());
            controle.Depois(nameof(IAuditRecoveryWriter.WriteRecoveredAsync), () => throw new InvalidOperationException("o processo caiu depois de gravar na trilha"), chamada: 1);
            return await new DeferredAudit(
                sp.GetRequiredService<ContactCenterDbContext>(), sp.GetRequiredService<IRequestContextAccessor>(), escritor, sp.GetRequiredService<TimeProvider>()).FlushAsync(50, default);
        });
        Assert.Equal(0, entregues);
        Assert.Single(await c.AuditoriaAsync(Operacao, item.Id.ToString())); // já está na trilha
        Assert.Equal(guardadas, await PendentesAsync(c)); // mas a linha guardada continua: a entrega será repetida

        // A reentrega aceita a repetição sem criar um segundo evento, e só então a linha sai.
        Assert.Equal(guardadas, await EntregarAsync(c));
        Assert.Single(await c.AuditoriaAsync(Operacao, item.Id.ToString()));
        Assert.Equal(0, await PendentesAsync(c));
    }

    [Fact]
    public async Task A_mudanca_de_prioridade_tambem_guarda_a_auditoria_na_mesma_transacao_e_a_reconciliacao_a_entrega()
    {
        var (c, _, item) = await CenaAsync("auddurprio");
        var supervisor = await c.AgenteNaFilaAsync(banco, item.QueueId, QueueMemberRole.Supervisor);
        var antes = await PendentesAsync(c);
        var antesDaOperacao = DateTimeOffset.UtcNow;

        await c.ComoAsync(supervisor.Contexto, async sp =>
        {
            var auditoria = SemEntrega(sp, sp.GetRequiredService<IDeferredAudit>());
            return await ActivatorUtilities.CreateInstance<QueueItemService>(sp, auditoria).SetPriorityAsync(item.Id, 5, Guid.NewGuid());
        });

        Assert.Equal(5, (await c.ItemAsync(item.Id))!.Priority);
        Assert.Empty(await c.AuditoriaAsync("contact-center.item.priority", item.Id.ToString()));
        Assert.Equal(antes + 1, await PendentesAsync(c));
        var depoisDaOperacao = DateTimeOffset.UtcNow;
        await Task.Delay(50); // a recuperação vem DEPOIS, de outro contexto (o job), e não pode se passar pela operação

        Assert.Equal(antes + 1, await EntregarComoWorkerAsync(c));
        var evento = Assert.Single(await c.AuditoriaAsync("contact-center.item.priority", item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);

        // A autoria e o instante são os da OPERAÇÃO (o supervisor, na origem dele, naquela hora), não os do job que entregou.
        Assert.Equal((supervisor.Id, supervisor.Contexto.Origin), (evento.ActorUserId, evento.ActorOrigin));
        Assert.NotEqual(WorkerOrigin, evento.ActorOrigin);
        Assert.InRange(evento.OccurredAt, antesDaOperacao, depoisDaOperacao);
    }

    [Fact]
    public async Task A_operacao_sem_falha_entrega_a_auditoria_na_hora_e_nao_deixa_nada_guardado()
    {
        var (c, ana, item) = await CenaAsync("auddurnormal");
        var antes = await PendentesAsync(c);

        await c.AssumirAsync(item.Id, ana.Contexto);

        Assert.Single(await c.AuditoriaAsync(Operacao, item.Id.ToString()), e => e.Outcome == AuditOutcome.Success);
        Assert.Equal(antes, await PendentesAsync(c));
    }
}
