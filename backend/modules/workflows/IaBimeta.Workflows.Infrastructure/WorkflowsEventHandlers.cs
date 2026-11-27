using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Workflows.Infrastructure;

/// <summary>
/// Os consumidores do Workflows para os eventos de estado do Conversations (ADR 0038). No molde do Agents (ADR 0017 G.1), cada handler SÓ grava no banco do módulo, na mesma transação do inbox: decide o dono da
/// sessão (uma leitura da sessão, sem chamada de modelo, de sistema externo nem de canal), acorda a instância ou pede o cancelamento. Quem RODA o fluxo é o executor do Worker. A entrega é pelo menos uma
/// vez; o inbox (por <c>EventId</c>) impede o efeito duplicado.
/// </summary>
internal static class FlowEventHandling
{
    public const string Consumer = "workflows.sessions";

    public static async Task<HandlerResult> RunAsync(InboxGate<WorkflowsDbContext> inbox, EventEnvelope envelope, Guid sessionId, Func<CancellationToken, Task> apply, CancellationToken cancellationToken, ITenantOperationGate? gate = null)
    {
        if (sessionId == Guid.Empty)
        {
            return HandlerResult.Dead("invalid-session");
        }

        // Empresa suspensa (Etapa 02): a mensagem do cliente continua preservada no Conversations, mas nenhum fluxo é escolhido nem acordado. O evento é consumido: não há reprocessamento depois da reativação.
        // Entrega atrasada (P1-03): o estímulo ANTERIOR à última reativação nunca acorda nem cria nada depois dela, mesmo que o relay o entregue tarde.
        if (gate is not null)
        {
            var estado = await gate.GetAsync(envelope.TenantId, cancellationToken);
            if (!estado.IsOperational || (estado.OperationalSince is { } reativadaEm && envelope.OccurredAt < reativadaEm))
            {
                return HandlerResult.Success;
            }
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
        catch (WorkflowsConflictException)
        {
            // A instância foi alterada ao mesmo tempo (o executor a roda): a próxima entrega relê e decide de novo.
            return HandlerResult.Retry(reason: nameof(WorkflowsConflictException));
        }
        catch (DbUpdateConcurrencyException)
        {
            return HandlerResult.Retry(reason: nameof(DbUpdateConcurrencyException));
        }
    }
}

/// <summary>Mensagem do cliente registrada: a primeira da sessão pode escolher um fluxo (o roteador decide); numa sessão que é de um fluxo, acorda a instância.</summary>
internal sealed class InboundForFlowHandler : EventHandlerBase<InboundMessageRegisteredV1>
{
    private readonly InboxGate<WorkflowsDbContext> _inbox;
    private readonly IAutomationRouter _router;
    private readonly IFlowRuntimeStore _store;
    private readonly TimeProvider _clock;
    private readonly ITenantOperationGate? _gate;

    public InboundForFlowHandler(InboxGate<WorkflowsDbContext> inbox, IAutomationRouter router, IFlowRuntimeStore store, TimeProvider clock, ITenantOperationGate? gate = null)
    {
        _gate = gate;
        _inbox = inbox;
        _router = router;
        _store = store;
        _clock = clock;
    }

    public override string EventType => ConversationEventTypes.InboundMessageRegistered;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, InboundMessageRegisteredV1 payload, CancellationToken cancellationToken) =>
        FlowEventHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            async token =>
            {
                // ADR 0041 (E2.5): o comando de saída confirmado não escolhe fluxo nem acorda instância (a mensagem fica registrada e visível).
                if (payload.IsOptOutCommand)
                {
                    return;
                }

                if (await _router.ResolveOnInboundAsync(payload.SessionId, payload.InboundSeq, token, envelope.OccurredAt) != AutomationOwner.Flow)
                {
                    return;
                }

                var instancia = await _store.FindActiveBySessionAsync(payload.SessionId, token);
                if (instancia is not null)
                {
                    instancia.Wake(_clock.GetUtcNow());
                    await _store.SaveAsync(token);
                }
            },
            cancellationToken,
            _gate);
}

