using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Registro de uma operação de atendimento já tratada: é o que torna <c>TransitionHandlingMode</c> (e <c>TransferSession</c>)
/// idempotente por <c>TransitionId</c> (ou <c>TransferId</c>). Também guarda a operação que foi avaliada e NÃO mudou nada (a sessão já
/// estava no estado pedido): uma repetição tardia dela devolve "já tratada", em vez de reavaliá-la contra um estado que mudou depois.
/// </summary>
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

    /// <summary>
    /// A sequência da última mensagem da sessão NO INSTANTE em que a operação MUDOU o estado (a fronteira estável do que veio antes e
    /// depois da mudança). É <b>nula</b> quando a operação foi avaliada e não mudou nada (a sessão já estava no estado pedido por outra
    /// via) e nos registros anteriores a este campo: a mudança que fez o estado ser o que é aconteceu em outro instante, desconhecido
    /// aqui, e usar a sequência do momento da avaliação seria declarar uma fronteira que não existe.
    /// </summary>
    public long? MessageSeqAt { get; private set; }

    /// <summary>
    /// O período de atendimento humano (<see cref="ServiceSession.HandlingPeriod"/>) em que a sessão ficou DEPOIS da mudança feita por esta
    /// operação: a identidade do período que ela abriu (ou manteve). Nulo, como a fronteira, para a operação sem efeito e para os registros
    /// anteriores a este campo (a identidade é desconhecida, e quem a usa a trata assim).
    /// </summary>
    public long? HandlingPeriodAfter { get; private set; }

    public DateTimeOffset AppliedAt { get; private set; }

    /// <param name="messageSeqAt">A sequência da última mensagem no instante da mudança; <c>null</c> para a operação sem efeito.</param>
    /// <param name="handlingPeriodAfter">O período de atendimento humano depois da mudança; <c>null</c> para a operação sem efeito.</param>
    public static HandlingTransition Create(
        TenantId tenantId, Guid sessionId, Guid transitionId, long stateVersionAfter, long? messageSeqAt, long? handlingPeriodAfter, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            SessionId = sessionId,
            TransitionId = transitionId,
            StateVersionAfter = stateVersionAfter,
            MessageSeqAt = messageSeqAt,
            HandlingPeriodAfter = handlingPeriodAfter,
            AppliedAt = now,
        };
}
