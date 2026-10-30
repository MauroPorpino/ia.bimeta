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
    DateTimeOffset UpdatedAt,
    string? TimeZoneId = null,
    OutOfHoursBehavior OutOfHours = OutOfHoursBehavior.Accept,
    NoAgentBehavior NoAgent = NoAgentBehavior.Wait,
    Guid? FallbackQueueId = null,
    int DefaultPriority = 0,
    int? MaxItemsPerAgent = null)
{
    internal static ServiceQueueView From(ServiceQueue queue) => new(
        queue.Id, queue.OrganizationId, queue.BusinessUnitId, queue.DepartmentId, queue.Name, queue.Status, queue.Version, queue.CreatedAt, queue.UpdatedAt,
        queue.TimeZoneId, queue.OutOfHours, queue.NoAgent, queue.FallbackQueueId, queue.DefaultPriority, queue.MaxItemsPerAgent);
}

/// <summary>Uma faixa semanal de atendimento, em horas e minutos do fuso da fila (início inclusivo, fim exclusivo; <c>24:00</c> é o fim do dia).</summary>
public sealed record ScheduleWindowView(DayOfWeek DayOfWeek, string Start, string End);

/// <summary>O horário de atendimento de uma fila e se ela atende agora. Sem fuso, a fila atende sempre.</summary>
public sealed record QueueScheduleView(
    Guid QueueId,
    string? TimeZoneId,
    IReadOnlyList<ScheduleWindowView> Windows,
    IReadOnlyList<DateOnly> ClosedDates,
    OutOfHoursBehavior OutOfHours,
    bool OpenNow);

public sealed record RoutingRuleView(
    Guid Id,
    Guid OrganizationId,
    string Name,
    Guid? ChannelInstanceId,
    Guid? BusinessUnitId,
    Guid? DepartmentId,
    Guid QueueId,
    int Priority,
    int Order,
    bool Enabled,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static RoutingRuleView From(RoutingRule rule) => new(
        rule.Id, rule.OrganizationId, rule.Name, rule.ChannelInstanceId, rule.BusinessUnitId, rule.DepartmentId, rule.QueueId, rule.Priority,
        rule.Order, rule.Enabled, rule.Version, rule.CreatedAt, rule.UpdatedAt);
}

public sealed record AgentPresenceView(Guid UserId, AgentStatus Status, DateTimeOffset? UpdatedAt);

/// <summary>Quem pode atender a fila agora e quanto ela tem esperando.</summary>
public sealed record QueueAvailabilityView(Guid QueueId, int Members, int AvailableAgents, int WaitingItems, bool OpenNow);

/// <summary>
/// Um destino possível para o atendimento, na ordem em que o roteamento o tentaria. <c>Notes</c> são códigos curtos do caminho
/// (<c>rule</c>, <c>out-of-hours:fallback</c>, <c>no-agent:fallback</c>, <c>closed-wait</c>, <c>no-agent-wait</c>).
/// </summary>
public sealed record RouteCandidateView(
    Guid QueueId,
    int Priority,
    Guid? RoutingRuleId,
    Guid? TargetBusinessUnitId,
    Guid? TargetDepartmentId,
    IReadOnlyList<string> Notes);

/// <summary>A decisão de roteamento de uma conversa (sem executar nada): os destinos possíveis e o que cada fila recusada alegou.</summary>
public sealed record RoutingDecisionView(IReadOnlyList<RouteCandidateView> Candidates, IReadOnlyList<RouteRefusalView> Refusals);

public sealed record RouteRefusalView(Guid QueueId, ContactCenterRule Rule);

public sealed record QueueMemberView(Guid QueueId, Guid UserId, QueueMemberRole Role, Guid? AddedBy, DateTimeOffset AddedAt)
{
    internal static QueueMemberView From(QueueMembership membership) => new(
        membership.QueueId, membership.UserId, membership.Role, membership.AddedBy, membership.AddedAt);
}

/// <summary>A operação que espera o Conversations. Enquanto existe, nenhuma outra operação muda o item.</summary>
public sealed record PendingOperationView(
    PendingKind Kind,
    Guid OperationId,
    Guid QueueId,
    Guid? UserId,
    int Attempts,
    string? LastError,
    DateTimeOffset StartedAt);

/// <summary>Uma conversa na fila de atendimento (nunca a entidade).</summary>
public sealed record QueueItemView(
    Guid Id,
    Guid SessionId,
    Guid OrganizationId,
    Guid? BusinessUnitId,
    Guid? DepartmentId,
    Guid QueueId,
    QueueItemStatus Status,
    Guid? AssignedUserId,
    long Version,
    DateTimeOffset EnteredAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    PendingOperationView? Pending,
    DateTimeOffset UpdatedAt,
    int Priority = 0,
    Guid? RoutingRuleId = null)
{
    internal static QueueItemView From(QueueItem item) => new(
        item.Id, item.SessionId, item.OrganizationId, item.BusinessUnitId, item.DepartmentId, item.QueueId, item.Status, item.AssignedUserId,
        item.Version, item.EnteredAt, item.AssignedAt, item.ClosedAt, item.CloseReason,
        item.Pending is { } p ? new PendingOperationView(p.Kind, p.OperationId, p.QueueId, p.UserId, item.PendingAttempts, item.PendingLastError, p.StartedAt) : null,
        item.UpdatedAt, item.Priority, item.RoutingRuleId);
}

public sealed record QueueItemHistoryView(
    Guid Id,
    Guid ItemId,
    Guid SessionId,
    Guid OperationId,
    HistoryKind Kind,
    Guid? FromQueueId,
    Guid ToQueueId,
    Guid? FromUserId,
    Guid? ToUserId,
    Guid? ActorUserId,
    string Reason,
    long? StateVersionAfter,
    DateTimeOffset OccurredAt,
    int? FromPriority = null,
    int? ToPriority = null)
{
    internal static QueueItemHistoryView From(QueueItemHistory h) => new(
        h.Id, h.ItemId, h.SessionId, h.OperationId, h.Kind, h.FromQueueId, h.ToQueueId, h.FromUserId, h.ToUserId, h.ActorUserId, h.Reason,
        h.StateVersionAfter, h.OccurredAt, h.FromPriority, h.ToPriority);
}

public enum OperationOutcome
{
    /// <summary>A operação foi aplicada agora.</summary>
    Completed = 1,

    /// <summary>A mesma operação (mesmo identificador) já tinha sido aplicada: nada mudou (repetição idempotente).</summary>
    AlreadyApplied = 2,

    /// <summary>A intenção foi registrada, mas o Conversations ainda não confirmou. Repetir com o mesmo identificador conclui.</summary>
    Pending = 3,

    /// <summary>A sessão terminou: o item foi encerrado e a operação perdeu o sentido.</summary>
    SessionEnded = 4,
}

public sealed record QueueItemOperationResult(QueueItemView Item, OperationOutcome Outcome);
