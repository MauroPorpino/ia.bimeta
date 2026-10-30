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

/// <param name="MessageSeq">
/// A sequência da última mensagem da sessão no instante da transição: quem projeta o fim de um atendimento usa-a como fronteira
/// estável do que ele pôde ver (não o instante em que o evento foi processado). Ausente em eventos anteriores a este campo.
/// </param>
/// <param name="HandlingPeriod">
/// O período de atendimento humano da sessão (<c>SessionView.HandlingPeriod</c>) DEPOIS desta transição, capturado na mesma transação que a gravou. Para
/// uma transição que leva à IA é o período do atendimento que ACABOU (o período só avança ao entrar em humano vindo de outro modo); para a que leva ao
/// humano, vindo da IA, é o do atendimento NOVO. Ausente em eventos anteriores a este campo: a identidade é DESCONHECIDA, e quem a usa nunca a inventa.
/// </param>
/// <param name="PlacementEpoch">
/// A geração da colocação da sessão (<c>SessionView.PlacementEpoch</c>) no instante da transição (a transição não a muda). Ausente em eventos anteriores a
/// este campo: desconhecida.
/// </param>
public sealed record HandlingModeChangedV1(
    Guid SessionId,
    HandlingMode From,
    HandlingMode To,
    Guid? ResponderRef,
    long StateVersion,
    Guid TransitionId,
    long? MessageSeq = null,
    long? HandlingPeriod = null,
    long? PlacementEpoch = null);

/// <param name="LastMessageSeq">A sequência da última mensagem da sessão ao encerrar (nenhuma chega depois). Ausente em eventos anteriores a este campo.</param>
public sealed record SessionClosedV1(Guid SessionId, string Reason, long? LastMessageSeq = null);

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
/// <param name="MessageSeq">
/// A sequência da última mensagem da sessão no instante do encaminhamento: a fronteira estável do que a unidade (e a fila) de origem pôde ver.
/// O que chegar depois é da unidade de destino. Quem projeta o encaminhamento a usa para limitar o histórico de quem ficou para trás. Ausente
/// em eventos anteriores a este campo: a fronteira é desconhecida, e quem a usa fecha o histórico em vez de abri-lo.
/// </param>
/// <param name="FromPlacementEpoch">
/// A geração da colocação da sessão ANTES do encaminhamento, capturada na mesma transação que o gravou: a geração da qual a sessão SAIU. É a origem do
/// limite de histórico (<paramref name="MessageSeq"/>): só quem estava preso a esta geração pode usá-lo. Ausente em eventos anteriores a este campo: a
/// origem é desconhecida, e o limite não pode ser certificado.
/// </param>
/// <param name="ToPlacementEpoch">A geração da colocação DEPOIS do encaminhamento (a anterior mais uma). Ausente em eventos anteriores a este campo.</param>
public sealed record SessionTransferredV1(
    Guid SessionId,
    Guid TransferId,
    Guid? FromBusinessUnitId,
    Guid? FromDepartmentId,
    Guid ToBusinessUnitId,
    Guid? ToDepartmentId,
    long StateVersion,
    long? MessageSeq = null,
    long? FromPlacementEpoch = null,
    long? ToPlacementEpoch = null);

public sealed record ConversationLocaleChangedV1(
    Guid SessionId,
    Guid ConversationId,
    IaBimeta.SharedKernel.Locale From,
    IaBimeta.SharedKernel.Locale To,
    LocaleSource Source,
    long StateVersion);
