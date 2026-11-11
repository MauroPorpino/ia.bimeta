using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Agents.Infrastructure;

public static class AgentsRetentionExtensions
{
    /// <summary>Registra o expurgo TECNICO do Agents (ADR 0034): as decisoes semanticas (so numeros) e as sugestoes da IA local ja decididas. Nada de dado pessoal de cliente fica fora do prazo; as pendentes nunca expiram por prazo.</summary>
    public static IServiceCollection AddAgentsRetention(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, SemanticDecisionRetentionTask>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, FaqSuggestionRetentionTask>());
        return services;
    }
}

internal abstract class AgentsRetentionTask<TEntity> : IRetentionTask
    where TEntity : class
{
    private readonly IServiceScopeFactory _scopes;

    protected AgentsRetentionTask(IServiceScopeFactory scopes, string name, RetentionCategory category)
    {
        _scopes = scopes;
        Name = name;
        Category = category;
    }

    public string Name { get; }

    public RetentionCategory Category { get; }

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<AgentsDbContext>();

        var proprio = pass.TenantId is { } id ? new TenantId(id) : null;
        var excluidos = pass.ExcludedTenants.Select(g => new TenantId(g)).ToList();
        var elegiveis = Eligible(db, pass.Cutoff, proprio, excluidos);

        var total = await elegiveis.CountAsync(cancellationToken);
        var apagados = dryRun || total == 0 ? 0 : await elegiveis.ExecuteDeleteAsync(cancellationToken);
        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, total, apagados);
    }

    protected abstract IQueryable<TEntity> Eligible(AgentsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded);
}

/// <summary>Decisoes semanticas anteriores ao corte (so numeros e codigos).</summary>
internal sealed class SemanticDecisionRetentionTask : AgentsRetentionTask<SemanticDecision>
{
    public SemanticDecisionRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "agents.semantic_decisions", RetentionCategory.SemanticDecisions)
    {
    }

    protected override IQueryable<SemanticDecision> Eligible(AgentsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var velhas = db.SemanticDecisions.IgnoreQueryFilters().Where(d => d.DecidedAt < cutoff);
        return own is not null ? velhas.Where(d => d.TenantId == own) : velhas.Where(d => !excluded.Contains(d.TenantId));
    }
}

/// <summary>Sugestoes JA DECIDIDAS (aceitas ou rejeitadas) cuja decisao e anterior ao corte. Uma sugestao pendente nunca e expurgada.</summary>
internal sealed class FaqSuggestionRetentionTask : AgentsRetentionTask<FaqSuggestion>
{
    public FaqSuggestionRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "agents.faq_suggestions", RetentionCategory.FaqSuggestions)
    {
    }

    protected override IQueryable<FaqSuggestion> Eligible(AgentsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var decididas = db.FaqSuggestions.IgnoreQueryFilters().Where(s => s.Status != FaqSuggestionStatus.Pending && s.DecidedAt != null && s.DecidedAt < cutoff);
        return own is not null ? decididas.Where(s => s.TenantId == own) : decididas.Where(s => !excluded.Contains(s.TenantId));
    }
}
