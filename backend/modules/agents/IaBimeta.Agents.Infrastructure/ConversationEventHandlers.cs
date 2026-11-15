using IaBimeta.Agents.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
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
    /// <summary>
    /// A sessão é de um FLUXO do chatbot visual (ADR 0038)? Então o Agents não abre turno: Workflows e Agents nunca respondem à mesma sessão. Com o chatbot visual desligado (ou não composto) a resposta é sempre
    /// "não": o caminho é idêntico ao anterior. Numa mensagem do cliente (<paramref name="inboundSeq"/>) o roteador também DECIDE (só a primeira mensagem da sessão pode escolher um fluxo).
    /// </summary>
    public static async Task<bool> OwnedByFlowAsync(IAutomationRouter? router, Guid sessionId, long? inboundSeq, CancellationToken cancellationToken)
    {
        if (router is null)
        {
            return false;
        }

        var dono = inboundSeq is { } seq ? await router.ResolveOnInboundAsync(sessionId, seq, cancellationToken) : await router.GetOwnerAsync(sessionId, cancellationToken);
        return dono == AutomationOwner.Flow;
    }

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
    private readonly IAutomationRouter? _router;

    public InboundMessageRegisteredHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler, IAutomationRouter? router = null)
    {
        _inbox = inbox;
        _scheduler = scheduler;
        _router = router;
    }

    public override string EventType => ConversationEventTypes.InboundMessageRegistered;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, InboundMessageRegisteredV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            async token =>
            {
                // ADR 0038: a sessão que é de um FLUXO não ganha turno de IA (e a primeira mensagem de uma sessão pode escolher o fluxo).
                if (!await TurnHandling.OwnedByFlowAsync(_router, payload.SessionId, payload.InboundSeq, token))
                {
                    await _scheduler.OnInboundMessageAsync(payload.SessionId, envelope.OccurredAt, token);
                }
            },
            cancellationToken);
}

/// <summary>O conteúdo pendente de uma mensagem ficou pronto (<c>MessageContentReadyV1</c>): o turno que espera é reavaliado já.</summary>
internal sealed class MessageContentReadyHandler : EventHandlerBase<MessageContentReadyV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;
    private readonly IAutomationRouter? _router;

    public MessageContentReadyHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler, IAutomationRouter? router = null)
    {
        _inbox = inbox;
        _scheduler = scheduler;
        _router = router;
    }

    public override string EventType => ConversationEventTypes.MessageContentReady;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, MessageContentReadyV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, async token =>
            {
                if (!await TurnHandling.OwnedByFlowAsync(_router, payload.SessionId, null, token))
                {
                    await _scheduler.OnContentChangedAsync(payload.SessionId, token);
                }
            },
            cancellationToken);
}

/// <summary>O conteúdo pendente de uma mensagem falhou (<c>MessageContentFailedV1</c>): o turno que espera é reavaliado já, e a política do tipo se aplica.</summary>
internal sealed class MessageContentFailedHandler : EventHandlerBase<MessageContentFailedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;
    private readonly IAutomationRouter? _router;

    public MessageContentFailedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler, IAutomationRouter? router = null)
    {
        _inbox = inbox;
        _scheduler = scheduler;
        _router = router;
    }

    public override string EventType => ConversationEventTypes.MessageContentFailed;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, MessageContentFailedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, async token =>
            {
                if (!await TurnHandling.OwnedByFlowAsync(_router, payload.SessionId, null, token))
                {
                    await _scheduler.OnContentChangedAsync(payload.SessionId, token);
                }
            },
            cancellationToken);
}

