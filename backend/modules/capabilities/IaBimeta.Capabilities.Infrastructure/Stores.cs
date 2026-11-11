using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Capabilities.Infrastructure;

/// <summary>As conexões do tenant do contexto (filtro global por tenant).</summary>
internal sealed class ConnectionStore : IConnectionStore
{
    private readonly CapabilitiesDbContext _db;

    public ConnectionStore(CapabilitiesDbContext db)
    {
        _db = db;
    }

    public Task<IntegrationConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Connections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<IntegrationConnection>> ListAsync(CancellationToken cancellationToken) =>
        await _db.Connections.OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) => _db.Connections.CountAsync(cancellationToken);

    public void Add(IntegrationConnection connection) => _db.Connections.Add(connection);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CapabilitiesConflictException("changed", "A conexão foi alterada por outra operação. Releia e tente de novo.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            _db.ChangeTracker.Clear();
            throw new CapabilitiesConflictException("key-in-use", "Já existe uma conexão com esta chave.");
        }
    }
}

internal sealed class DefinitionStore : IDefinitionStore
{
    private readonly CapabilitiesDbContext _db;

    public DefinitionStore(CapabilitiesDbContext db)
    {
        _db = db;
    }

    public Task<CapabilityDefinition?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Definitions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<CapabilityDefinition?> FindPublishedAsync(string key, CancellationToken cancellationToken) =>
        _db.Definitions.FirstOrDefaultAsync(d => d.Key == key && d.Status == DefinitionStatus.Published, cancellationToken);

    public Task<CapabilityDefinition?> FindDraftAsync(string key, CancellationToken cancellationToken) =>
        _db.Definitions.FirstOrDefaultAsync(d => d.Key == key && d.Status == DefinitionStatus.Draft, cancellationToken);

    public async Task<IReadOnlyList<CapabilityDefinition>> ListVersionsAsync(string key, CancellationToken cancellationToken) =>
        await _db.Definitions.Where(d => d.Key == key).OrderByDescending(d => d.Version).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CapabilityDefinition>> ListLatestAsync(Guid? connectionId, CancellationToken cancellationToken)
    {
        var consulta = _db.Definitions.AsQueryable();
        if (connectionId is { } conexao)
        {
            consulta = consulta.Where(d => d.ConnectionId == conexao);
        }

        // Uma linha por chave: a versão mais nova (rascunho, publicada ou aposentada). A conferência é em memória: o limite por tenant (200 chaves) mantém o conjunto pequeno.
        var todas = await consulta.ToListAsync(cancellationToken);
        return todas.GroupBy(d => d.Key).Select(g => g.OrderByDescending(d => d.Version).First()).ToList();
    }

    public async Task<IReadOnlyList<CapabilityDefinition>> ListPublishedAsync(CancellationToken cancellationToken) =>
        await _db.Definitions.Where(d => d.Status == DefinitionStatus.Published).ToListAsync(cancellationToken);

    public async Task<int> CountKeysAsync(CancellationToken cancellationToken) =>
        await _db.Definitions.Select(d => d.Key).Distinct().CountAsync(cancellationToken);

    public async Task<int> MaxVersionAsync(string key, CancellationToken cancellationToken) =>
        await _db.Definitions.Where(d => d.Key == key).Select(d => (int?)d.Version).MaxAsync(cancellationToken) ?? 0;

    public void Add(CapabilityDefinition definition) => _db.Definitions.Add(definition);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CapabilitiesConflictException("changed", "A operação foi alterada por outra operação. Releia e tente de novo.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            _db.ChangeTracker.Clear();
            throw new CapabilitiesConflictException("key-in-use", "Já existe um rascunho ou uma versão publicada para esta chave (outra operação foi mais rápida).");
        }
    }

    public async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        await work();
        await transacao.CommitAsync(cancellationToken);
    }
}

internal sealed class ExecutionStore : IExecutionStore
{
    private readonly CapabilitiesDbContext _db;

    public ExecutionStore(CapabilitiesDbContext db)
    {
        _db = db;
    }

