using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>As sugestoes da IA local do tenant do contexto (filtro global por tenant, ADR 0034).</summary>
internal sealed class FaqSuggestionStore : IFaqSuggestionStore
{
    private readonly AgentsDbContext _db;

    public FaqSuggestionStore(AgentsDbContext db)
    {
        _db = db;
    }

    private IQueryable<FaqSuggestion> Filtered(FaqSuggestionStatus? status, FaqSuggestionKind? kind)
    {
        var consulta = _db.FaqSuggestions.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            consulta = consulta.Where(x => x.Status == s);
        }

        if (kind is { } k)
        {
            consulta = consulta.Where(x => x.Kind == k);
        }

        return consulta;
    }

    public async Task<IReadOnlyList<FaqSuggestion>> ListAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, int take, int skip, CancellationToken cancellationToken) =>
        await Filtered(status, kind).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);

    public Task<int> CountAsync(FaqSuggestionStatus? status, FaqSuggestionKind? kind, CancellationToken cancellationToken) =>
        Filtered(status, kind).CountAsync(cancellationToken);

    public Task<FaqSuggestion?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        _db.FaqSuggestions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<FaqSuggestion?> FindPendingByKeyAsync(string dedupeKey, CancellationToken cancellationToken) =>
        _db.FaqSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.DedupeKey == dedupeKey && x.Status == FaqSuggestionStatus.Pending, cancellationToken);

    public void Add(FaqSuggestion suggestion) => _db.FaqSuggestions.Add(suggestion);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new FaqSuggestionConflictException("suggestion-changed", "A sugestão foi alterada por outra operação. Releia e tente de novo.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Outra geração criou a mesma sugestão pendente ao mesmo tempo.
            _db.ChangeTracker.Clear();
            throw new FaqSuggestionConflictException("suggestion-changed", "Já existe uma sugestão pendente igual. Releia a lista.");
        }
    }
}
