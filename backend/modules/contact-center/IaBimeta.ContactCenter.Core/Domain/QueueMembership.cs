using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

public enum QueueMemberRole
{
    /// <summary>Atende os atendimentos da fila.</summary>
    Agent = 1,

    /// <summary>Atende e acompanha a fila (o escopo de supervisão é definido nas próximas fatias).</summary>
    Supervisor = 2,
}

/// <summary>
/// Um atendente em uma fila. O usuário é um identificador opaco do Identity (DAD-2), validado pelo caso de uso. Um atendente de
/// outra unidade ou organização pode estar na fila: a equipe compartilhada é uma decisão explícita de quem gere a fila. A
/// chave é <c>(tenant_id, queue_id, user_id)</c>: um usuário aparece uma vez por fila.
/// </summary>
public sealed class QueueMembership : ITenantOwned
{
    private QueueMembership(TenantId tenantId, Guid queueId, Guid userId, QueueMemberRole role, Guid? addedBy, DateTimeOffset addedAt)
    {
        TenantId = tenantId;
        QueueId = queueId;
        UserId = userId;
        Role = role;
        AddedBy = addedBy;
        AddedAt = addedAt;
    }

    public TenantId TenantId { get; }

    public Guid QueueId { get; }

    public Guid UserId { get; }

    public QueueMemberRole Role { get; private set; }

    /// <summary>Quem colocou o atendente na fila (usuário do contexto), ou <c>null</c> se foi uma operação de sistema.</summary>
    public Guid? AddedBy { get; }

    public DateTimeOffset AddedAt { get; }

    public static QueueMembership Create(TenantId tenantId, Guid queueId, Guid userId, QueueMemberRole role, Guid? addedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (queueId == Guid.Empty || userId == Guid.Empty)
        {
            throw new ContactCenterValidationException("A fila e o atendente são obrigatórios.");
        }

        if (!Enum.IsDefined(role))
        {
            throw new ContactCenterValidationException("O papel do atendente na fila é inválido.");
        }

        return new QueueMembership(tenantId, queueId, userId, role, addedBy, now);
    }

    /// <summary>Muda o papel. Devolve <c>false</c>, sem mudar nada, se já era este.</summary>
    public bool ChangeRole(QueueMemberRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ContactCenterValidationException("O papel do atendente na fila é inválido.");
        }

        if (Role == role)
        {
            return false;
        }

        Role = role;
        return true;
    }
}
