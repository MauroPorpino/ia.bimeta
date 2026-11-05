using IaBimeta.Agents.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>
/// Os consumidores do Agents para os eventos de estado do Conversations (CONVERSATION-LIFECYCLE.md §13). Cada handler SÓ grava o <c>AiTurn</c> no banco do módulo, na mesma transação do inbox: nenhum
/// chama o modelo, o Conversations nem o ContactCenter (o relay entrega em lote, e uma chamada lenta aqui seguraria a entrega de todos os tenants, ADR 0017 G.1). A entrega é pelo menos uma vez, então
/// o inbox (por <c>EventId</c>) impede o efeito duplicado. Conflito com o executor (versão do turno, turno aberto da sessão) volta como nova tentativa; o tenant vem do envelope, pelo contexto do host.
/// O Agents NÃO consome <c>OutboundMessageFailedV1</c> nem os eventos de entrega: falha de envio não dispara IA (ARQ-24).
/// </summary>
internal static class TurnHandling
{
    /// <summary>O nome estável deste consumidor no inbox do módulo.</summary>
    public const string Consumer = "agents.turn-scheduling";

    public static async Task<HandlerResult> RunAsync(
        InboxGate<AgentsDbContext> inbox, EventEnvelope envelope, Guid sessionId, Func<CancellationToken, Task> apply, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return HandlerResult.Dead("invalid-session");
        }

        try
        {
            return await inbox.RunOnceAsync(
                envelope.TenantId,
                Consumer,
                envelope.EventId,
                async token =>
                {
                    await apply(token);
                    return HandlerResult.Success;
                },
                cancellationToken);
        }
        catch (AiTurnConflictException)
        {
            // O executor (ou outro handler) alterou o turno ao mesmo tempo: a próxima entrega relê e decide de novo.
            return HandlerResult.Retry(reason: nameof(AiTurnConflictException));
        }
        catch (DbUpdateConcurrencyException)
        {
            return HandlerResult.Retry(reason: nameof(DbUpdateConcurrencyException));
        }
    }
}

/// <summary>Mensagem do cliente registrada (<c>InboundMessageRegisteredV1</c>): T1, T2, T6 ou T12.</summary>
internal sealed class InboundMessageRegisteredHandler : EventHandlerBase<InboundMessageRegisteredV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public InboundMessageRegisteredHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.InboundMessageRegistered;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, InboundMessageRegisteredV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnInboundMessageAsync(payload.SessionId, envelope.OccurredAt, token), cancellationToken);
}

/// <summary>O conteúdo pendente de uma mensagem ficou pronto (<c>MessageContentReadyV1</c>): o turno que espera é reavaliado já.</summary>
internal sealed class MessageContentReadyHandler : EventHandlerBase<MessageContentReadyV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public MessageContentReadyHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.MessageContentReady;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, MessageContentReadyV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnContentChangedAsync(payload.SessionId, token), cancellationToken);
}

/// <summary>O conteúdo pendente de uma mensagem falhou (<c>MessageContentFailedV1</c>): o turno que espera é reavaliado já, e a política do tipo se aplica.</summary>
internal sealed class MessageContentFailedHandler : EventHandlerBase<MessageContentFailedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public MessageContentFailedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.MessageContentFailed;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, MessageContentFailedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnContentChangedAsync(payload.SessionId, token), cancellationToken);
}

/// <summary>
/// O modo de atendimento ou o responsável mudou (<c>HandlingModeChangedV1</c>). Para humano (ou qualquer modo que não seja a IA autônoma): T8, cancela o turno aberto, e nenhuma resposta sai depois de o
/// humano assumir. De volta à IA: CON-7, o turno dispara já.
/// </summary>
internal sealed class HandlingModeChangedHandler : EventHandlerBase<HandlingModeChangedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public HandlingModeChangedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.HandlingModeChanged;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, HandlingModeChangedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            token => payload.To == HandlingMode.AiAutonomous
                ? _scheduler.OnReturnedToAiAsync(payload.SessionId, token)
                : _scheduler.OnLeftAiControlAsync(payload.SessionId, "mode-changed", token),
            cancellationToken);
}

/// <summary>A sessão foi encerrada (<c>SessionClosedV1</c>): T8, o turno aberto é cancelado.</summary>
internal sealed class SessionClosedHandler : EventHandlerBase<SessionClosedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public SessionClosedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.SessionClosed;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, SessionClosedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnLeftAiControlAsync(payload.SessionId, "session-closed", token), cancellationToken);
}

/// <summary>A sessão foi encaminhada para outra unidade ou setor (<c>SessionTransferredV1</c>): o turno em andamento, montado para a unidade anterior, é trocado por um sucessor.</summary>
internal sealed class SessionTransferredHandler : EventHandlerBase<SessionTransferredV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public SessionTransferredHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.SessionTransferred;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, SessionTransferredV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnSessionStateChangedAsync(payload.SessionId, token), cancellationToken);
}

/// <summary>O idioma efetivo da conversa mudou (<c>ConversationLocaleChangedV1</c>): o turno em andamento, escrito no idioma anterior, é trocado por um sucessor.</summary>
internal sealed class ConversationLocaleChangedHandler : EventHandlerBase<ConversationLocaleChangedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public ConversationLocaleChangedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.ConversationLocaleChanged;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, ConversationLocaleChangedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnSessionStateChangedAsync(payload.SessionId, token), cancellationToken);
}
