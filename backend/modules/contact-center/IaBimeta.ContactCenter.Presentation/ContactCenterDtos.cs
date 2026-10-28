using IaBimeta.ContactCenter.Domain;

namespace IaBimeta.ContactCenter.Presentation;

/// <summary>Nenhum DTO aceita tenant: o tenant é sempre o do contexto confiável criado pelo host.</summary>
public sealed record CreateServiceQueueRequest(Guid OrganizationId, Guid? BusinessUnitId, Guid? DepartmentId, string Name);

public sealed record SetQueueMemberRequest(QueueMemberRole Role);
