using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Rastro de um encaminhamento da sessão entre unidades (e setores): de onde, para onde, quem pediu, por quê e quando. É o
/// que torna <c>TransferSession</c> idempotente por <c>TransferId</c> (a chave é <c>(tenant_id, session_id, transfer_id)</c>) e
/// o que permite reconstruir por onde a conversa passou. Só de acréscimo: nunca é alterado nem apagado.
/// </summary>
public sealed class SessionTransfer : ITenantOwned
{
    private SessionTransfer()
    {
        TenantId = null!;
        Reason = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid TransferId { get; private set; }

    public Guid? FromBusinessUnitId { get; private set; }

    public Guid? FromDepartmentId { get; private set; }

    public Guid ToBusinessUnitId { get; private set; }

    public Guid? ToDepartmentId { get; private set; }

    /// <summary>O usuário que pediu; nulo quando veio de um componente de sistema (o ContactCenter, por exemplo).</summary>
    public Guid? RequestedBy { get; private set; }

    public string Reason { get; private set; }

    public long StateVersionAfter { get; private set; }

    public DateTimeOffset TransferredAt { get; private set; }

    public static SessionTransfer Create(
        TenantId tenantId,
        Guid sessionId,
        Guid transferId,
        Guid? fromBusinessUnitId,
        Guid? fromDepartmentId,
        Guid toBusinessUnitId,
        Guid? toDepartmentId,
        Guid? requestedBy,
        string reason,
        long stateVersionAfter,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        return new SessionTransfer
        {
            TenantId = tenantId,
            SessionId = sessionId,
            TransferId = transferId,
            FromBusinessUnitId = fromBusinessUnitId,
            FromDepartmentId = fromDepartmentId,
            ToBusinessUnitId = toBusinessUnitId,
            ToDepartmentId = toDepartmentId,
            RequestedBy = requestedBy,
            Reason = ConversationLimits.ValidateReason(reason),
            StateVersionAfter = stateVersionAfter,
            TransferredAt = now,
        };
    }
}
