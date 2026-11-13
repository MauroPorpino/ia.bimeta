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

    public async Task<PublishedDefinition?> GetPublishedVersionAsync(CancellationToken cancellationToken)
    {
        var versao = await _db.DefinitionVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Status == AgentDefinitionStatus.Published, cancellationToken);
        return versao is null ? null : new PublishedDefinition(versao.Number, versao.Content);
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

    public async Task LockDefinitionAsync(CancellationToken cancellationToken)
    {
        // Bloqueio consultivo de TRANSAÇÃO (liberado no commit ou rollback), por tenant: o hash é de 64 bits do identificador do tenant com um sal próprio desta finalidade.
        var chave = $"agents.definition:{_db.CurrentTenantId:N}";
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken);

        // Com o bloqueio em mãos, o que este contexto já leu das versões (antes da espera) pode estar defasado, e o EF não atualiza uma entidade já rastreada: solta-as para a próxima leitura vir do banco.
        foreach (var entrada in _db.ChangeTracker.Entries<AgentDefinitionVersion>().Where(e => e.State == EntityState.Unchanged).ToList())
        {
            entrada.State = EntityState.Detached;
        }
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
