using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Workflows.Infrastructure;

public static class WorkflowsRetentionExtensions
{
    /// <summary>
    /// Registra o expurgo do Workflows (ADR 0037 §5): as instâncias de fluxo JÁ ENCERRADAS, os passos e os efeitos delas, depois do prazo de cada empresa (<c>Retention:Days:FlowInstances</c>, padrão 30 dias). Uma tarefa por
    /// tabela (a regra do inventário); as três valem em qualquer ordem, porque cada uma também recolhe as linhas órfãs das outras.
    /// </summary>
    public static IServiceCollection AddWorkflowsRetention(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, FlowEffectRetentionTask>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, FlowStepRetentionTask>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, FlowInstanceRetentionTask>());
        return services;
    }
}

internal abstract class WorkflowsRetentionTask<TEntity> : IRetentionTask
    where TEntity : class
{
    private readonly IServiceScopeFactory _scopes;

    protected WorkflowsRetentionTask(IServiceScopeFactory scopes, string name)
    {
        _scopes = scopes;
        Name = name;
    }

    public string Name { get; }

    public RetentionCategory Category => RetentionCategory.FlowInstances;

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<WorkflowsDbContext>();
        var proprio = pass.TenantId is { } id ? new TenantId(id) : null;
        var excluidos = pass.ExcludedTenants.Select(g => new TenantId(g)).ToList();
        var elegiveis = Eligible(db, pass.Cutoff, proprio, excluidos);

        var total = await elegiveis.CountAsync(cancellationToken);
        var apagados = dryRun || total == 0 ? 0 : await elegiveis.ExecuteDeleteAsync(cancellationToken);
        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, total, apagados);
    }

    protected abstract IQueryable<TEntity> Eligible(WorkflowsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded);

    /// <summary>As instâncias ENCERRADAS antes do corte (uma instância ativa nunca expira por prazo).</summary>
    protected static IQueryable<FlowInstance> Ended(WorkflowsDbContext db, DateTimeOffset cutoff) =>
        db.Instances.IgnoreQueryFilters().Where(i => i.Status >= FlowStatus.Completed && i.EndedAt != null && i.EndedAt < cutoff);
}

/// <summary>Os efeitos das instâncias encerradas antes do corte, e os órfãos (a instância já foi expurgada).</summary>
internal sealed class FlowEffectRetentionTask : WorkflowsRetentionTask<FlowEffectRecord>
{
    public FlowEffectRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "workflows.flow_effects")
    {
    }

    protected override IQueryable<FlowEffectRecord> Eligible(WorkflowsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var velhos = db.Effects.IgnoreQueryFilters().Where(e =>
            Ended(db, cutoff).Any(i => i.TenantId == e.TenantId && i.Id == e.InstanceId) || !db.Instances.IgnoreQueryFilters().Any(i => i.TenantId == e.TenantId && i.Id == e.InstanceId));
        return own is not null ? velhos.Where(e => e.TenantId == own) : velhos.Where(e => !excluded.Contains(e.TenantId));
    }
}

/// <summary>Os passos das instâncias encerradas antes do corte, e os órfãos.</summary>
internal sealed class FlowStepRetentionTask : WorkflowsRetentionTask<FlowStepRecord>
{
    public FlowStepRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "workflows.flow_steps")
    {
    }

    protected override IQueryable<FlowStepRecord> Eligible(WorkflowsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var velhos = db.Steps.IgnoreQueryFilters().Where(s =>
            Ended(db, cutoff).Any(i => i.TenantId == s.TenantId && i.Id == s.InstanceId) || !db.Instances.IgnoreQueryFilters().Any(i => i.TenantId == s.TenantId && i.Id == s.InstanceId));
        return own is not null ? velhos.Where(s => s.TenantId == own) : velhos.Where(s => !excluded.Contains(s.TenantId));
    }
}

/// <summary>As instâncias ENCERRADAS antes do corte. As variáveis e o conteúdo dos efeitos já foram esvaziados no encerramento: aqui sai o que sobrou (identificadores, estados e códigos).</summary>
internal sealed class FlowInstanceRetentionTask : WorkflowsRetentionTask<FlowInstance>
{
    public FlowInstanceRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "workflows.flow_instances")
    {
    }

    protected override IQueryable<FlowInstance> Eligible(WorkflowsDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var velhas = Ended(db, cutoff);
        return own is not null ? velhas.Where(i => i.TenantId == own) : velhas.Where(i => !excluded.Contains(i.TenantId));
    }
}
