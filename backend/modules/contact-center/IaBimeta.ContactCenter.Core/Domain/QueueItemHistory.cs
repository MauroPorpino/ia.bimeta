using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

public enum HistoryKind
{
    /// <summary>A conversa entrou na fila.</summary>
    Entered = 1,

    /// <summary>Um atendente assumiu a conversa que esperava na fila.</summary>
    Assigned = 2,

    /// <summary>A conversa passou de um atendente para outro.</summary>
    Reassigned = 3,

    /// <summary>O atendente devolveu a conversa à fila.</summary>
    Released = 4,

    /// <summary>A conversa foi para outra fila.</summary>
    TransferredQueue = 5,

    /// <summary>O atendimento voltou para a IA.</summary>
    ReturnedToAi = 6,

    /// <summary>A sessão terminou e o item foi encerrado.</summary>
    SessionClosed = 7,

    /// <summary>Um supervisor mudou a prioridade do atendimento.</summary>
    PriorityChanged = 8,
}

/// <summary>
/// Um fato do histórico de atribuições e transferências de um item: quem fez, de onde para onde, por quê e quando. O histórico é
/// só de acréscimo (o banco recusa alterar ou apagar). A chave de idempotência é <c>(tenant_id, operation_id)</c>: cada
/// operação deixa no máximo um registro, mesmo que duas tentativas de concluí-la corram juntas.
/// </summary>
public sealed class QueueItemHistory : ITenantOwned
{
    private QueueItemHistory()
    {
        TenantId = null!;
        Reason = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid OperationId { get; private set; }

    public HistoryKind Kind { get; private set; }

    public Guid? FromQueueId { get; private set; }

    public Guid ToQueueId { get; private set; }

    public Guid? FromUserId { get; private set; }

    public Guid? ToUserId { get; private set; }

    /// <summary>O usuário que pediu a operação, ou <c>null</c> se foi um componente de sistema.</summary>
    public Guid? ActorUserId { get; private set; }

    public string Reason { get; private set; }

    /// <summary>A <c>StateVersion</c> da sessão depois da operação, quando o Conversations a devolveu.</summary>
    public long? StateVersionAfter { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>A prioridade antes e depois, só no registro de mudança de prioridade.</summary>
    public int? FromPriority { get; private set; }

    public int? ToPriority { get; private set; }

    public static QueueItemHistory Create(
        TenantId tenantId, Guid itemId, Guid sessionId, Guid operationId, HistoryKind kind, Guid? fromQueueId, Guid toQueueId,
        Guid? fromUserId, Guid? toUserId, Guid? actorUserId, string reason, long? stateVersionAfter, DateTimeOffset now,
        int? fromPriority = null, int? toPriority = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        return new QueueItemHistory
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ItemId = itemId,
            SessionId = sessionId,
            OperationId = operationId,
            Kind = kind,
            FromQueueId = fromQueueId,
            ToQueueId = toQueueId,
            FromUserId = fromUserId,
            ToUserId = toUserId,
            ActorUserId = actorUserId,
            Reason = reason,
            StateVersionAfter = stateVersionAfter,
            OccurredAt = now,
            FromPriority = fromPriority,
            ToPriority = toPriority,
        };
    }
}
