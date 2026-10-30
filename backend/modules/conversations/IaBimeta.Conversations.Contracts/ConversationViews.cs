using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Contracts;

/// <summary>Uma página de resultados. O <see cref="NextCursor"/> é opaco e nulo na última página.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// Visão de uma sessão de atendimento (nunca a entidade). Não expõe época nem token de lane. <c>LastMessageSeq</c>, <c>HandlingPeriod</c> e
/// <c>PlacementEpoch</c> são só para quem chama em processo (o ContactCenter, para a fronteira do histórico, para a identidade do período de
/// atendimento a que um acesso está preso e para a identidade da colocação, a geração monotônica que avança a cada mudança de unidade ou setor
/// e prova a continuidade do vínculo entre a sessão e o lugar onde um acesso foi autorizado) e não fazem parte do JSON da API HTTP.
/// </summary>
public sealed record SessionView(
    Guid Id,
    Guid OrganizationId,
    Guid? BusinessUnitId,
    Guid ContactId,
    string? ContactDisplayName,
    ChannelInstanceId ChannelInstanceId,
    SessionStatus Status,
    HandlingMode HandlingMode,
    Guid? ResponderRef,
    long StateVersion,
    long LastInboundSeq,
    long AnsweredThroughSeq,
    bool HasActiveTurn,
    bool ReplyDeliveryFailed,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMessageAt,
    Locale? EffectiveLocale = null,
    LocaleSource? LocaleSource = null,
    Locale? ContactPreferredLocale = null,
    Guid? DepartmentId = null,
    [property: System.Text.Json.Serialization.JsonIgnore] long LastMessageSeq = 0,
    [property: System.Text.Json.Serialization.JsonIgnore] long HandlingPeriod = 0,
    [property: System.Text.Json.Serialization.JsonIgnore] long PlacementEpoch = 0);

/// <summary>
/// Visão de uma mensagem para quem lê o histórico (usuário autorizado). O texto de conteúdo que ainda não está pronto é
/// nulo: ninguém lê conteúdo que não foi interpretado.
/// </summary>
public sealed record MessageView(
    Guid Id,
    Guid SessionId,
    Guid ConversationId,
    MessageDirection Direction,
    MessageAuthor Author,
    Guid? AuthorRef,
    long MessageSeq,
    long? InboundSeq,
    MessageKind Kind,
    ContentState ContentState,
    string? Text,
    bool LateArrival,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ProviderTimestamp,
    DateTimeOffset ReceivedAt,
    DeliveryState? DeliveryState,
    Guid? ResendOfMessageId,
    ReplyKind? ReplyKind,
    long? CoveredThroughSeq,
    ChannelInstanceId ChannelInstanceId,
    Locale? Locale = null,
    MessageLocaleOrigin? LocaleOrigin = null);

/// <summary>
/// Um encaminhamento da sessão entre unidades (e setores): de onde, para onde, quem pediu, por quê e quando. O rastro é só
/// de acréscimo.
/// </summary>
/// <param name="RequestedBy">O usuário que pediu o encaminhamento, ou <c>null</c> se veio de um componente de sistema.</param>
public sealed record SessionTransferView(
    Guid TransferId,
    Guid SessionId,
    Guid? FromBusinessUnitId,
    Guid? FromDepartmentId,
    Guid ToBusinessUnitId,
    Guid? ToDepartmentId,
    Guid? RequestedBy,
    string Reason,
    long StateVersionAfter,
    DateTimeOffset TransferredAt);

/// <summary>
/// Tradução de uma mensagem, guardada à parte: o texto original da mensagem nunca é alterado nem sobrescrito. A origem
/// (<see cref="Provenance"/>) é um código curto de quem a produziu (por exemplo, <c>human</c>).
/// </summary>
public sealed record MessageTranslationView(
    Guid Id,
    Guid MessageId,
    Locale TargetLocale,
    Locale? SourceLocale,
    string Text,
    string Provenance,
    DateTimeOffset CreatedAt);
