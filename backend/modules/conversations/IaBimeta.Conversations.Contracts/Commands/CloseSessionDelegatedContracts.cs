namespace IaBimeta.Conversations.Contracts.Commands;

// Os contratos da 9ª operação do gateway elevado do ContactCenter (ADR 0009, item 10; ADR 0016, seção M e AC-ENC01 a AC-ENC09): o encerramento do
// atendimento por quem o conduz. Nesta fatia (A1) existem SÓ os tipos: a operação (o método na porta, a ordem de validação dentro da trava da sessão, a
// idempotência antes da cerca, a pré-condição, a fronteira certificada e a publicação de <c>SessionClosedV1</c>) nasce na fatia A6. O <c>CloseSessionAsync</c>
// direto do Conversations (<see cref="CloseSessionCommand"/>) NÃO muda. A extensão do satélite do WebChat (<c>SatelliteDecision</c>,
// <c>SatelliteAckThroughSeq</c> e os resultados que dela dependem) é da 5.2w e continua Proposta: fica fora daqui e entra depois como campos aditivos.

/// <summary>
/// O encerramento do atendimento pedido pelo ContactCenter em nome de quem o conduz (9ª operação). O tenant vem do contexto confiável, nunca do comando. A
/// decisão de acesso é do ContactCenter (autorização sobre o responsável efetivo, ADR 0010, D.19); o Conversations executa com o contexto de sistema do mesmo
/// tenant. Só atendimento humano com item de fila é encerrável por este caso de uso.
/// </summary>
/// <param name="SessionId">A sessão a encerrar.</param>
/// <param name="OperationId">A chave de idempotência: o <c>RequestId</c> do cliente. Idempotência por <c>(tenant, OperationId)</c>, avaliada ANTES da cerca.</param>
/// <param name="AuthorUserId">O autor humano original, quando houver; o rastro dele fica no ContactCenter (item 5 do ADR 0009).</param>
/// <param name="ActorOrigin">A origem do pedido, para a auditoria (por exemplo, <c>contact-center</c>).</param>
/// <param name="Precondition">A base sobre a qual o encerramento foi autorizado (modo, período, colocação e responsável). Deriva da intenção persistida e é imutável.</param>
/// <param name="ExpectedStateVersion">A única parte renovável da base.</param>
/// <param name="FenceToken">O token da cerca do executor.</param>
/// <param name="ReasonCode">O motivo, um código curto no mesmo formato dos demais motivos (minúsculas, números e <c>_.-</c>).</param>
public sealed record CloseSessionDelegatedCommand(
    Guid SessionId,
    Guid OperationId,
    Guid? AuthorUserId,
    string ActorOrigin,
    HandlingPrecondition Precondition,
    long ExpectedStateVersion,
    long FenceToken,
    string ReasonCode);

/// <summary>
/// O resultado do encerramento delegado. A idempotência é avaliada antes das condições, então uma recusa definitiva prova que NADA foi aplicado, e a repetição
/// do mesmo <c>OperationId</c> devolve a mesma recusa.
/// </summary>
public enum CloseSessionDelegatedOutcome
{
    /// <summary>A sessão foi encerrada agora, com fronteira certificada, e <c>SessionClosedV1</c> foi publicado.</summary>
    Applied = 1,

    /// <summary>O mesmo <c>OperationId</c> com o mesmo efeito já tinha sido aplicado: devolve o resultado original, sem evento novo.</summary>
    AlreadyApplied = 2,

    /// <summary>A sessão já estava encerrada por outra via: converge sem erro, e o item do ContactCenter conclui pela projeção.</summary>
    AlreadyClosed = 3,

    /// <summary>A pré-condição divergiu (modo, período, colocação ou responsável): nada foi aplicado.</summary>
    HandlingChanged = 4,

    /// <summary>A versão do estado mudou e a pré-condição ainda vale: o chamador relê a versão e repete com a MESMA pré-condição.</summary>
    StaleState = 5,

    /// <summary>Há uma cerca maior: outro executor assumiu, e este não escreve mais.</summary>
    FencedOut = 6,

    SessionNotFound = 7,

    /// <summary>O mesmo <c>OperationId</c> com outro efeito (outra sessão, autor, motivo ou base): recusado, sem expor os dados originais.</summary>
    OperationIdReused = 8,
}

/// <summary>
/// O resultado do encerramento delegado. <paramref name="StateVersionAfter"/> e <paramref name="LastMessageSeq"/> (a fronteira estável do que a sessão recebeu)
/// existem em <see cref="CloseSessionDelegatedOutcome.Applied"/> e <see cref="CloseSessionDelegatedOutcome.AlreadyApplied"/>; <paramref name="Reason"/> e
/// <paramref name="LastMessageSeq"/> existem em <see cref="CloseSessionDelegatedOutcome.AlreadyClosed"/>.
/// </summary>
public sealed record CloseSessionDelegatedResult(
    CloseSessionDelegatedOutcome Outcome,
    long? StateVersionAfter = null,
    long? LastMessageSeq = null,
    string? Reason = null);
