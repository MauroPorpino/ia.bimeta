using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

/// <summary>A operação sobre um item que o identificador do pedido (<c>requestId</c>) representa.</summary>
public enum OperationKind
{
    /// <summary>A conversa entra na fila (pedido de atendimento humano).</summary>
    Enter = 1,

    /// <summary>O atendente assume uma conversa por conta própria (por ela ou como "a próxima" da fila).</summary>
    Claim = 2,

    /// <summary>Alguém passa a conversa a um atendente da fila (o supervisor, ou o responsável, a outro).</summary>
    Assign = 3,

    Release = 4,

    TransferQueue = 5,

    ReturnToAi = 6,

    SetPriority = 7,
}

public enum OperationState
{
    /// <summary>A intenção está gravada e o Conversations ainda não confirmou. Repetir o pedido (o mesmo autor) a conclui.</summary>
    Pending = 1,

    Applied = 2,

    /// <summary>O pedido foi atendido sem mudar nada (a conversa já estava como pedido). Também deixa recibo.</summary>
    NoChange = 3,

    /// <summary>O destino foi recusado de forma definitiva e a operação foi desfeita. Repetir devolve a mesma recusa.</summary>
    Cancelled = 4,

    SessionEnded = 5,
}

/// <summary>
/// O recibo de UMA operação, chave de idempotência do pedido (<c>(tenant_id, operation_id)</c>): liga o identificador ao tenant, à
/// operação, ao item (e à fila e ao atendente de destino, quando há) e ao AUTOR. É gravado na MESMA transação em que a intenção entra
/// no item, então um identificador nunca vale para dois itens (nem em paralelo), e a resposta dada ao autor fica guardada
/// (<see cref="Snapshot"/>): repetir o pedido, mesmo depois de a operação ter mudado o que o autor pode ver, devolve essa resposta, e
/// só ao autor original. Quem reusa o identificador para outra coisa recebe conflito, nunca o resultado alheio. Operação que não
/// mudou nada também deixa recibo: um pedido repetido ou atrasado não refaz o que já foi respondido (nem desfaz o que veio depois).
/// </summary>
public sealed class QueueOperation : ITenantOwned
{
    private QueueOperation()
    {
        TenantId = null!;
    }

    /// <summary>O identificador do pedido (a chave de idempotência).</summary>
    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid ItemId { get; private set; }

    public Guid SessionId { get; private set; }

    public OperationKind Kind { get; private set; }

    /// <summary>Quem pediu, ou <c>null</c> se foi um componente de sistema. Só o autor repete o pedido.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>A fila a que o pedido se prende: a do item no momento do pedido, ou a de destino da transferência.</summary>
    public Guid QueueId { get; private set; }

    /// <summary>O atendente de destino (atribuição e transferência), quando há.</summary>
    public Guid? TargetUserId { get; private set; }

    public OperationState State { get; private set; }

    /// <summary>O código da regra que recusou o destino (<c>ContactCenterRule</c>), só no estado cancelado.</summary>
    public int? FailureRule { get; private set; }

    /// <summary>A resposta dada ao autor (a visão do item, em JSON), quando a operação terminou sem cancelar.</summary>
    public string? Snapshot { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsPending => State == OperationState.Pending;

    /// <summary>O recibo de uma operação que acabou de ter a intenção gravada.</summary>
    public static QueueOperation Begin(
        TenantId tenantId, Guid operationId, OperationKind kind, Guid itemId, Guid sessionId, Guid? actorUserId, Guid queueId, Guid? targetUserId, DateTimeOffset now) =>
        Create(tenantId, operationId, kind, itemId, sessionId, actorUserId, queueId, targetUserId, OperationState.Pending, null, now);

    /// <summary>O recibo de uma operação que já nasce concluída (nada a pedir ao Conversations): sem mudança, ou só no próprio item.</summary>
    public static QueueOperation Immediate(
        TenantId tenantId, Guid operationId, OperationKind kind, Guid itemId, Guid sessionId, Guid? actorUserId, Guid queueId, Guid? targetUserId,
        OperationState state, string snapshot, DateTimeOffset now)
    {
        if (state is not (OperationState.Applied or OperationState.NoChange) || string.IsNullOrEmpty(snapshot))
        {
            throw new ContactCenterValidationException("Uma operação imediata termina aplicada ou sem mudança, com a resposta guardada.");
        }

        return Create(tenantId, operationId, kind, itemId, sessionId, actorUserId, queueId, targetUserId, state, snapshot, now);
    }

    private static QueueOperation Create(
        TenantId tenantId, Guid operationId, OperationKind kind, Guid itemId, Guid sessionId, Guid? actorUserId, Guid queueId, Guid? targetUserId,
        OperationState state, string? snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (operationId == Guid.Empty || itemId == Guid.Empty || sessionId == Guid.Empty || queueId == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new ContactCenterValidationException("A operação precisa de identificador, item, sessão, fila e tipo válidos.");
        }

        return new QueueOperation
        {
            Id = operationId,
            TenantId = tenantId,
            ItemId = itemId,
            SessionId = sessionId,
            Kind = kind,
            ActorUserId = actorUserId,
            QueueId = queueId,
            TargetUserId = targetUserId,
            State = state,
            Snapshot = snapshot,
            CreatedAt = now,
            CompletedAt = state == OperationState.Pending ? null : now,
        };
    }

    /// <summary>
    /// O pedido atual (tipo, autor e o que ele diz do que alcança) é o MESMO que gerou este recibo? O tipo e o autor sempre precisam
    /// bater; o item, a fila e a sessão só são comparados quando o pedido os conhece (cada rota fala do que sabe: a conversa na fila
    /// pelo item, a próxima da fila pela fila, o pedido de atendimento pela sessão).
    /// </summary>
    public bool Matches(OperationKind kind, Guid? actorUserId, Guid? itemId = null, Guid? queueId = null, Guid? sessionId = null) =>
        Kind == kind
        && ActorUserId == actorUserId
        && (itemId is null || ItemId == itemId)
        && (queueId is null || QueueId == queueId)
        && (sessionId is null || SessionId == sessionId);

    /// <summary>O atendente de destino do pedido (ou a ausência dele) é o que o recibo guarda?</summary>
    public bool HasTarget(Guid? targetUserId) => TargetUserId == targetUserId;

    /// <summary>A operação terminou (aplicada, sem mudança ou com a sessão encerrada), com a resposta dada ao autor.</summary>
    public void Complete(OperationState state, string snapshot, DateTimeOffset now)
    {
        if (!IsPending)
        {
            throw new QueueItemStateException("A operação já terminou.");
        }

        if (state is not (OperationState.Applied or OperationState.NoChange or OperationState.SessionEnded) || string.IsNullOrEmpty(snapshot))
        {
            throw new ContactCenterValidationException("A operação termina aplicada, sem mudança ou com a sessão encerrada, com a resposta guardada.");
        }

        State = state;
        Snapshot = snapshot;
        CompletedAt = now;
    }

    /// <summary>O destino foi recusado de forma definitiva: a operação é desfeita e repetir o pedido devolve a mesma recusa.</summary>
    public void Cancel(int failureRule, DateTimeOffset now)
    {
        if (!IsPending)
        {
            throw new QueueItemStateException("A operação já terminou.");
        }

        State = OperationState.Cancelled;
        FailureRule = failureRule;
        CompletedAt = now;
    }
}
