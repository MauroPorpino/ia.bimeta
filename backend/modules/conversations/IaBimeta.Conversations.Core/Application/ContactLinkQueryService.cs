using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>Leitura do vínculo, sempre sem rastreamento e sem trava (ver <see cref="IContactLinkQueries"/>).</summary>
public interface IContactLinkReader
{
    Task<(bool Resolved, bool Shared)> GetLinkStateAsync(Guid connectionId, Guid organizationId, string customerRef, CancellationToken cancellationToken);

    /// <summary>As sessões da organização dos contatos com vínculo ativo para algum dos clientes na conexão, da mais recente para a mais antiga, UMA A MAIS que <paramref name="take"/> (para saber se há próxima página).</summary>
    Task<IReadOnlyList<LinkedSessionRow>> ListSessionsAsync(Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs, SessionCursor? after, int take, CancellationToken cancellationToken);

    Task<bool> IsSessionLinkedAsync(Guid sessionId, Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs, CancellationToken cancellationToken);
}

public sealed record LinkedSessionRow(Guid SessionId, Guid ContactId, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt, DateTimeOffset? ClosedAt);

/// <summary>
/// As consultas do vínculo para o ContactCenter (E2.3). O contexto de sessão embutida vem do Identity (<see cref="IEmbeddedContextQuery"/>, relido a cada chamada, então sessão revogada ou conexão desativada nega na
/// requisição seguinte) e nada vem de parâmetro além do cliente pedido, que só vale se estiver entre os clientes em foco ASSINADOS pelo sistema externo. Qualquer inconsistência é negação.
/// </summary>
public sealed class ContactLinkQueryService : IContactLinkQueries
{
    private readonly IContactLinkReader _reader;
    private readonly IRequestContextAccessor _context;
    private readonly IEmbeddedContextQuery _embedded;

    public ContactLinkQueryService(IContactLinkReader reader, IRequestContextAccessor context, IEmbeddedContextQuery embedded)
    {
        _reader = reader;
        _context = context;
        _embedded = embedded;
    }

    public async Task<CustomerSessionPage?> ListCustomerSessionsAsync(string customerRef, int take, string? cursor, CancellationToken cancellationToken = default)
    {
        if (take < 1 || take > ConversationLimits.MaxPageSize)
        {
            throw new ConversationsValidationException($"O tamanho da página deve ser de 1 a {ConversationLimits.MaxPageSize}.");
        }

        SessionCursor? apos;
        try
        {
            apos = CursorCodec.DecodeSession(cursor);
        }
        catch (ConversationsValidationException)
        {
            throw new InvalidPageCursorException();
        }

        var cliente = Normalize(customerRef);
        if (cliente is null || await AuthorizeAsync(cancellationToken) is not { } sessao || !sessao.CustomerRefs.Contains(cliente, StringComparer.Ordinal))
        {
            return null;
        }

        var (resolvido, compartilhado) = await _reader.GetLinkStateAsync(sessao.ConnectionId, sessao.OrganizationId, cliente, cancellationToken);
        var linhas = await _reader.ListSessionsAsync(sessao.ConnectionId, sessao.OrganizationId, new[] { cliente }, apos, take, cancellationToken);
        var pagina = linhas.Take(take).Select(l => new CustomerSessionRef(l.SessionId, l.ContactId, l.CreatedAt, l.LastMessageAt, l.ClosedAt)).ToList();
        var proximo = linhas.Count > take ? CursorCodec.Encode(new SessionCursor(pagina[^1].LastMessageAt, pagina[^1].SessionId)) : null;
        return new CustomerSessionPage(resolvido, compartilhado, pagina, proximo);
    }

    public async Task<bool> IsSessionOfGrantedCustomerAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await AuthorizeAsync(cancellationToken) is { } sessao
        && sessao.CustomerRefs.Count > 0
        && await _reader.IsSessionLinkedAsync(sessionId, sessao.ConnectionId, sessao.OrganizationId, sessao.CustomerRefs, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListSessionIdsOfGrantedCustomersAsync(int take, CancellationToken cancellationToken = default)
    {
        if (take < 1 || take > ConversationLimits.MaxPageSize || await AuthorizeAsync(cancellationToken) is not { } sessao || sessao.CustomerRefs.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var linhas = await _reader.ListSessionsAsync(sessao.ConnectionId, sessao.OrganizationId, sessao.CustomerRefs, null, take, cancellationToken);
        return linhas.Take(take).Select(l => l.SessionId).ToList();
    }

    /// <summary>A sessão embutida corrente (de usuário, ativa, com conexão ativa), ou <c>null</c>. Contexto de sistema, de integração ou de sessão interativa nunca autoriza.</summary>
    private async Task<EmbeddedSessionInfo?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        if (contexto.Principal?.Embedded is null)
        {
            return null;
        }

        var sessao = await _embedded.GetCurrentAsync(cancellationToken);

        // O limite duro do contexto e o da conexão têm de coincidir: qualquer divergência nega.
        return sessao is not null && sessao.OrganizationId == contexto.Principal.Embedded.OrganizationId ? sessao : null;
    }

    private static string? Normalize(string? customerRef)
    {
        var valor = (customerRef ?? string.Empty).Trim();
        return valor.Length == 0 || valor.Length > ContactLinkLimits.CustomerRefMaxLength || valor.Any(char.IsControl) ? null : valor;
    }
}