/// <summary>
/// O modo de atendimento ou o responsável mudou (<c>HandlingModeChangedV1</c>). Para humano (ou qualquer modo que não seja a IA autônoma): T8, cancela o turno aberto, e nenhuma resposta sai depois de o
/// humano assumir. De volta à IA: CON-7, o turno dispara já.
/// </summary>
internal sealed class HandlingModeChangedHandler : EventHandlerBase<HandlingModeChangedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;
    private readonly IAutomationRouter? _router;

    public HandlingModeChangedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler, IAutomationRouter? router = null)
    {
        _inbox = inbox;
        _scheduler = scheduler;
        _router = router;
    }

    public override string EventType => ConversationEventTypes.HandlingModeChanged;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, HandlingModeChangedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            async token =>
            {
                if (payload.To != HandlingMode.AiAutonomous)
                {
                    await _scheduler.OnLeftAiControlAsync(payload.SessionId, "mode-changed", token);
                }
                else if (!await TurnHandling.OwnedByFlowAsync(_router, payload.SessionId, null, token))
                {
                    // ADR 0038: de volta à IA, a sessão que ainda é de um fluxo não ganha turno de IA.
                    await _scheduler.OnReturnedToAiAsync(payload.SessionId, token);
                }
            },
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
    private readonly IAutomationRouter? _router;

    public SessionTransferredHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler, IAutomationRouter? router = null)
    {
        _inbox = inbox;
        _scheduler = scheduler;
        _router = router;
    }

    public override string EventType => ConversationEventTypes.SessionTransferred;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, SessionTransferredV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, async token =>
            {
                if (!await TurnHandling.OwnedByFlowAsync(_router, payload.SessionId, null, token))
                {
                    await _scheduler.OnSessionStateChangedAsync(payload.SessionId, token);
                }
            },
            cancellationToken);
}

/// <summary>O idioma efetivo da conversa mudou (<c>ConversationLocaleChangedV1</c>): o turno em andamento, escrito no idioma anterior, é trocado por um sucessor.</summary>
internal sealed class ConversationLocaleChangedHandler : EventHandlerBase<ConversationLocaleChangedV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;
    private readonly IAutomationRouter? _router;

    public ConversationLocaleChangedHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler, IAutomationRouter? router = null)
    {
        _inbox = inbox;
        _scheduler = scheduler;
        _router = router;
    }

    public override string EventType => ConversationEventTypes.ConversationLocaleChanged;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, ConversationLocaleChangedV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, async token =>
            {
                if (!await TurnHandling.OwnedByFlowAsync(_router, payload.SessionId, null, token))
                {
                    await _scheduler.OnSessionStateChangedAsync(payload.SessionId, token);
                }
            },
            cancellationToken);
}

/// <summary>
/// O fluxo do chatbot visual terminou e a sessão voltou ao agente (<c>FlowReleasedToAgentV1</c>, ADR 0038): a entrada ainda não respondida é reavaliada já, como no retorno à IA (CON-7). Se o atendente já
/// assumiu ou não há entrada pendente, não cria turno.
/// </summary>
internal sealed class FlowReleasedToAgentHandler : EventHandlerBase<FlowReleasedToAgentV1>
{
    private readonly InboxGate<AgentsDbContext> _inbox;
    private readonly TurnScheduler _scheduler;

    public FlowReleasedToAgentHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler)
    {
        _inbox = inbox;
        _scheduler = scheduler;
    }

    public override string EventType => ConversationEventTypes.FlowReleasedToAgent;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, FlowReleasedToAgentV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(_inbox, envelope, payload.SessionId, token => _scheduler.OnReturnedToAiAsync(payload.SessionId, token), cancellationToken);
}

/// <summary>Retoma a sessão mesmo quando o evento de entrada já foi consumido enquanto o fluxo era o dono.</summary>
internal sealed class FlowDrainedToAgentHandler(InboxGate<AgentsDbContext> inbox, TurnScheduler scheduler) : EventHandlerBase<FlowDrainedToAgentV1>
{
    public override string EventType => FlowDrainedToAgentV1.EventType;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, FlowDrainedToAgentV1 payload, CancellationToken cancellationToken) =>
        TurnHandling.RunAsync(inbox, envelope, payload.SessionId, token => scheduler.OnReturnedToAiAsync(payload.SessionId, token), cancellationToken);
}