/// <summary>O conteúdo de uma mídia ficou pronto: a instância é reavaliada já (a mensagem pendente deixa de ser só "tipo de mídia").</summary>
internal sealed class ContentReadyForFlowHandler : EventHandlerBase<MessageContentReadyV1>
{
    private readonly InboxGate<WorkflowsDbContext> _inbox;
    private readonly IFlowRuntimeStore _store;
    private readonly TimeProvider _clock;
    private readonly ITenantOperationGate? _gate;

    public ContentReadyForFlowHandler(InboxGate<WorkflowsDbContext> inbox, IFlowRuntimeStore store, TimeProvider clock, ITenantOperationGate? gate = null)
    {
        _gate = gate;
        _inbox = inbox;
        _store = store;
        _clock = clock;
    }

    public override string EventType => ConversationEventTypes.MessageContentReady;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, MessageContentReadyV1 payload, CancellationToken cancellationToken) =>
        FlowEventHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            async token =>
            {
                var instancia = await _store.FindActiveBySessionAsync(payload.SessionId, token);
                if (instancia is not null)
                {
                    instancia.Wake(_clock.GetUtcNow());
                    await _store.SaveAsync(token);
                }
            },
            cancellationToken,
            _gate);
}

/// <summary>
/// O modo de atendimento mudou. Para humano (ou qualquer modo que não seja a IA autônoma) o fluxo é CANCELADO com segurança: nenhuma mensagem de fluxo sai depois de o atendente assumir (as cercas do
/// Conversations também recusariam). De volta à IA, a sessão já é do agente.
/// </summary>
internal sealed class ModeChangedForFlowHandler : EventHandlerBase<HandlingModeChangedV1>
{
    private readonly InboxGate<WorkflowsDbContext> _inbox;
    private readonly IFlowRuntimeStore _store;
    private readonly TimeProvider _clock;

    public ModeChangedForFlowHandler(InboxGate<WorkflowsDbContext> inbox, IFlowRuntimeStore store, TimeProvider clock)
    {
        _inbox = inbox;
        _store = store;
        _clock = clock;
    }

    public override string EventType => ConversationEventTypes.HandlingModeChanged;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, HandlingModeChangedV1 payload, CancellationToken cancellationToken) =>
        FlowEventHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            async token =>
            {
                if (payload.To == HandlingMode.AiAutonomous)
                {
                    return;
                }

                var instancia = await _store.FindActiveBySessionAsync(payload.SessionId, token);
                if (instancia is not null)
                {
                    instancia.RequestCancel("human-takeover", _clock.GetUtcNow());
                    await _store.SaveAsync(token);
                }
            },
            cancellationToken);
}

/// <summary>A sessão foi encerrada: a instância é cancelada.</summary>
internal sealed class SessionClosedForFlowHandler : EventHandlerBase<SessionClosedV1>
{
    private readonly InboxGate<WorkflowsDbContext> _inbox;
    private readonly IFlowRuntimeStore _store;
    private readonly TimeProvider _clock;

    public SessionClosedForFlowHandler(InboxGate<WorkflowsDbContext> inbox, IFlowRuntimeStore store, TimeProvider clock)
    {
        _inbox = inbox;
        _store = store;
        _clock = clock;
    }

    public override string EventType => ConversationEventTypes.SessionClosed;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, SessionClosedV1 payload, CancellationToken cancellationToken) =>
        FlowEventHandling.RunAsync(
            _inbox,
            envelope,
            payload.SessionId,
            async token =>
            {
                var instancia = await _store.FindActiveBySessionAsync(payload.SessionId, token);
                if (instancia is not null)
                {
                    instancia.RequestCancel("session-closed", _clock.GetUtcNow());
                    await _store.SaveAsync(token);
                }
            },
            cancellationToken);
}
