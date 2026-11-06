using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Messaging;

public static class RetentionExtensions
{
    /// <summary>Registra o expurgo dos eventos PROCESSADOS do outbox do módulo (e a sonda dos mortos, que nunca são expurgados). O DbContext é o do módulo emissor.</summary>
    public static IServiceCollection AddOutboxRetention<TContext>(this IServiceCollection services, string module)
        where TContext : DbContext
    {
        services.AddSingleton(provider => new OutboxRetentionTask<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), module));
        services.AddSingleton<IRetentionTask>(provider => provider.GetRequiredService<OutboxRetentionTask<TContext>>());
        services.AddSingleton<IDeadLetterProbe>(provider => provider.GetRequiredService<OutboxRetentionTask<TContext>>());
        return services;
    }

    /// <summary>Registra o expurgo das marcas de idempotência (Inbox) do módulo consumidor. O DbContext é o do módulo consumidor.</summary>
    public static IServiceCollection AddInboxRetention<TContext>(this IServiceCollection services, string module)
        where TContext : DbContext
    {
        services.AddSingleton<IRetentionTask>(provider => new InboxRetentionTask<TContext>(provider.GetRequiredService<IServiceScopeFactory>(), module));
        return services;
    }
}

/// <summary>As consultas do expurgo do outbox: sem o filtro global, com o predicado explícito da passada.</summary>
internal static class OutboxRetentionQueries
{
    public static IQueryable<OutboxMessage> Within(IQueryable<OutboxMessage> query, RetentionPass pass)
    {
        if (pass.TenantId is { } proprio)
        {
            return query.Where(o => o.TenantId == proprio);
        }

        var excluidos = pass.ExcludedTenants.ToList();
        return query.Where(o => !excluidos.Contains(o.TenantId));
    }

    /// <summary>Só os PROCESSADOS anteriores ao corte. Pendente, em andamento e morto nunca entram.</summary>
    public static IQueryable<OutboxMessage> ProcessedBefore(DbContext db, RetentionPass pass)
    {
        var corte = pass.Cutoff;
        return Within(db.Set<OutboxMessage>().IgnoreQueryFilters().Where(o => o.State == OutboxState.Processed && o.ProcessedAt < corte), pass);
    }
}

/// <summary>
/// O expurgo dos eventos processados de UM outbox (<c>&lt;módulo&gt;.outbox_message</c>). É aqui que expira o payload do evento do canal, que carrega o texto do cliente. Os MORTOS ficam: só saem por reenfileiramento ou
/// descarte autorizado (DP-06); esta tarefa apenas os conta, e o expurgo do Inbox consulta a contagem antes de agir.
/// </summary>
public sealed class OutboxRetentionTask<TContext> : IRetentionTask, IDeadLetterProbe
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;

    public OutboxRetentionTask(IServiceScopeFactory scopes, string module)
    {
        _scopes = scopes;
        Name = module + ".outbox_message";
    }

    public string Name { get; }

    public RetentionCategory Category => RetentionCategory.ProcessedEvents;

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var elegiveis = OutboxRetentionQueries.ProcessedBefore(db, pass);

        var total = await elegiveis.CountAsync(cancellationToken);
        var apagados = dryRun || total == 0 ? 0 : await elegiveis.ExecuteDeleteAsync(cancellationToken);
        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, total, apagados);
    }

    public async Task<(int Count, DateTimeOffset? Oldest)> DeadAsync(RetentionPass pass, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var mortos = OutboxRetentionQueries.Within(db.Set<OutboxMessage>().IgnoreQueryFilters().Where(o => o.State == OutboxState.Dead), pass);

        var total = await mortos.CountAsync(cancellationToken);
        var maisAntigo = total == 0 ? null : await mortos.MinAsync(o => (DateTimeOffset?)o.CreatedAt, cancellationToken);
        return (total, maisAntigo);
    }
}

/// <summary>
/// O expurgo das marcas de idempotência de UM consumidor (<c>&lt;módulo&gt;.inbox_message</c>: só identificadores). Quem decide se é seguro rodar é o orquestrador, que confere antes se há eventos mortos mais antigos que
/// o corte (reenfileirar um morto depois de a marca sumir poderia repetir o efeito).
/// </summary>
public sealed class InboxRetentionTask<TContext> : IRetentionTask
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;

    public InboxRetentionTask(IServiceScopeFactory scopes, string module)
    {
        _scopes = scopes;
        Name = module + ".inbox_message";
    }

    public string Name { get; }

    public RetentionCategory Category => RetentionCategory.InboxMarkers;

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();

        var corte = pass.Cutoff;
        var excluidos = pass.ExcludedTenants.ToList();
        var antigas = db.Set<InboxMessage>().IgnoreQueryFilters().Where(m => m.ProcessedAt < corte);
        var elegiveis = pass.TenantId is { } proprio
            ? antigas.Where(m => m.TenantId == proprio)
            : antigas.Where(m => !excluidos.Contains(m.TenantId));

        var total = await elegiveis.CountAsync(cancellationToken);
        var apagados = dryRun || total == 0 ? 0 : await elegiveis.ExecuteDeleteAsync(cancellationToken);
        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, total, apagados);
    }
}
