using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Contracts.Commands;

/// <summary>
/// Autoridade do Conversations sobre o turno de IA e sobre a aceitação da resposta (ADR 0007). O Agents propõe, e o
/// Conversations decide: toda resposta carrega <c>TurnId</c>, a época do turno ativo, a <c>StateVersion</c> e a cerca de
/// entrada <c>basedOnInboundSeq</c>. A resposta defasada é SEMPRE rejeitada: o contrato não tem como pedir o contrário.
/// Só um contexto de sistema pode chamar.
/// </summary>
public interface ITurnCommands
{
    /// <summary>
    /// Comparar-e-trocar atômico: assume o turno ativo da sessão (qualquer turno anterior deixa de ser ativo no mesmo
    /// instante) e devolve o grant e o snapshot da MESMA leitura consistente.
    /// </summary>
    Task<BeginTurnResult> BeginTurnAsync(BeginTurnCommand command, CancellationToken cancellationToken = default);

    /// <summary>Libera o turno ativo se ainda for este turno e esta época. Idempotente.</summary>
    Task<EndTurnResult> EndTurnAsync(EndTurnCommand command, CancellationToken cancellationToken = default);

    /// <summary>Limpa o turno ativo e avança a época: a resposta de qualquer turno em andamento passa a ser recusada.</summary>
    Task<CancelTurnResult> CancelTurnAsync(CancelTurnCommand command, CancellationToken cancellationToken = default);

    /// <summary>Valida as cercas e, se todas passam, aceita a resposta numa única transação (CONVERSATION-LIFECYCLE.md §6.1).</summary>
    Task<RegisterAiReplyResult> RegisterAiReplyAsync(RegisterAiReplyCommand command, CancellationToken cancellationToken = default);

    /// <summary>Sinaliza a sessão (por exemplo, rajada longa) sem alterar responsável nem modo. Um sinal por rajada.</summary>
    Task<RaiseSessionAttentionResult> RaiseSessionAttentionAsync(RaiseSessionAttentionCommand command, CancellationToken cancellationToken = default);
}

/// <param name="LeaseToken">Token da lane do worker. Recusado se for menor que o maior já visto (worker com lane antiga).</param>
/// <param name="MaxSnapshotMessages">Quantas mensagens recentes entram no snapshot (limitado pelo Conversations).</param>
public sealed record BeginTurnCommand(Guid SessionId, Guid TurnId, long LeaseToken, int MaxSnapshotMessages = 50);

public enum BeginTurnOutcome
{
    Granted = 1,
    SessionNotFound = 2,
    SessionClosed = 3,
    NotAiResponder = 4,
    LeaseTokenStale = 5,
}

/// <param name="Epoch">A época deste turno. A resposta precisa devolvê-la.</param>
/// <param name="BasedOnInboundSeq">O <c>LastInboundSeq</c> do snapshot: a cerca de entrada que a resposta deve citar.</param>
public sealed record TurnGrant(long Epoch, long BasedOnInboundSeq, long StateVersion);

/// <summary>
/// O contexto de UMA sessão para o turno (TEN-5: nenhuma API aceita várias). O texto de conteúdo que não está pronto é
/// nulo: conteúdo indisponível nunca chega ao modelo (I9).
/// </summary>
/// <param name="ConversationId">A conversa de destino da resposta.</param>
/// <param name="EffectiveLocale">O idioma em que a resposta deve ser escrita: o efetivo da conversa, não o do tenant.</param>
/// <param name="LocaleSource">De onde veio a escolha (uma suposição quando é <see cref="LocaleSource.TenantDefault"/>).</param>
/// <param name="ChannelInstanceId">O número por onde a conversa acontece: o Agents carrega dele a configuração do agente.</param>
/// <param name="BusinessUnitId">A unidade responsável no momento do turno (muda por transferência, que avança a versão do estado).</param>
/// <param name="DepartmentId">O setor responsável, se houver.</param>
public sealed record TurnSnapshot(
    Guid SessionId,
    HandlingMode HandlingMode,
    long StateVersion,
    long LastInboundSeq,
    long AnsweredThroughSeq,
    bool ReplyDeliveryFailed,
    DeliveryState? LastReplyDeliveryState,
    IReadOnlyList<SnapshotMessage> Messages,
    Guid ConversationId,
    Locale EffectiveLocale,
    LocaleSource LocaleSource,
    ChannelInstanceId ChannelInstanceId,
    Guid? BusinessUnitId,
    Guid? DepartmentId);

