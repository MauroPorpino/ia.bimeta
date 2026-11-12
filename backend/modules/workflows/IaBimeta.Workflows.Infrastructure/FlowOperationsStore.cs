using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Workflows.Infrastructure;

internal sealed class FlowOperationsStore : IFlowOperationsStore
{
    private readonly WorkflowsDbContext _db;

    public FlowOperationsStore(WorkflowsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FlowActivation>> ListActivationsAsync(CancellationToken cancellationToken) =>
        await _db.Activations.AsNoTracking().ToListAsync(cancellationToken);

    public Task<FlowActivation?> FindActivationAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Activations.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<int> CountActivationsAsync(CancellationToken cancellationToken) => _db.Activations.CountAsync(cancellationToken);

    public void Add(FlowActivation activation) => _db.Activations.Add(activation);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new WorkflowsConflictException("changed", "A ativação foi alterada por outra pessoa. Releia e tente de novo.");
        }
    }

    public async Task<FlowInstancePage> ListInstancesAsync(FlowInstanceFilter filter, CancellationToken cancellationToken)
    {
        var consulta = _db.Instances.AsNoTracking().AsQueryable();
        if (filter.Organizations is { } orgs)
        {
            consulta = consulta.Where(i => orgs.Contains(i.OrganizationId));
        }

        if (filter.Status is { } status)
        {
            consulta = consulta.Where(i => i.Status == status);
        }

        if (filter.Key is { } chave)
        {
            consulta = consulta.Where(i => i.WorkflowKey == chave);
        }

        var total = await consulta.CountAsync(cancellationToken);
        var itens = await consulta.OrderByDescending(i => i.CreatedAt).Skip(filter.Skip).Take(filter.Take).ToListAsync(cancellationToken);
        return new FlowInstancePage(itens.Select(FlowInstanceView.From).ToList(), total);
    }

    public Task<FlowInstance?> FindInstanceAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public async Task<IReadOnlyList<FlowStepView>> ListStepsAsync(Guid instanceId, CancellationToken cancellationToken) =>
        await _db.Steps.AsNoTracking().Where(s => s.InstanceId == instanceId).OrderBy(s => s.Step).ThenBy(s => s.At).Take(500)
            .Select(s => new FlowStepView(s.Step, s.NodeId, s.Outcome, s.At)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FlowEffectView>> ListEffectsAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        // Só estado, tipo e código: o conteúdo do efeito (cifrado) nunca sai daqui.
        var efeitos = await _db.Effects.AsNoTracking().Where(e => e.InstanceId == instanceId).OrderBy(e => e.Step).ThenBy(e => e.Index).Take(500).ToListAsync(cancellationToken);
        return efeitos.Select(e => new FlowEffectView(e.Key, e.Kind.ToString(), e.State.ToString(), e.ResultCode, e.Attempts)).ToList();
    }

    public async Task<IReadOnlyList<FlowMetricRow>> MetricsAsync(IReadOnlyCollection<Guid>? organizations, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var consulta = _db.Instances.AsNoTracking().Where(i => i.CreatedAt >= since);
        if (organizations is { } orgs)
        {
            consulta = consulta.Where(i => orgs.Contains(i.OrganizationId));
        }

        var linhas = await consulta.GroupBy(i => new { i.WorkflowKey, i.VersionNumber, i.Status }).Select(g => new { g.Key.WorkflowKey, g.Key.VersionNumber, g.Key.Status, Quantidade = g.Count() }).ToListAsync(cancellationToken);
        return linhas
            .GroupBy(l => new { l.WorkflowKey, l.VersionNumber })
            .Select(g =>
            {
                int De(FlowStatus s) => g.Where(l => l.Status == s).Sum(l => l.Quantidade);
                return new FlowMetricRow(
                    g.Key.WorkflowKey, g.Key.VersionNumber, g.Sum(l => l.Quantidade), g.Where(l => (int)l.Status < (int)FlowStatus.Completed).Sum(l => l.Quantidade),
                    De(FlowStatus.Completed), De(FlowStatus.HandedOff), De(FlowStatus.Delegated), De(FlowStatus.Failed), De(FlowStatus.Cancelled));
            })
            .OrderBy(r => r.WorkflowKey, StringComparer.Ordinal)
            .ThenByDescending(r => r.VersionNumber)
            .ToList();
    }

    public async Task<IReadOnlyList<FlowFunnelRow>> FunnelAsync(IReadOnlyCollection<Guid>? organizations, string key, int version, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var instancias = _db.Instances.AsNoTracking().Where(i => i.WorkflowKey == key && i.VersionNumber == version && i.CreatedAt >= since);
        if (organizations is { } orgs)
        {
            instancias = instancias.Where(i => orgs.Contains(i.OrganizationId));
        }

        // Uma passagem por nó conta UMA vez por instância (a pergunta que repete não infla o funil): contagem de instâncias distintas por nó.
        var linhas = await _db.Steps.AsNoTracking()
            .Join(instancias, s => s.InstanceId, i => i.Id, (s, i) => new { s.NodeId, s.InstanceId })
            .Distinct()
            .GroupBy(x => x.NodeId)
            .Select(g => new { NodeId = g.Key, Visitas = g.Count() })
            .OrderByDescending(x => x.Visitas)
            .ThenBy(x => x.NodeId)
            .ToListAsync(cancellationToken);
        return linhas.Select(l => new FlowFunnelRow(l.NodeId, l.Visitas)).ToList();
    }
}
