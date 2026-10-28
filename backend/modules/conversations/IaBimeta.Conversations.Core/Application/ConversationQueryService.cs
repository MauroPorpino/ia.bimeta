using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Leitura de sessões e do histórico (TEN-2): o recurso é buscado por <c>(tenant, id)</c>, a organização que decide a
/// permissão vem do recurso carregado, e sem a permissão de leitura sobre ela a sessão responde como inexistente. A lista já
/// sai filtrada pelo escopo efetivo do usuário. Nenhuma consulta aceita mais de uma sessão (TEN-5).
/// </summary>
public sealed class ConversationQueryService : IConversationQueries
{
    private readonly IConversationStore _store;
    private readonly IRequestContextAccessor _context;

    public ConversationQueryService(IConversationStore store, IRequestContextAccessor context)
    {
        _store = store;
        _context = context;
    }

    public async Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        return linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId)
            ? null
            : linha.ToView();
    }

    public async Task<Page<SessionView>> ListSessionsAsync(ListSessionsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var contexto = _context.GetRequired();
        var take = ValidateTake(query.Take);
        var depois = CursorCodec.DecodeSession(query.Cursor);

        var visiveis = contexto.OrganizationsWithPermission(Permission.ConversationRead);
        if (visiveis is { Count: 0 })
        {
            // Sem leitura de conversas em organização alguma: a operação em si é negada, em vez de uma lista vazia que esconderia isso.
            throw new AccessDeniedException(Permission.ConversationRead);
        }

        // Uma a mais que o pedido: se vier, há próxima página.
        var linhas = await _store.ListSessionsAsync(new SessionListFilter(visiveis, query.Status, depois, take + 1), cancellationToken);
        var pagina = linhas.Take(take).ToList();
        var proximo = linhas.Count > take ? CursorCodec.Encode(new SessionCursor(pagina[^1].Session.LastMessageAt, pagina[^1].Session.Id)) : null;
        return new Page<SessionView>(pagina.Select(l => l.ToView()).ToList(), proximo);
    }

    public async Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var contexto = _context.GetRequired();
        var take = ValidateTake(query.Take);
        var depois = CursorCodec.DecodeMessage(query.Cursor);
        if (!Enum.IsDefined(query.Order))
        {
            throw new ConversationsValidationException("A ordem do histórico é inválida.");
        }

        var linha = await _store.FindSessionAsync(query.SessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        var mensagens = await _store.ListMessagesAsync(query.SessionId, new MessagePageFilter(query.Order, depois, take + 1), cancellationToken);
        var pagina = mensagens.Take(take).ToList();
        var proximo = mensagens.Count > take ? CursorCodec.Encode(new MessageCursor(pagina[^1].OccurredAt, pagina[^1].MessageSeq)) : null;
        return new Page<MessageView>(pagina.Select(m => m.ToView()).ToList(), proximo);
    }

    public async Task<IReadOnlyList<MessageTranslationView>?> GetMessageTranslationsAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        // A mensagem precisa ser desta sessão: o id de uma mensagem de outra sessão (ou de outro tenant) é inexistente.
        if (await _store.FindMessageAsync(sessionId, messageId, cancellationToken) is null)
        {
            return null;
        }

        var traducoes = await _store.ListTranslationsAsync(messageId, cancellationToken);
        return traducoes.Select(t => t.ToView()).ToList();
    }

    public async Task<IReadOnlyList<SessionTransferView>?> GetTransfersAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        var rastro = await _store.ListTransfersAsync(sessionId, cancellationToken);
        return rastro.Select(t => t.ToView()).ToList();
    }

    private static int ValidateTake(int take)
    {
        if (take < 1 || take > ConversationLimits.MaxPageSize)
        {
            throw new ConversationsValidationException($"O tamanho da página deve ser de 1 a {ConversationLimits.MaxPageSize}.");
        }

        return take;
    }
}
