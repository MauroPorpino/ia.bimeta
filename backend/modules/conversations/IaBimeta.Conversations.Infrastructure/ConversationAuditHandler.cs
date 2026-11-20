using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Conversations.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// Entrega à trilha de auditoria os pedidos que o Conversations guardou no outbox na MESMA transação do efeito (ADR 0041, seção 8). Grava com o autor, a origem e o instante ORIGINAIS e com o identificador do evento
/// como chave de idempotência (a entrega é pelo menos uma vez, e a trilha grava UM evento por tenant e chave). O tenant é o do contexto que o relay abre para o tenant do envelope.
/// <para>
/// A auditoria NUNCA é descartada: se a trilha não aceita agora, o evento é ADIADO (<c>Defer</c>, que não gasta tentativa do relay) e volta depois, para sempre, até a trilha aceitar (o mesmo critério do outbox de
/// auditoria do Identity). Uma entrada inválida fica visível na própria linha do outbox (<c>last_error</c> com o tipo do erro), nunca em silêncio.
/// </para>
/// </summary>
internal sealed class ConversationAuditHandler : EventHandlerBase<ConversationAuditRequestedV1>
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly IAuditRecoveryWriter _audit;

    public ConversationAuditHandler(IAuditRecoveryWriter audit)
    {
        _audit = audit;
    }

    public override string EventType => ConversationAuditEvents.AuditRequested;

    public override int EventVersion => 1;

    protected override async Task<HandlerResult> HandleAsync(EventEnvelope envelope, ConversationAuditRequestedV1 payload, CancellationToken cancellationToken)
    {
        try
        {
            await _audit.WriteRecoveredAsync(
                new AuditEntry(payload.Operation, payload.ResourceType, payload.ResourceId, payload.Outcome, envelope.EventId),
                new AuditProvenance(payload.ActorUserId, payload.ActorOrigin, payload.OccurredAt),
                cancellationToken);
            return HandlerResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Só o TIPO do erro (nunca a mensagem, que pode carregar dado): a próxima tentativa é depois do prazo e não gasta tentativa do relay.
            return HandlerResult.Defer(RetryAfter, ex.GetType().Name);
        }
    }
}
