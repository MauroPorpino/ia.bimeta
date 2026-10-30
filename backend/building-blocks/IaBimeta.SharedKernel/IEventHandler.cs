namespace IaBimeta.SharedKernel;

public enum HandlerOutcome
{
    /// <summary>O evento foi tratado (ou já tinha sido: o handler é idempotente por <c>EventId</c>).</summary>
    Success = 1,

    /// <summary>Falha transitória (rede, banco, serviço fora do ar): tenta de novo depois, com espera crescente.</summary>
    Retry = 2,

    /// <summary>Falha permanente (validação, política): o evento vai para os mortos com o motivo, e a fila segue.</summary>
    Dead = 3,
}

/// <summary>
/// O resultado de um handler (BOUNDARIES.md §12.9): <c>Success</c>, <c>Retry(após)</c> ou <c>Dead(motivo)</c>. O motivo é um
/// código curto, sem conteúdo de mensagem nem dado pessoal: vai para logs e para a visão administrativa dos mortos.
/// </summary>
public readonly record struct HandlerResult(HandlerOutcome Outcome, TimeSpan? RetryAfter = null, string? Reason = null)
{
    public static HandlerResult Success { get; } = new(HandlerOutcome.Success);

    /// <param name="after">A espera mínima antes da próxima tentativa; <c>null</c> deixa a política de espera do relay decidir.</param>
    public static HandlerResult Retry(TimeSpan? after = null, string? reason = null) => new(HandlerOutcome.Retry, after, reason);

    public static HandlerResult Dead(string reason) => new(HandlerOutcome.Dead, null, reason);
}

/// <summary>
/// Quem consome um tipo de evento durável. A garantia é <b>pelo menos uma vez</b>: o mesmo evento pode chegar de novo, e o handler
/// é idempotente por <c>EventId</c> (cada módulo guarda o próprio inbox). A ordem é por <c>OrderingKey</c>. O handler roda em um
/// escopo do host com o <see cref="RequestContext"/> do tenant do evento, criado pelo host a partir do envelope confiável, nunca
/// de dado do payload. O módulo dono do handler registra-o; o Messaging só o encontra pelo tipo e pela versão.
/// </summary>
public interface IEventHandler
{
    /// <summary>O nome estável do evento consumido, sem a versão (por exemplo, <c>conversations.handling-mode-changed</c>).</summary>
    string EventType { get; }

    /// <summary>A versão do contrato do payload que o handler entende.</summary>
    int EventVersion { get; }

    /// <summary>O tipo do contrato do payload (por exemplo, <c>HandlingModeChangedV1</c>): o Messaging o reconstrói do que o outbox guardou.</summary>
    Type PayloadType { get; }

    /// <summary>
    /// Trata o evento. Exceção não tratada conta como falha transitória (e vai para os mortos depois do máximo de tentativas): o
    /// handler deve devolver <see cref="HandlerResult.Dead"/> para o que é permanente.
    /// </summary>
    Task<HandlerResult> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Atalho para o handler de um contrato: o payload já chega no tipo certo.</summary>
public abstract class EventHandlerBase<TPayload> : IEventHandler
    where TPayload : class
{
    public abstract string EventType { get; }

    public abstract int EventVersion { get; }

    public Type PayloadType => typeof(TPayload);

    public Task<HandlerResult> HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Payload is TPayload payload
            ? HandleAsync(envelope, payload, cancellationToken)
            : Task.FromResult(HandlerResult.Dead("payload-type-mismatch"));
    }

    protected abstract Task<HandlerResult> HandleAsync(EventEnvelope envelope, TPayload payload, CancellationToken cancellationToken);
}
