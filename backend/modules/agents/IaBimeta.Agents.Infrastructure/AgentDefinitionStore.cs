using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>O armazenamento das versões da definição do agente do tenant do contexto (filtro global por tenant). O turno lê só a versão PUBLICADA.</summary>
internal sealed class AgentDefinitionStore : IAgentDefinitionStore
{
    private readonly AgentsDbContext _db;

    public AgentDefinitionStore(AgentsDbContext db)
    {
        _db = db;
    }

    public async Task<AgentDefinitionContent?> GetPublishedAsync(CancellationToken cancellationToken)
    {
        var versao = await _db.DefinitionVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Status == AgentDefinitionStatus.Published, cancellationToken);
        return versao?.Content;
    }

    public Task<AgentDefinitionVersion?> FindDraftAsync(CancellationToken cancellationToken) =>
        _db.DefinitionVersions.FirstOrDefaultAsync(v => v.Status == AgentDefinitionStatus.Draft, cancellationToken);

    public Task<AgentDefinitionVersion?> FindPublishedVersionAsync(CancellationToken cancellationToken) =>
        _db.DefinitionVersions.FirstOrDefaultAsync(v => v.Status == AgentDefinitionStatus.Published, cancellationToken);

    public async Task<int> NextNumberAsync(CancellationToken cancellationToken) =>
        ((await _db.DefinitionVersions.MaxAsync(v => (int?)v.Number, cancellationToken)) ?? 0) + 1;

    public void Add(AgentDefinitionVersion version) => _db.DefinitionVersions.Add(version);

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        var resultado = await work();
        await transacao.CommitAsync(cancellationToken);
        return resultado;
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new AgentDefinitionConflictException();
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Duas pessoas criando o primeiro rascunho (ou publicando) ao mesmo tempo: a segunda perde com conflito explícito.
            _db.ChangeTracker.Clear();
            throw new AgentDefinitionConflictException();
        }
    }
}
