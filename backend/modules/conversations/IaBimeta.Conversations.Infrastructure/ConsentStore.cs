using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// A persistência do consentimento (ADR 0041, E2.4). Cada unidade de trabalho é UMA transação: as linhas de consentimento, a decisão da ocorrência e o pedido de auditoria (outbox do módulo) entram no mesmo
/// <c>SaveChanges</c>, e ou tudo é confirmado ou nada. As operações do mesmo telefone na mesma organização se serializam por trava consultiva da transação. O repositório NÃO tem caminho de atualização nem exclusão do
/// consentimento (só <see cref="IConsentUnitOfWork.Add"/>), e o banco recusa UPDATE e DELETE por gatilho.
/// </summary>
internal sealed class ConsentStore : IConsentStore
{
    private readonly ConversationsDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOutboxWriter _outbox;

    public ConsentStore(ConversationsDbContext db, TimeProvider clock, IOutboxWriter outbox)
    {
        _db = db;
        _clock = clock;
        _outbox = outbox;
    }

    public async Task<IConsentUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        return new ConsentUnitOfWork(_db, transacao, _clock, _outbox);
    }
}

internal sealed class ConsentUnitOfWork : IConsentUnitOfWork
{
    private readonly ConversationsDbContext _db;
    private readonly IDbContextTransaction _transacao;
    private readonly TimeProvider _clock;
    private readonly IOutboxWriter _outbox;
    private bool _confirmada;

    public ConsentUnitOfWork(ConversationsDbContext db, IDbContextTransaction transacao, TimeProvider clock, IOutboxWriter outbox)
    {
        _db = db;
        _transacao = transacao;
        _clock = clock;
        _outbox = outbox;
    }

    public Task<ConsentSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken) => ConsentQueries.FindSessionAsync(_db, sessionId, cancellationToken);

    public async Task LockPhoneAsync(Guid organizationId, string phone, CancellationToken cancellationToken)
    {
        var chave = ConsentAuditEvents.PhoneLockKey(_db.CurrentTenantValue, organizationId, phone);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken);
    }

    public async Task LockReviewAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var chave = $"optout-review:{_db.CurrentTenantValue:N}:{messageId:N}";
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken);
    }

    public Task<ContactConsent?> GetLatestAsync(Guid organizationId, string phone, ConsentPurpose purpose, CancellationToken cancellationToken) =>
        _db.Consents.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Phone == phone && c.Purpose == purpose)
            .OrderByDescending(c => c.Seq)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<DateTimeOffset?> GetLatestRevocationCollectedAtAsync(Guid organizationId, string phone, ConsentPurpose purpose, CancellationToken cancellationToken) =>
        await _db.Consents.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Phone == phone && c.Purpose == purpose && c.Status == ConsentStatus.Revoked)
            .MaxAsync(c => (DateTimeOffset?)c.CollectedAt, cancellationToken);

    public void Add(ContactConsent consent) => _db.Consents.Add(consent);

    public Task<OptOutReview?> FindReviewAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken) =>
        _db.OptOutReviews.Where(r => r.SessionId == sessionId && r.MessageId == messageId).FirstOrDefaultAsync(cancellationToken);

    public async Task CommitAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        try
        {
            // A auditoria entra no MESMO SaveChanges do efeito (outbox do módulo): se a transação é desfeita, ela some junto.
            if (events.Count > 0)
            {
                _outbox.Stage(_db, events, new TenantId(_db.CurrentTenantValue), _clock.GetUtcNow());
            }

            await _db.SaveChangesAsync(cancellationToken);
            await _transacao.CommitAsync(cancellationToken);
            _confirmada = true;
        }
        catch
        {
            // Todo caminho de falha desanexa o que foi adicionado: o contexto desta requisição não pode guardar uma linha que não foi gravada.
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_confirmada)
        {
            _db.ChangeTracker.Clear();
            await _transacao.RollbackAsync();
        }

        await _transacao.DisposeAsync();
    }
}

/// <summary>As leituras do consentimento: sem transação, sem trava, sempre sob o filtro global do tenant.</summary>
internal sealed class ConsentReader : IConsentReader
{
    private readonly ConversationsDbContext _db;

    public ConsentReader(ConversationsDbContext db)
    {
        _db = db;
    }

    public Task<ConsentSessionInfo?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken) => ConsentQueries.FindSessionAsync(_db, sessionId, cancellationToken);

    public async Task<IReadOnlyList<ContactConsent>> LatestPerPurposeAsync(Guid organizationId, string phone, CancellationToken cancellationToken)
    {
        // Duas finalidades, no máximo: uma consulta por finalidade, cada uma pelo índice (tenant, organização, telefone, finalidade, seq).
        var resultado = new List<ContactConsent>(2);
        foreach (var finalidade in Enum.GetValues<ConsentPurpose>())
        {
            var ultima = await _db.Consents.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && c.Phone == phone && c.Purpose == finalidade)
                .OrderByDescending(c => c.Seq)
                .FirstOrDefaultAsync(cancellationToken);
            if (ultima is not null)
            {
                resultado.Add(ultima);
            }
        }

        return resultado;
    }

    public async Task<IReadOnlyList<ContactConsent>> HistoryAsync(Guid organizationId, string phone, int limit, CancellationToken cancellationToken) =>
        await _db.Consents.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.Phone == phone)
            .OrderByDescending(c => c.Seq)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OptOutReview>> ListReviewsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await _db.OptOutReviews.AsNoTracking()
            .Where(r => r.SessionId == sessionId)
            .OrderByDescending(r => r.OpenedAt)
            .ThenByDescending(r => r.Id)
            .Take(ConsentLimits.MaxHistory)
            .ToListAsync(cancellationToken);
}

internal static class ConsentQueries
{
    /// <summary>A organização, o contato e a referência do contato da sessão (uma consulta, com o contato do MESMO tenant), ou <c>null</c>.</summary>
    public static Task<ConsentSessionInfo?> FindSessionAsync(ConversationsDbContext db, Guid sessionId, CancellationToken cancellationToken) =>
        db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Join(db.Contacts.AsNoTracking(), s => s.ContactId, c => c.Id, (s, c) => new ConsentSessionInfo(s.OrganizationId, c.Id, c.ExternalRef))
            .FirstOrDefaultAsync(cancellationToken);
}
