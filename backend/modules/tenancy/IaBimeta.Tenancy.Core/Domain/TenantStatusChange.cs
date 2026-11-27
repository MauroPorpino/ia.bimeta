using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Tenancy.Domain;

/// <summary>
/// Histórico imutável (só inclusão) das mudanças de situação de uma empresa: situação anterior, nova, motivo, operador e instante.
/// Dado da plataforma (não pertence a um tenant).
/// </summary>
public sealed class TenantStatusChange
{
    private TenantStatusChange(Guid id, TenantId tenantId, TenantStatus fromStatus, TenantStatus toStatus, string reason, Guid? actorUserId, DateTimeOffset changedAt)
    {
        Id = id;
        TenantId = tenantId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Reason = reason;
        ActorUserId = actorUserId;
        ChangedAt = changedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public TenantStatus FromStatus { get; }

    public TenantStatus ToStatus { get; }

    public string Reason { get; }

    public Guid? ActorUserId { get; }

    public DateTimeOffset ChangedAt { get; }

    internal static TenantStatusChange Create(TenantId tenantId, TenantStatus from, TenantStatus to, string reason, Guid? actorUserId, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), tenantId, from, to, reason, actorUserId, now);
}
