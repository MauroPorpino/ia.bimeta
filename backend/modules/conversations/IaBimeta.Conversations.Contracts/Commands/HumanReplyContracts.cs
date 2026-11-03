namespace IaBimeta.Conversations.Contracts.Commands;

// Os contratos da 7ª operação do gateway elevado do ContactCenter (ADR 0009, item 9; ADR 0014, seção A): o atendente humano responde ao cliente.
// Nesta fatia (A1) existem SÓ os tipos: a operação (o método na porta, a idempotência por OperationId, a cerca, a pré-condição, o hash canônico do efeito e a
// gravação atômica da mensagem e do evento) nasce na fatia A4, junto da implementação. Fora do escopo aprovado do MVP e, por isso, ausentes daqui:
// destinatários (e-mail) e anexos (DP-05); entram depois como campos aditivos, com valor padrão.

/// <summary>
/// A resposta de um atendente humano a uma conversa externa (7ª operação). O tenant vem do contexto confiável, nunca do comando. O autor humano original
/// vai na intenção persistida do ContactCenter e é reaplicado por quem retomar a operação: troca de processo nunca troca o autor.
/// </summary>
/// <param name="SessionId">A sessão que recebe a resposta.</param>
/// <param name="OperationId">
/// A chave de idempotência durável: gerada pelo servidor na primeira criação da intenção e reutilizada nas repetições do mesmo pedido do cliente. É também a
/// chave de idempotência do envio ao provedor, e não muda entre as tentativas.
/// </param>
/// <param name="AuthorUserId">O autor humano original, validado no Identity.</param>
/// <param name="Precondition">A base sobre a qual a resposta foi autorizada. Deriva da intenção persistida, é imutável e é conferida de forma atômica com a gravação.</param>
/// <param name="ExpectedStateVersion">A única parte renovável da base: pode ser relida do Conversations e repetida com a MESMA pré-condição.</param>
/// <param name="FenceToken">O token da cerca do executor: decide quem pode escrever, e não integra a identidade nem a autorização do pedido.</param>
/// <param name="Content">O conteúdo da resposta.</param>
public sealed record RegisterHumanReplyCommand(
    Guid SessionId,
    Guid OperationId,
    Guid AuthorUserId,
    ReplyPrecondition Precondition,
    long ExpectedStateVersion,
    long FenceToken,
    ReplyContent Content);

/// <summary>
/// A pré-condição atômica da resposta humana: o atendimento sobre o qual quem autorizou decidiu. Divergência de modo, de período, de colocação ou do
/// responsável exigido resulta em <see cref="RegisterHumanReplyOutcome.HandlingChanged"/>, sem gravar. Corrige a <see cref="HandlingPrecondition"/> geral, que
/// não distingue a política exclusiva da colaborativa.
/// </summary>
/// <param name="HandlingPeriod">O período de atendimento humano em que a resposta foi autorizada.</param>
/// <param name="PlacementEpoch">A geração da colocação da sessão. Base sem ela deixa a intenção inerte (ADR 0010, D.23).</param>
/// <param name="ResponderMatch">Se o responsável da sessão precisa ser exatamente o da base (<see cref="ResponderMatch.Exact"/>) ou pode mudar (<see cref="ResponderMatch.Any"/>).</param>
/// <param name="ResponderRef">O responsável exigido. Só vale com <see cref="ResponderMatch.Exact"/>; com <see cref="ResponderMatch.Any"/> não se exige responsável.</param>
/// <param name="Mode">O modo de atendimento exigido: o atendimento humano.</param>
public sealed record ReplyPrecondition(
    long HandlingPeriod,
    long PlacementEpoch,
    ResponderMatch ResponderMatch,
    Guid? ResponderRef = null,
    HandlingMode Mode = HandlingMode.HumanAssisted);

/// <summary>Como o responsável da sessão é conferido na pré-condição da resposta humana.</summary>
public enum ResponderMatch
{
    /// <summary>Política exclusiva: o autor é o responsável, e a troca de responsável invalida a resposta.</summary>
    Exact = 1,

    /// <summary>Política colaborativa: o responsável pode mudar sem invalidar a resposta de um colaborador autorizado. Modo, período e colocação continuam exigidos.</summary>
    Any = 2,
}

/// <summary>O conteúdo efetivo de uma resposta humana, tal como foi materializado na intenção (nunca recalculado a partir de dado vivo).</summary>
/// <param name="Text">O texto da resposta.</param>
/// <param name="InReplyToMessageId">A mensagem citada, pelo identificador imutável dela, se houver.</param>
/// <param name="ChannelData">Metadados de envio que o adaptador declara relevantes. Enquanto o <see cref="IaBimeta.Conversations.Contracts.ChannelData"/> não for persistido (fatia A3), é recusado.</param>
public sealed record ReplyContent(string Text, Guid? InReplyToMessageId = null, ChannelData? ChannelData = null);

/// <summary>
/// O resultado da resposta humana. Só <see cref="Applied"/> e <see cref="AlreadyApplied"/> deixam efeito; uma operação rejeitada não deixa registro, e repetir
/// reavalia.
/// </summary>
public enum RegisterHumanReplyOutcome
{
    /// <summary>A mensagem de saída e o evento de envio foram gravados juntos.</summary>
    Applied = 1,

    /// <summary>O mesmo <c>OperationId</c> com o mesmo efeito já tinha sido aplicado: devolve o resultado original, sem mensagem nem evento novos.</summary>
    AlreadyApplied = 2,

    /// <summary>O mesmo <c>OperationId</c> com outro efeito (outra sessão, autor ou conteúdo): recusado, sem expor os dados originais.</summary>
    OperationIdReused = 3,

    SessionNotFound = 4,

    SessionClosed = 5,

    /// <summary>A pré-condição divergiu (modo, período, colocação ou responsável exigido): nada foi gravado, e a intenção não é "atualizada".</summary>
    HandlingChanged = 6,

    /// <summary>A versão do estado mudou e a pré-condição ainda vale: o chamador relê a versão e repete com a MESMA pré-condição.</summary>
    StaleState = 7,

    /// <summary>Há uma cerca maior: outro executor assumiu, e este não escreve mais.</summary>
    FencedOut = 8,

    ContentRejected = 9,

    AuthorNotFound = 10,
}

/// <summary>Se a cerca do executor que reconheceu um efeito já aplicado ainda é a vigente. Informativo: nunca concede autoridade para escrever.</summary>
public enum FenceObservation
{
    Current = 1,

    /// <summary>A cerca da sessão já passou deste executor: ele reconhece o efeito, mas devolve a finalização ao sucessor.</summary>
    Overtaken = 2,
}

/// <summary>
/// O resultado da resposta humana. Os campos de efeito existem em <see cref="RegisterHumanReplyOutcome.Applied"/> e em
/// <see cref="RegisterHumanReplyOutcome.AlreadyApplied"/>; <see cref="Fence"/> e <see cref="CurrentDeliveryState"/> só em <c>AlreadyApplied</c> (leitura, não
/// parte do efeito original).
/// </summary>
public sealed record RegisterHumanReplyResult(
    RegisterHumanReplyOutcome Outcome,
    Guid? MessageId = null,
    long? MessageSeq = null,
    long? StateVersionAfter = null,
    DateTimeOffset? RegisteredAt = null,
    FenceObservation? Fence = null,
    DeliveryState? CurrentDeliveryState = null);
