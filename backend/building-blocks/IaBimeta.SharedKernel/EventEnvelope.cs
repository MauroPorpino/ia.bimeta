namespace IaBimeta.SharedKernel;

/// <summary>
/// Envelope dos eventos entre módulos (BOUNDARIES.md §4 COM-3 e §12.9): o que os módulos conhecem do transporte. O
/// <see cref="Payload"/> é o contrato do evento, que pertence ao módulo emissor (COM-4). A entrega é pelo menos uma vez, e
/// o consumidor é idempotente por <see cref="EventId"/>.
/// </summary>
/// <param name="Type">Nome estável do evento, sem a versão (por exemplo, <c>conversations.inbound-message-registered</c>).</param>
/// <param name="Version">Versão do contrato do payload.</param>
/// <param name="OrderingKey">Chave de ordem: eventos com a mesma chave são entregues em ordem (por exemplo, a sessão).</param>
public sealed record EventEnvelope(
    Guid EventId,
    string Type,
    int Version,
    TenantId TenantId,
    Guid CorrelationId,
    Guid? CausationId,
    string OrderingKey,
    DateTimeOffset OccurredAt,
    object Payload);
