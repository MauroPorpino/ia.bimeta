namespace IaBimeta.Conversations.Contracts.Events;

/// <summary>
/// Nomes e versões dos eventos do Conversations no envelope (<see cref="IaBimeta.SharedKernel.EventEnvelope"/>). O contrato
/// de cada evento pertence ao módulo emissor (COM-4). A <c>OrderingKey</c> de todos é a sessão.
/// </summary>
public static class ConversationEventTypes
{
    public const string InboundMessageRegistered = "conversations.inbound-message-registered";
    public const string MessageContentReady = "conversations.message-content-ready";
    public const string MessageContentFailed = "conversations.message-content-failed";
    public const string HandlingModeChanged = "conversations.handling-mode-changed";
    public const string SessionClosed = "conversations.session-closed";
    public const string OutboundMessageRequested = "conversations.outbound-message-requested";
    public const string AiReplyAccepted = "conversations.ai-reply-accepted";
    public const string OutboundMessageFailed = "conversations.outbound-message-failed";
    public const string SessionAttentionRaised = "conversations.session-attention-raised";
    public const string ConversationLocaleChanged = "conversations.conversation-locale-changed";
    public const string SessionTransferred = "conversations.session-transferred";
}

/// <summary>
/// Mensagem do cliente registrada com <c>InboundSeq</c>. Consumido pelo Agents (abre ou estende o turno). A instância de
/// canal permite carregar a configuração do número (agente, idioma, horário) sem consultar o Conversations.
/// </summary>
public sealed record InboundMessageRegisteredV1(
    Guid SessionId,
    Guid ConversationId,
    Guid MessageId,
    long InboundSeq,
    MessageKind Kind,
    ContentState ContentState,
    bool LateArrival,
    IaBimeta.SharedKernel.ChannelInstanceId ChannelInstanceId);

public sealed record MessageContentReadyV1(Guid SessionId, Guid MessageId, long InboundSeq);

public sealed record MessageContentFailedV1(Guid SessionId, Guid MessageId, long InboundSeq, string Reason);

public sealed record HandlingModeChangedV1(
    Guid SessionId,
    HandlingMode From,
    HandlingMode To,
    Guid? ResponderRef,
    long StateVersion,
    Guid TransitionId);

public sealed record SessionClosedV1(Guid SessionId, string Reason);

/// <summary>
/// Saída liberada para envio. Consumido pelo Channels. O <c>OperationId</c> é a chave de idempotência do efeito externo
/// (EFX): para reenvio deriva do <c>RequestId</c> da ação do atendente.
/// </summary>
/// <remarks>
/// A <c>ChannelInstanceId</c> diz de QUAL número a mensagem sai: o Sender usa exatamente essa instância, e confere que ela
/// pertence ao tenant do envelope antes de enviar (o Conversations não conhece o cadastro do Channels).
/// </remarks>
public sealed record OutboundMessageRequestedV1(
    Guid SessionId,
    Guid ConversationId,
    Guid MessageId,
    Guid OperationId,
    Guid? ResendOfMessageId,
    IaBimeta.SharedKernel.ChannelInstanceId ChannelInstanceId);

/// <summary>Informativo: encerra a rajada e o item de atenção correspondente no ContactCenter.</summary>
public sealed record AiReplyAcceptedV1(Guid SessionId, Guid TurnId, Guid MessageId, long AnsweredThroughSeq);

/// <summary>Falha definitiva de envio. Consumido pelo ContactCenter, NUNCA pelo Agents: falha de envio não dispara IA (ARQ-24).</summary>
public sealed record OutboundMessageFailedV1(Guid MessageId, Guid SessionId, long CoveredThroughSeq, string Reason);

public sealed record SessionAttentionRaisedV1(Guid SessionId, SessionAttentionReason Reason, int MessagesInBurst, int InterruptionsInBurst);

/// <summary>
/// O idioma efetivo da conversa mudou. Consumido pelo Agents (cancela o turno em andamento, escrito no idioma anterior) e
/// pelo ContactCenter. Não carrega conteúdo de mensagem.
/// </summary>
/// <summary>
/// A sessão foi encaminhada para outra unidade (e setor) da mesma organização. Consumido pelo Agents (cancela o turno em
/// andamento, montado para a unidade anterior) e pelo ContactCenter (refaz a fila). O rastro completo está na própria
/// sessão (<c>GetTransfersAsync</c>). Não carrega conteúdo de mensagem.
/// </summary>
public sealed record SessionTransferredV1(
    Guid SessionId,
    Guid TransferId,
    Guid? FromBusinessUnitId,
    Guid? FromDepartmentId,
    Guid ToBusinessUnitId,
    Guid? ToDepartmentId,
    long StateVersion);

public sealed record ConversationLocaleChangedV1(
    Guid SessionId,
    Guid ConversationId,
    IaBimeta.SharedKernel.Locale From,
    IaBimeta.SharedKernel.Locale To,
    LocaleSource Source,
    long StateVersion);
