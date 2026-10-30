using IaBimeta.ContactCenter.Domain;

namespace IaBimeta.ContactCenter.Presentation;

/// <summary>Nenhum DTO aceita tenant nem usuário que age: o tenant e o usuário são sempre os do contexto confiável criado pelo host.</summary>
public sealed record CreateServiceQueueRequest(Guid OrganizationId, Guid? BusinessUnitId, Guid? DepartmentId, string Name);

public sealed record SetQueueMemberRequest(QueueMemberRole Role);

/// <param name="QueueId">A fila pedida, ou nula para o roteamento decidir pelas regras da organização.</param>
/// <param name="RequestId">A chave de idempotência do pedido: repetir o mesmo valor devolve o mesmo resultado, sem repetir o efeito.</param>
/// <param name="Reason">Código curto do motivo (por exemplo, <c>ai-requested</c>), sem texto livre. Padrão: <c>handoff</c>.</param>
public sealed record RequestHandoffRequest(Guid SessionId, Guid? QueueId, Guid RequestId, string? Reason);

public sealed record ItemActionRequest(Guid RequestId, string? Reason);

public sealed record AssignItemRequest(Guid UserId, Guid RequestId, string? Reason);

public sealed record TransferItemRequest(Guid QueueId, Guid? UserId, Guid RequestId, string? Reason);

public sealed record QueuePolicyRequest(Guid? FallbackQueueId, OutOfHoursBehavior OutOfHours, NoAgentBehavior NoAgent, int DefaultPriority, int? MaxItemsPerAgent);

public sealed record ScheduleWindowRequest(DayOfWeek DayOfWeek, string Start, string End);

public sealed record SetScheduleRequest(string TimeZone, IReadOnlyList<ScheduleWindowRequest>? Windows, IReadOnlyList<DateOnly>? ClosedDates);

public sealed record CreateRoutingRuleRequest(
    Guid OrganizationId, string Name, Guid? ChannelInstanceId, Guid? BusinessUnitId, Guid? DepartmentId, Guid QueueId, int Priority, int Order);

public sealed record RoutingPreviewRequest(Guid SessionId);

public sealed record SetPresenceRequest(AgentStatus Status);

public sealed record SetPriorityRequest(int Priority, Guid RequestId, string? Reason);
