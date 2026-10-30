using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>Cursor de sessões: ordem por <c>(LastMessageAt, Id)</c>, da mais recente para a mais antiga.</summary>
public sealed record SessionCursor(DateTimeOffset LastMessageAt, Guid Id);

/// <summary>Cursor do histórico: ordem por <c>(OccurredAt, MessageSeq)</c>.</summary>
public sealed record MessageCursor(DateTimeOffset OccurredAt, long MessageSeq);

/// <param name="OrganizationIds">As organizações visíveis ao usuário, ou <c>null</c> para todas (contexto de sistema ou concessão de tenant).</param>
public sealed record SessionListFilter(IReadOnlySet<Guid>? OrganizationIds, SessionStatus? Status, SessionCursor? After, int Take);

/// <param name="ThroughMessageSeq">Só as mensagens com <c>MessageSeq</c> até este valor (a fronteira de um período), aplicado antes da paginação.</param>
public sealed record MessagePageFilter(HistoryOrder Order, MessageCursor? After, int Take, long? ThroughMessageSeq = null);

/// <param name="ContactPreferredLocale">A preferência de idioma do contato, se houver.</param>
/// <param name="EffectiveLocale">O idioma efetivo da conversa da sessão e a origem da escolha.</param>
public sealed record SessionRow(
    ServiceSession Session,
    string? ContactDisplayName,
    Locale? ContactPreferredLocale = null,
    Locale? EffectiveLocale = null,
    LocaleSource? LocaleSource = null);

/// <summary>
/// Porta de persistência do Conversations. Opera sempre dentro do tenant do contexto da operação: um registro de outro
/// tenant nunca é encontrado nem listado. As leituras daqui não travam nada; quem muda estado usa
/// <see cref="BeginAsync"/>.
/// </summary>
public interface IConversationStore
{
    /// <summary>Abre a unidade de trabalho: uma transação em que a linha da sessão é travada antes de ser lida e alterada.</summary>
    Task<IConversationUnitOfWork> BeginAsync(CancellationToken cancellationToken);

    Task<SessionRow?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionRow>> ListSessionsAsync(SessionListFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<Message>> ListMessagesAsync(Guid sessionId, MessagePageFilter filter, CancellationToken cancellationToken);

    Task<Message?> FindMessageByProviderEventAsync(ChannelInstanceId channelInstanceId, string providerEventId, CancellationToken cancellationToken);

    /// <summary>A mensagem, só se pertence à sessão informada (sem rastreamento).</summary>
    Task<Message?> FindMessageAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>As traduções da mensagem, por idioma de destino.</summary>
    Task<IReadOnlyList<MessageTranslation>> ListTranslationsAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>O rastro de encaminhamentos da sessão, do mais antigo para o mais novo.</summary>
    Task<IReadOnlyList<SessionTransfer>> ListTransfersAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>O registro da transição (ou da operação avaliada sem efeito) pelo identificador, sem rastreamento e sem trava; nulo se não existe.</summary>
    Task<HandlingTransition?> FindTransitionReadOnlyAsync(Guid sessionId, Guid transitionId, CancellationToken cancellationToken);

    /// <summary>O registro do encaminhamento pelo identificador, sem rastreamento e sem trava; nulo se não existe.</summary>
    Task<SessionTransfer?> FindTransferReadOnlyAsync(Guid sessionId, Guid transferId, CancellationToken cancellationToken);
}

/// <summary>
/// Uma transação sobre a sessão. A linha da sessão é travada (<c>FOR UPDATE</c>) ANTES de ser lida, então todo comando que
/// muda o estado de uma sessão se serializa nela: a ordem das mensagens é total, e o comparar-e-trocar das cercas
/// (turno ativo, época, <c>StateVersion</c>, <c>LastInboundSeq</c>) não tem corrida. Sem <see cref="CommitAsync"/>, o
/// descarte desfaz tudo.
/// </summary>
public interface IConversationUnitOfWork : IAsyncDisposable
{
    Task<Contact> GetOrCreateContactAsync(ChannelInstanceId channelInstanceId, string externalRef, string? displayName, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>O identificador da sessão aberta do contato (no máximo uma por contato), criando-a se não houver.</summary>
    Task<Guid> GetOrCreateOpenSessionIdAsync(Contact contact, Guid organizationId, Guid? businessUnitId, Guid? departmentId, HandlingMode initialHandlingMode, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Trava a linha da sessão e a devolve já rastreada para alteração, ou <c>null</c> se ela não existe no tenant.</summary>
    Task<ServiceSession?> LockSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Trava a sessão da mensagem e devolve as duas, rastreadas, ou <c>null</c> se a mensagem não existe no tenant.</summary>
    Task<(ServiceSession Session, Message Message)?> LockMessageAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>A conversa da sessão no canal dela (rastreada para alteração), ou <c>null</c> se ainda não existe.</summary>
    Task<Conversation?> FindSessionConversationAsync(ServiceSession session, CancellationToken cancellationToken);

    /// <summary>Cria a conversa da sessão com o idioma efetivo inicial, se ainda não existir, e a devolve (a existente nunca é alterada).</summary>
    Task<Conversation> GetOrCreateConversationAsync(ServiceSession session, DateTimeOffset now, Locale effectiveLocale, LocaleSource localeSource, CancellationToken cancellationToken);

    Task<Conversation?> FindConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>O contato do tenant, rastreado para alteração, ou <c>null</c> se não existe no tenant.</summary>
    Task<Contact?> FindContactAsync(Guid contactId, CancellationToken cancellationToken);

    Task<MessageTranslation?> FindTranslationAsync(Guid messageId, Locale targetLocale, CancellationToken cancellationToken);

    Task<Message?> FindMessageByProviderEventAsync(ChannelInstanceId channelInstanceId, string providerEventId, CancellationToken cancellationToken);

    /// <summary>A resposta já aceita para o turno, se houver (idempotência do registro: uma resposta por turno).</summary>
    Task<Message?> FindReplyByTurnAsync(Guid sessionId, Guid turnId, CancellationToken cancellationToken);

    Task<Message?> FindResendByRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Há um reenvio ainda em andamento desta mensagem (não terminal)?</summary>
    Task<bool> HasActiveResendAsync(Guid originalMessageId, CancellationToken cancellationToken);

    /// <summary>As últimas mensagens da sessão, em ordem cronológica crescente.</summary>
    Task<IReadOnlyList<Message>> RecentMessagesAsync(Guid sessionId, int take, CancellationToken cancellationToken);

    Task<Message?> LastOutboundAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<HandlingTransition?> FindTransitionAsync(Guid sessionId, Guid transitionId, CancellationToken cancellationToken);

    Task<SessionTransfer?> FindTransferAsync(Guid sessionId, Guid transferId, CancellationToken cancellationToken);

    void Add(Message message);

    void Add(HandlingTransition transition);

    void Add(SessionTransfer transfer);

    void Add(MessageTranslation translation);

    /// <exception cref="DuplicateProviderEventException">Outra transação gravou o mesmo evento do provedor no mesmo instante.</exception>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Confirma a unidade COM os eventos que a mudança de estado produziu: o evento é gravado no outbox do módulo na MESMA
    /// transação (BOUNDARIES.md §12.9), então ele existe se, e somente se, a mudança existe. Só depois da confirmação os eventos
    /// seguem para quem os consome (o relay do outbox, ou o publicador em memória do desenvolvimento).
    /// </summary>
    /// <exception cref="DuplicateProviderEventException">Outra transação gravou o mesmo evento do provedor no mesmo instante.</exception>
    Task CommitAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken);
}
