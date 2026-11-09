using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>As lacunas do FAQ do tenant do contexto (filtro global por tenant) e a gravação da decisão do administrador. Quem grava as ocorrências é o <see cref="KnowledgeGapRecorder"/>, por SQL atômico.</summary>
internal sealed class KnowledgeGapStore : IKnowledgeGapStore
{
    private readonly AgentsDbContext _db;

    public KnowledgeGapStore(AgentsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<KnowledgeGapRow>> ListAsync(KnowledgeGapStatus? status, int take, int skip, CancellationToken cancellationToken)
    {
        var lacunas = _db.KnowledgeGaps.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            lacunas = lacunas.Where(g => g.Status == s);
        }

        var linhas = await lacunas
            .Select(g => new { Gap = g, Count = _db.KnowledgeGapOccurrences.Count(o => o.GapId == g.Id) })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Gap.LastSeenAt).ThenBy(x => x.Gap.Id)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
        return linhas.Select(x => new KnowledgeGapRow(x.Gap, x.Count)).ToList();
    }

    public Task<int> CountAsync(KnowledgeGapStatus? status, CancellationToken cancellationToken) =>
        status is { } s
            ? _db.KnowledgeGaps.CountAsync(g => g.Status == s, cancellationToken)
            : _db.KnowledgeGaps.CountAsync(cancellationToken);

    public Task<KnowledgeGap?> FindAsync(Guid gapId, CancellationToken cancellationToken) =>
        _db.KnowledgeGaps.FirstOrDefaultAsync(g => g.Id == gapId, cancellationToken);

    public Task<int> CountOccurrencesAsync(Guid gapId, CancellationToken cancellationToken) =>
        _db.KnowledgeGapOccurrences.CountAsync(o => o.GapId == gapId, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeGapOccurrence>> LatestOccurrencesAsync(Guid gapId, int take, CancellationToken cancellationToken) =>
        await _db.KnowledgeGapOccurrences.AsNoTracking()
            .Where(o => o.GapId == gapId)
            .OrderByDescending(o => o.OccurredAt).ThenBy(o => o.TurnId)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Outro administrador decidiu a mesma lacuna primeiro (a revisão da linha mudou, ou a linha sumiu): o segundo perde com conflito explícito.
            _db.ChangeTracker.Clear();
            throw new KnowledgeGapConflictException("gap-changed", "A lacuna foi decidida por outra operação. Releia a lista e tente de novo.");
        }
    }
}
