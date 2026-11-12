using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

public enum SimInputKind
{
    /// <summary>O cliente escreve um texto.</summary>
    Say = 1,

    /// <summary>O cliente toca numa opção (id da escolha).</summary>
    Choose = 2,

    /// <summary>O cliente envia uma mídia.</summary>
    Media = 3,

    /// <summary>O tempo passa (relógio virtual, em segundos): dispara o prazo da pergunta.</summary>
    Wait = 4,
}

public sealed record SimInput(SimInputKind Kind, string? Text = null, string? ChoiceId = null, int Seconds = 0);

/// <summary>A resposta simulada de um nó assíncrono (por id do nó). Sem stub: conhecimento "não encontrado" e integração "falha".</summary>
public sealed record NodeStub(bool? Found = null, string? Answer = null, bool? Success = null, IReadOnlyDictionary<string, string>? Fields = null, string? ErrorCode = null);

public sealed record SimulationScenario(IReadOnlyList<SimInput> Inputs, IReadOnlyDictionary<string, NodeStub>? Stubs = null, DateTimeOffset? StartAt = null);

public sealed record SimEvent(string Kind, string? NodeId, string? Text = null, IReadOnlyList<FlowChoice>? Choices = null, IReadOnlyDictionary<string, string>? Data = null);

/// <param name="Outcome"><c>completed</c>, <c>handed_off</c>, <c>delegated</c>, <c>failed</c>, <c>waiting_for_input</c> (os insumos acabaram) ou <c>event-limit</c>.</param>
public sealed record SimulationResult(IReadOnlyList<SimEvent> Events, FlowState FinalState, string Outcome, IReadOnlyList<string> Visited);

/// <summary>
/// O simulador (ADR 0037 §9): roda o MESMO interpretador com um executor simulado. Não recebe nenhuma porta (IA, conhecimento, operação, canal, relógio): é impossível, por construção,
/// que ele faça chamada real. O que seria uma chamada vira um evento <c>simulated-*</c> com a resposta do stub do cenário.
/// </summary>
public sealed class FlowSimulator
{
    private static readonly DateTimeOffset DefaultStart = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly FlowInterpreter _interpreter;
    private readonly int _maxEvents;

    public FlowSimulator(FlowLimits? limits = null, int maxEvents = 500)
    {
        _interpreter = new FlowInterpreter(limits);
        _maxEvents = maxEvents;
    }

    public SimulationResult Run(FlowGraph graph, SimulationScenario scenario)
    {
        var now = scenario.StartAt ?? DefaultStart;
        var inputs = new Queue<SimInput>(scenario.Inputs);
        var stubs = scenario.Stubs ?? new Dictionary<string, NodeStub>();
        var events = new List<SimEvent>();
        var visited = new List<string>();
        var lastEffects = new List<FlowEffect>();

        InboundInput? first = null;
        if (inputs.Count > 0 && inputs.Peek().Kind != SimInputKind.Wait)
        {
            first = ToInbound(inputs.Dequeue());
            events.Add(new SimEvent("customer", null, first.Text ?? first.ChoiceId ?? first.MediaKind));
        }

        var state = Apply(_interpreter.Start(graph, now, first), events, visited, lastEffects);
        var outcome = string.Empty;
        while (outcome.Length == 0)
        {
            if (events.Count >= _maxEvents)
            {
                outcome = "event-limit";
                break;
            }

            switch (state.Status)
            {
                case FlowStatus.AwaitingEffect:
                    state = Apply(_interpreter.Advance(graph, state, Resolve(state, lastEffects, stubs), now), events, visited, lastEffects);
                    break;

                case FlowStatus.WaitingInput:
                    if (inputs.Count == 0)
                    {
                        outcome = "waiting_for_input";
                        break;
                    }

                    var next = inputs.Dequeue();
                    if (next.Kind == SimInputKind.Wait)
                    {
                        now = now.AddSeconds(Math.Max(0, next.Seconds));
                        events.Add(new SimEvent("time-passed", state.NodeId, Data: new Dictionary<string, string> { ["seconds"] = next.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                        state = Apply(_interpreter.Advance(graph, state, new TimeoutInput(), now), events, visited, lastEffects);
                    }
                    else
                    {
                        var inbound = ToInbound(next);
                        events.Add(new SimEvent("customer", state.NodeId, inbound.Text ?? inbound.ChoiceId ?? inbound.MediaKind));
                        state = Apply(_interpreter.Advance(graph, state, inbound, now), events, visited, lastEffects);
                    }

                    break;

                default:
                    outcome = state.Status switch
                    {
                        FlowStatus.Completed => "completed",
                        FlowStatus.HandedOff => "handed_off",
                        FlowStatus.Delegated => "delegated",
                        FlowStatus.Cancelled => "cancelled",
                        _ => "failed",
                    };
                    break;
            }
        }

        return new SimulationResult(events, state, outcome, visited);
    }

    private static InboundInput ToInbound(SimInput input) => input.Kind switch
    {
        SimInputKind.Choose => new InboundInput(ChoiceId: input.ChoiceId),
        SimInputKind.Media => new InboundInput(MediaKind: input.Text ?? "image"),
        _ => new InboundInput(Text: input.Text),
    };

    private static FlowInput Resolve(FlowState state, List<FlowEffect> lastEffects, IReadOnlyDictionary<string, NodeStub> stubs)
    {
        var effect = lastEffects.FirstOrDefault(e => e.Key == state.PendingEffectKey);
        stubs.TryGetValue(effect?.NodeId ?? string.Empty, out var stub);
        return effect switch
        {
            LookupKnowledgeEffect => new KnowledgeResultInput(effect.Key, stub?.Found ?? false, stub?.Answer),
            CallCapabilityEffect => stub is { Success: true }
                ? new CapabilityResultInput(effect.Key, true, stub.Fields)
                : new CapabilityResultInput(effect.Key, false, ErrorCode: stub?.ErrorCode ?? "simulated-failure"),
            _ => new TimeoutInput(),
        };
    }

    private static FlowState Apply(StepResult result, List<SimEvent> events, List<string> visited, List<FlowEffect> lastEffects)
    {
        visited.AddRange(result.Visited);
        lastEffects.Clear();
        lastEffects.AddRange(result.Effects);
        foreach (var e in result.Effects)
        {
            events.Add(e switch
            {
                SendMessageEffect m => new SimEvent("bot-message", m.NodeId, m.Text, m.Choices.Count > 0 ? m.Choices : null),
                LookupKnowledgeEffect k => new SimEvent("simulated-knowledge", k.NodeId, k.Query),
                CallCapabilityEffect c => new SimEvent("simulated-integration", c.NodeId, c.OperationKey, Data: c.Parameters),
                RequestHandoffEffect h => new SimEvent("handoff", h.NodeId, h.ReasonCode, Data: h.Summary),
                DelegateToAgentEffect d => new SimEvent("delegated-to-agent", d.NodeId),
                FinishEffect f => new SimEvent("finished", f.NodeId),
                _ => new SimEvent("effect", e.NodeId),
            });
        }

        return result.State;
    }
}
