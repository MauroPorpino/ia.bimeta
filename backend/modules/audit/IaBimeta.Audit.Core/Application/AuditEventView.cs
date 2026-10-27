using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Domain;

namespace IaBimeta.Audit.Application;

/// <summary>Visão de um evento de auditoria entregue à camada de apresentação (nunca a entidade).</summary>
public sealed record AuditEventView(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorOrigin,
    string Operation,
    string ResourceType,
    string? ResourceId,
    AuditOutcome Outcome)
{
    internal static AuditEventView From(AuditEvent evento) =>
        new(evento.Id, evento.OccurredAt, evento.ActorUserId, evento.ActorOrigin, evento.Operation, evento.ResourceType, evento.ResourceId, evento.Outcome);
}
