using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Workflows.Infrastructure;

internal sealed class FlowRuntimeStore : IFlowRuntimeStore
{
    private readonly WorkflowsDbContext _db;

    public FlowRuntimeStore(WorkflowsDbContext db)
    {
        _db = db;
    }

    public Task<RoutingDecision?> GetDecisionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Decisions.FirstOrDefaultAsync(d => d.SessionId == sessionId, cancellationToken);

    public async Task<bool> TryRecordAsync(RoutingDecision decision, FlowInstance? instance, CancellationToken cancellationToken)
    {
        _db.Decisions.Add(decision);
        if (instance is not null)
        {
            _db.Instances.Add(instance);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            // Outro processo decidiu esta sessão primeiro: nada é gravado e a resposta é a dele.
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<FlowActivation>> ListEnabledActivationsAsync(CancellationToken cancellationToken) =>
        await _db.Activations.AsNoTracking().Where(a => a.Enabled).ToListAsync(cancellationToken);

    public Task<FlowInstance?> FindInstanceAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Instances.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<IAsyncDisposable?> TryLockExecutionAsync(Guid instanceId, CancellationToken cancellationToken) =>
        FlowExecutionLock.TryAcquireAsync(_db, instanceId, cancellationToken);

    public Task<FlowInstance?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Instances.FirstOrDefaultAsync(i => i.SessionId == sessionId && i.Status < FlowStatus.Completed, cancellationToken);

    public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        _db.Instances.CountAsync(i => i.Status < FlowStatus.Completed, cancellationToken);

    public async Task<IReadOnlyList<FlowEffectRecord>> ListOpenEffectsAsync(Guid instanceId, CancellationToken cancellationToken) =>
        await _db.Effects.Where(e => e.InstanceId == instanceId && (e.State == EffectState.Pending || e.State == EffectState.InFlight)).OrderBy(e => e.Step).ThenBy(e => e.Index).ToListAsync(cancellationToken);

    public async Task WipeEffectPayloadsAsync(Guid instanceId, CancellationToken cancellationToken) =>
        await _db.Effects.Where(e => e.InstanceId == instanceId && e.PayloadCipher != string.Empty).ExecuteUpdateAsync(s => s.SetProperty(e => e.PayloadCipher, string.Empty).SetProperty(e => e.PayloadKeyId, string.Empty), cancellationToken);

    public void Add(FlowEffectRecord effect) => _db.Effects.Add(effect);

    public void Add(FlowStepRecord step) => _db.Steps.Add(step);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new WorkflowsConflictException("changed", "A instância foi alterada por outro processo.");
        }
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        // Entre tenants: só os identificadores (o tenant de cada instância vem da linha gravada pelo módulo, nunca de payload).
        // O tenant cuja instância espera há MAIS tempo vem primeiro: um tenant barulhento não faz os outros esperarem para sempre.
        var tenants = await _db.Instances.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.NextRunAt != null && i.NextRunAt <= now && (i.LeaseExpiresAt == null || i.LeaseExpiresAt < now))
            .GroupBy(i => i.TenantId)
            .OrderBy(g => g.Min(i => i.NextRunAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ClaimDueAsync(string owner, TimeSpan leaseFor, DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        var candidatas = await _db.Instances
            .Where(i => i.NextRunAt != null && i.NextRunAt <= now && (i.LeaseExpiresAt == null || i.LeaseExpiresAt < now))
            .OrderBy(i => i.NextRunAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var reivindicadas = new List<Guid>();
        foreach (var instancia in candidatas)
        {
            instancia.Claim(owner, now + leaseFor, now);
            try
            {
                // Uma por vez: a revisão otimista decide a disputa entre executores; quem perde pula esta instância.
                await _db.SaveChangesAsync(cancellationToken);
                reivindicadas.Add(instancia.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                _db.ChangeTracker.Clear();
            }
        }

        _db.ChangeTracker.Clear();
        return reivindicadas;
    }
}