    public async Task<(bool Added, CapabilityExecution Execution)> TryAddAsync(CapabilityExecution execution, CancellationToken cancellationToken)
    {
        _db.Executions.Add(execution);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return (true, execution);
        }
        catch (DbUpdateException ex) when (execution.IdempotencyKey is not null && PostgresErrors.IsUniqueViolation(ex, out var restricao) && restricao == CapabilityExecutionConfiguration.IdempotencyIndex)
        {
            // Outra chamada com a MESMA chave chegou primeiro (o índice único decidiu): devolve a dela, sem executar de novo.
            _db.ChangeTracker.Clear();
            var existente = await _db.Executions.FirstAsync(e => e.IdempotencyKey == execution.IdempotencyKey, cancellationToken);
            return (false, existente);
        }
    }

    public Task<CapabilityExecution?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Executions.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new CapabilitiesConflictException("already-confirmed", "A execução foi alterada por outra operação.");
        }
    }

    public Task<int> CountSinceAsync(DateTimeOffset since, Guid? connectionId, bool writesOnly, CancellationToken cancellationToken)
    {
        var consulta = _db.Executions.Where(e => e.CreatedAt >= since);
        if (connectionId is { } conexao)
        {
            consulta = consulta.Where(e => e.ConnectionId == conexao);
        }

        if (writesOnly)
        {
            consulta = consulta.Where(e => e.Effect == CapabilityEffect.Write);
        }

        return consulta.CountAsync(cancellationToken);
    }

    public Task<int> CountInFlightAsync(Guid connectionId, DateTimeOffset notBefore, CancellationToken cancellationToken) =>
        _db.Executions.CountAsync(e => e.ConnectionId == connectionId && e.Status == CapabilityExecutionStatus.InProgress && e.CreatedAt >= notBefore, cancellationToken);

    private IQueryable<CapabilityExecution> Filtered(ExecutionFilter filtro, IReadOnlySet<Guid>? organizations)
    {
        var consulta = _db.Executions.AsNoTracking().AsQueryable();
        if (organizations is not null)
        {
            var organizacoes = organizations.ToArray();
            consulta = consulta.Where(e => e.OrganizationId != null && organizacoes.Contains(e.OrganizationId.Value));
        }

        if (filtro.ConnectionId is { } conexao)
        {
            consulta = consulta.Where(e => e.ConnectionId == conexao);
        }

        if (filtro.Key is { } chave)
        {
            consulta = consulta.Where(e => e.DefinitionKey == chave);
        }

        if (filtro.Status is { } status)
        {
            consulta = consulta.Where(e => e.Status == status);
        }

        if (filtro.IsTest is { } teste)
        {
            consulta = consulta.Where(e => e.IsTest == teste);
        }

        return consulta;
    }

    public async Task<IReadOnlyList<CapabilityExecution>> ListAsync(ExecutionFilter filter, IReadOnlySet<Guid>? organizations, int take, int skip, CancellationToken cancellationToken) =>
        await Filtered(filter, organizations).OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);

    public Task<int> CountAsync(ExecutionFilter filter, IReadOnlySet<Guid>? organizations, CancellationToken cancellationToken) =>
        Filtered(filter, organizations).CountAsync(cancellationToken);

    public async Task<IReadOnlyList<ConnectionStats>> StatsAsync(DateTimeOffset since, IReadOnlySet<Guid>? organizations, CancellationToken cancellationToken)
    {
        var consulta = Filtered(new ExecutionFilter(null, null, null, null), organizations).Where(e => e.CreatedAt >= since);
        return await consulta
            .GroupBy(e => e.ConnectionId)
            .Select(g => new ConnectionStats(
                g.Key,
                g.Count(),
                g.Count(e => e.Status == CapabilityExecutionStatus.Failed),
                g.Count(e => e.Status == CapabilityExecutionStatus.Unknown),
                g.Max(e => (DateTimeOffset?)e.CreatedAt)))
            .ToListAsync(cancellationToken);
    }
}
