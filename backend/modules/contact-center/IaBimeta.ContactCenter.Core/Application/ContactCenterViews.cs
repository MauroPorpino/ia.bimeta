using IaBimeta.ContactCenter.Domain;

namespace IaBimeta.ContactCenter.Application;

public sealed record ServiceQueueView(
    Guid Id,
    Guid OrganizationId,
    Guid? BusinessUnitId,
    Guid? DepartmentId,
    string Name,
    QueueStatus Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static ServiceQueueView From(ServiceQueue queue) => new(
        queue.Id, queue.OrganizationId, queue.BusinessUnitId, queue.DepartmentId, queue.Name, queue.Status, queue.Version, queue.CreatedAt, queue.UpdatedAt);
}

public sealed record QueueMemberView(Guid QueueId, Guid UserId, QueueMemberRole Role, Guid? AddedBy, DateTimeOffset AddedAt)
{
    internal static QueueMemberView From(QueueMembership membership) => new(
        membership.QueueId, membership.UserId, membership.Role, membership.AddedBy, membership.AddedAt);
}