public sealed record SnapshotMessage(
    Guid MessageId,
    MessageDirection Direction,
    MessageAuthor Author,
    long MessageSeq,
    long? InboundSeq,
    MessageKind Kind,
    ContentState ContentState,
    string? Text,
    bool LateArrival,
    bool IsPending,
    DateTimeOffset OccurredAt,
    Locale? Locale = null);

public sealed record BeginTurnResult(BeginTurnOutcome Outcome, TurnGrant? Grant, TurnSnapshot? Snapshot);

public sealed record EndTurnCommand(Guid SessionId, Guid TurnId, long Epoch, TurnEndReason Reason);

public enum EndTurnOutcome
{
    /// <summary>O turno era o ativo desta época e foi liberado.</summary>
    Ended = 1,

    /// <summary>O turno já não era o ativo (já terminado, substituído ou cancelado): nada mudou.</summary>
    NotActive = 2,

    SessionNotFound = 3,
}

public sealed record EndTurnResult(EndTurnOutcome Outcome);

/// <param name="Reason">Motivo curto (por exemplo, <c>manual</c>), só letras minúsculas, números e <c>_.-</c>.</param>
public sealed record CancelTurnCommand(Guid SessionId, string Reason);

public sealed record CancelTurnResult(bool SessionFound, bool TurnWasActive, long TurnEpoch);

/// <param name="TurnId">Chave de idempotência do registro: no máximo uma resposta aceita por turno.</param>
/// <param name="Epoch">A época devolvida pelo <c>BeginTurn</c>.</param>
/// <param name="ExpectedStateVersion">A versão do estado em que o turno se baseou.</param>
/// <param name="BasedOnInboundSeq">O <c>LastInboundSeq</c> do snapshot: a cerca de entrada.</param>
/// <param name="TargetConversationId">A conversa de destino: precisa pertencer à sessão.</param>
/// <param name="AiRequestIds">As chamadas ao modelo que geraram a resposta (correlação e auditoria).</param>
public sealed record RegisterAiReplyCommand(
    Guid SessionId,
    Guid TurnId,
    long Epoch,
    long ExpectedStateVersion,
    long BasedOnInboundSeq,
    ReplyKind ReplyKind,
    Guid TargetConversationId,
    string Text,
    IReadOnlyList<Guid> AiRequestIds);

public enum RegisterReplyOutcome
{
    /// <summary>Aceita: cobre logicamente até <c>basedOnInboundSeq</c> e foi liberada para envio.</summary>
    Accepted = 1,

    /// <summary>Este turno já tinha uma resposta aceita (repetição idempotente).</summary>
    AlreadyRegistered = 2,

    SessionNotFound = 3,

    /// <summary>O turno não é o ativo desta época (substituído, cancelado, terminado ou worker com lane antiga).</summary>
    TurnNotActive = 4,

    NotAiResponder = 5,

    /// <summary>A versão do estado mudou (por exemplo, houve transição de modo).</summary>
    StaleState = 6,

    /// <summary>Chegou mensagem do cliente depois do snapshot: a resposta está defasada e é sempre recusada.</summary>
    StaleInput = 7,

    /// <summary>Tipo ou conteúdo não permitido, ou destino que não pertence à sessão.</summary>
    ShapeNotAllowed = 8,
}

public sealed record RegisterAiReplyResult(RegisterReplyOutcome Outcome, Guid? MessageId, long? AnsweredThroughSeq, long? LastInboundSeq);

/// <param name="MessagesInBurst">Mensagens do cliente na rajada (informativo, vem do Agents).</param>
/// <param name="InterruptionsInBurst">Interrupções somadas a descartes na rajada (informativo).</param>
public sealed record RaiseSessionAttentionCommand(Guid SessionId, SessionAttentionReason Reason, int MessagesInBurst, int InterruptionsInBurst);

public enum RaiseAttentionOutcome
{
    Raised = 1,

    /// <summary>Esta rajada já foi sinalizada (um sinal por rajada); novo sinal só depois de uma resposta aceita.</summary>
    AlreadyRaised = 2,

    SessionNotFound = 3,

    SessionClosed = 4,
}

public sealed record RaiseSessionAttentionResult(RaiseAttentionOutcome Outcome);
