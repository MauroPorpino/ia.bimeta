namespace IaBimeta.Audit.Contracts.Commands;

/// <summary>
/// A autoria e o instante ORIGINAIS de uma entrada de auditoria que foi guardada para ser entregue depois: quem agiu, por qual origem e
/// quando. Quem guarda a entrada (um outbox de módulo) os captura do contexto confiável e do relógio NO MOMENTO da operação.
/// </summary>
/// <param name="ActorUserId">O usuário que agiu; nulo em contexto de sistema.</param>
/// <param name="ActorOrigin">A origem do contexto (por exemplo, <c>session</c>), no mesmo formato aceito pela trilha.</param>
/// <param name="OccurredAt">Quando a operação aconteceu (não quando a entrada foi entregue).</param>
public sealed record AuditProvenance(Guid? ActorUserId, string ActorOrigin, DateTimeOffset OccurredAt);

/// <summary>
/// A entrega, à trilha, de uma entrada guardada antes (por um outbox de módulo): grava com a autoria e o instante ORIGINAIS, e não com os
/// de quem entrega (o job de reconciliação, por exemplo, que não é o autor do que aconteceu nem aconteceu agora). É uma capacidade
/// distinta de <see cref="IAuditWriter"/> de propósito: só o código de entrega de um outbox a usa, e o <b>tenant continua sendo o do
/// <c>RequestContext</c></b> (nunca um parâmetro): quem entrega abre o contexto do tenant da entrada. A entrada deve trazer uma chave de
/// idempotência (<see cref="AuditEntry.IdempotencyKey"/>), para que a entrega repetida não duplique o evento.
/// </summary>
public interface IAuditRecoveryWriter
{
    /// <exception cref="IaBimeta.SharedKernel.TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task WriteRecoveredAsync(AuditEntry entry, AuditProvenance provenance, CancellationToken cancellationToken = default);
}
