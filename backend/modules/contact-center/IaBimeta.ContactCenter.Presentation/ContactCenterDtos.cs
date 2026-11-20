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

/// <summary>A resposta do atendente: o <c>RequestId</c> é a chave de idempotência (repetir o mesmo pedido devolve a mesma mensagem).</summary>
public sealed record ReplyToItemRequest(Guid RequestId, string Text);

/// <summary>A resposta por modelo de mensagem aprovado: <c>Parameters</c> vai por chave de parâmetro (<c>body.1</c>, <c>body.nome</c>, <c>header.1</c>, <c>button.0</c>), como as opções de resposta as listam.</summary>
public sealed record ReplyWithTemplateRequest(Guid RequestId, Guid TemplateId, Dictionary<string, string>? Parameters);

public sealed record CloseItemRequest(Guid RequestId, string? ReasonCode);

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

/// <summary>O cliente em foco que o atendente vincula ao contato do item (ADR 0041). O identificador é opaco, do sistema externo; só vale se a sessão embutida o autorizou.</summary>
public sealed record CustomerLinkRequest(string? CustomerRef);

/// <summary>
/// O consentimento que o atendente com <c>ConsentManage</c> registra para o telefone do contato do item (ADR 0041, E2.4). Finalidade e situação são NOMES (<c>Utility</c>/<c>Marketing</c>, <c>Granted</c>/<c>Revoked</c>);
/// a data da coleta é texto ISO 8601 com fuso. O telefone não vem aqui: é o do contato do item.
/// </summary>
public sealed record RecordConsentRequest(string? Purpose, string? Status, string? Evidence, string? CollectedAt);

/// <summary>A decisão da pessoa autorizada sobre uma possível saída: <c>Revoked</c> ou <c>Kept</c>.</summary>
public sealed record ResolveOptOutRequest(string? Resolution);
