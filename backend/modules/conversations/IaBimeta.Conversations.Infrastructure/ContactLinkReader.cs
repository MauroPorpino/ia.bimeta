using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// A leitura do vínculo (E2.3): sem rastreamento, sem trava e SEMPRE com o filtro global do tenant. O vínculo ATIVO (sem <c>unlinked_at</c>) da conexão e da organização é a subconsulta; as sessões são as da MESMA organização dos
/// contatos vinculados. Uma única instrução por leitura (nada de laço por contato), com paginação por chave <c>(última mensagem, id)</c>.
/// </summary>
internal sealed class ContactLinkReader : IContactLinkReader
{
    private readonly ConversationsDbContext _db;

    public ContactLinkReader(ConversationsDbContext db)
    {
        _db = db;
    }

    private IQueryable<Guid> LinkedContacts(Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs) =>
        _db.ContactLinks.AsNoTracking()
            .Where(l => l.ConnectionId == connectionId && l.OrganizationId == organizationId && l.UnlinkedAt == null && customerRefs.Contains(l.CustomerRef))
            .Select(l => l.ContactId);

    public async Task<(bool Resolved, bool Shared)> GetLinkStateAsync(Guid connectionId, Guid organizationId, string customerRef, CancellationToken cancellationToken)
    {
        var doCliente = await _db.ContactLinks.AsNoTracking()
            .Where(l => l.ConnectionId == connectionId && l.OrganizationId == organizationId && l.UnlinkedAt == null && l.CustomerRef == customerRef)
            .Select(l => l.ContactId)
            .ToListAsync(cancellationToken);
        if (doCliente.Count == 0)
        {
            return (false, false);
        }

        // Compartilhado: mais de um contato para o cliente, ou algum desses contatos com vínculo ativo para OUTRO cliente.
        var compartilhado = doCliente.Count > 1 || await _db.ContactLinks.AsNoTracking()
            .AnyAsync(l => l.ConnectionId == connectionId && l.OrganizationId == organizationId && l.UnlinkedAt == null && l.CustomerRef != customerRef && doCliente.Contains(l.ContactId), cancellationToken);
        return (true, compartilhado);
    }

    public async Task<IReadOnlyList<LinkedSessionRow>> ListSessionsAsync(
        Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs, SessionCursor? after, int take, CancellationToken cancellationToken)
    {
        // A comparação de linha (last_message_at, id) < (cursor) dá a paginação estável por chave; o filtro de tenant continua valendo por cima do SQL direto.
        IQueryable<ServiceSession> sessoes = after is { } cursor
            ? _db.Sessions.FromSqlInterpolated($"SELECT * FROM conversations.service_sessions WHERE (last_message_at, id) < ({cursor.LastMessageAt}, {cursor.Id})")
            : _db.Sessions;
        var contatos = LinkedContacts(connectionId, organizationId, customerRefs);

        return await sessoes.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && contatos.Contains(s.ContactId))
            .OrderByDescending(s => s.LastMessageAt).ThenByDescending(s => s.Id)
            .Take(take + 1)
            .Select(s => new LinkedSessionRow(s.Id, s.ContactId, s.CreatedAt, s.LastMessageAt, s.ClosedAt))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> IsSessionLinkedAsync(Guid sessionId, Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs, CancellationToken cancellationToken)
    {
        var contatos = LinkedContacts(connectionId, organizationId, customerRefs);
        return _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.OrganizationId == organizationId && contatos.Contains(s.ContactId), cancellationToken);
    }
}
