using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Workflows.Infrastructure;

internal sealed class WorkflowStore : IWorkflowStore
{
    private readonly WorkflowsDbContext _db;

    public WorkflowStore(WorkflowsDbContext db)
    {
        _db = db;
    }

    public Task<WorkflowVersion?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Versions.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public Task<WorkflowVersion?> FindPublishedAsync(string key, CancellationToken cancellationToken) =>
        _db.Versions.FirstOrDefaultAsync(v => v.Key == key && v.Status == VersionStatus.Published, cancellationToken);

    public Task<WorkflowVersion?> FindDraftAsync(string key, CancellationToken cancellationToken) =>
        _db.Versions.FirstOrDefaultAsync(v => v.Key == key && v.Status == VersionStatus.Draft, cancellationToken);

    public async Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(string key, CancellationToken cancellationToken) =>
        await _db.Versions.Where(v => v.Key == key).OrderByDescending(v => v.Version).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkflowVersion>> ListLatestAsync(CancellationToken cancellationToken)
    {
        // Uma linha por chave: a versão mais nova. A conferência é em memória: o limite de chaves por tenant mantém o conjunto pequeno.
        var todas = await _db.Versions.ToListAsync(cancellationToken);
        return todas.GroupBy(v => v.Key).Select(g => g.OrderByDescending(v => v.Version).First()).ToList();
    }

    public async Task<int> CountKeysAsync(CancellationToken cancellationToken) =>
        await _db.Versions.Select(v => v.Key).Distinct().CountAsync(cancellationToken);

    public void Add(WorkflowVersion version) => _db.Versions.Add(version);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new WorkflowsConflictException("changed", "O fluxo foi alterado por outra operação. Releia e tente de novo.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            _db.ChangeTracker.Clear();
            throw new WorkflowsConflictException("key-in-use", "Já existe um rascunho ou uma versão publicada para esta chave (outra operação foi mais rápida).");
        }
    }

    public async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        await work();
        await transacao.CommitAsync(cancellationToken);
    }
}
