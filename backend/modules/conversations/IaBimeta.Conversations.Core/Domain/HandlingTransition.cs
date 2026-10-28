using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>Registro de uma transição aplicada: é o que torna <c>TransitionHandlingMode</c> idempotente por <c>TransitionId</c>.</summary>
public sealed class HandlingTransition : ITenantOwned
{
    private HandlingTransition()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid TransitionId { get; private set; }

    public long StateVersionAfter { get; private set; }

    public DateTimeOffset AppliedAt { get; private set; }

    public static HandlingTransition Create(TenantId tenantId, Guid sessionId, Guid transitionId, long stateVersionAfter, DateTimeOffset now) =>
        new() { TenantId = tenantId, SessionId = sessionId, TransitionId = transitionId, StateVersionAfter = stateVersionAfter, AppliedAt = now };
}
