using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <summary>
/// O interpretador determinístico (ADR 0037 §3): <c>(grafo, estado, entrada, instante) → (estado', efeitos)</c>. Função pura: sem I/O, sem relógio, sem aleatoriedade e sem saber o canal.
/// Quem realiza os efeitos é um executor (o runtime ou o simulador); o resultado de um efeito assíncrono volta como nova entrada.
/// </summary>
public sealed class FlowInterpreter
{
    private readonly FlowLimits _limits;

    public FlowInterpreter(FlowLimits? limits = null)
    {
        _limits = limits ?? new FlowLimits();
    }

    /// <summary>Inicia uma instância no nó inicial. <paramref name="first"/> é a mensagem do cliente que disparou o fluxo (vira <c>input.text</c>).</summary>
    public StepResult Start(FlowGraph graph, DateTimeOffset now, InboundInput? first = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (first?.Text is { } text)
        {
            vars[FlowNames.InputText] = Clip(text);
        }

        return Execute(graph, vars, step: 1, now, graph.Start, new List<FlowEffect>(), new List<string>(), attempts: 0);
    }

    public StepResult Advance(FlowGraph graph, FlowState state, FlowInput input, DateTimeOffset now)
    {
        if (state.Status.IsTerminal() || state.NodeId is null)
        {
            return Ignore(state);
        }

        var node = graph.Node(state.NodeId);
        if (node is null)
        {
            return Fail(state, state.NodeId, "node-missing", [], []);
        }

        var vars = new Dictionary<string, string>(state.Variables, StringComparer.Ordinal);
        var step = state.Step + 1;
        var cfg = node.Config ?? new NodeConfig();

        switch (state.Status)
        {
            case FlowStatus.WaitingInput when input is InboundInput inbound && node.Type == NodeType.Question:
                return OnAnswer(graph, state, node, cfg, inbound, vars, step, now);

            case FlowStatus.WaitingInput when input is TimeoutInput && node.Type == NodeType.Question:
                if (state.WaitUntil is null || now < state.WaitUntil)
                {
                    return Ignore(state);
                }

                return Leave(graph, vars, step, now, node, FlowNames.PortTimeout, [], [node.Id]);

            case FlowStatus.AwaitingEffect when input is CapabilityResultInput cap && node.Type == NodeType.Integration && cap.EffectKey == state.PendingEffectKey:
                {
                    var prefix = cfg.ResultPrefix ?? node.Id;
                    if (cap.Success && cap.Fields is not null)
                    {
                        foreach (var (name, value) in cap.Fields)
                        {
                            vars[prefix + "." + name] = Clip(value);
                        }
                    }
                    else if (!cap.Success)
                    {
                        vars[prefix + ".error"] = Clip(cap.ErrorCode ?? "failed");
                    }

                    return Leave(graph, vars, step, now, node, cap.Success ? FlowNames.PortSuccess : FlowNames.PortFailure, [], [node.Id]);
                }

            case FlowStatus.AwaitingEffect when input is KnowledgeResultInput kn && node.Type == NodeType.Knowledge && kn.EffectKey == state.PendingEffectKey:
                if (kn.Found && !string.IsNullOrWhiteSpace(kn.Answer))
                {
                    vars[FlowNames.KnowledgeAnswer] = Clip(kn.Answer);
                    return Leave(graph, vars, step, now, node, FlowNames.PortFound, [], [node.Id]);
                }

                vars.Remove(FlowNames.KnowledgeAnswer);
                return Leave(graph, vars, step, now, node, FlowNames.PortNotFound, [], [node.Id]);

            default:
                return Ignore(state);
        }
    }

    /// <summary>Cancela a instância (tomada por atendente, desativação). Idempotente.</summary>
    public StepResult Cancel(FlowState state, string reason)
    {
        if (state.Status.IsTerminal())
        {
            return Ignore(state);
        }

        return new StepResult(state with { Status = FlowStatus.Cancelled, Step = state.Step + 1, WaitUntil = null, PendingEffectKey = null, FailureCode = reason }, [], []);
    }

    private StepResult OnAnswer(FlowGraph graph, FlowState state, FlowNode node, NodeConfig cfg, InboundInput inbound, Dictionary<string, string> vars, long step, DateTimeOffset now)
    {
        if (inbound.Text is { } rawText)
        {
            vars[FlowNames.InputText] = Clip(rawText);
        }

        var value = AnswerParser.Parse(cfg, inbound, _limits);
        if (value is not null)
        {
            vars[cfg.Variable ?? FlowNames.InputText] = Clip(value);
            return Leave(graph, vars, step, now, node, FlowNames.PortAnswered, [], [node.Id], attempts: 0);
        }

        var attempts = state.Attempts + 1;
        var maxAttempts = Math.Clamp(cfg.MaxAttempts, 1, _limits.MaxAttemptsPerQuestion);
        if (attempts >= maxAttempts)
        {
            return Leave(graph, vars, step, now, node, FlowNames.PortInvalid, [], [node.Id], attempts: 0);
        }

        // Ainda há tentativas: repete a pergunta (texto de "inválido" se houver), sem sair do nó.
        var effects = new List<FlowEffect>();
        var text = TemplateRenderer.Render(string.IsNullOrWhiteSpace(cfg.InvalidText) ? cfg.Prompt : cfg.InvalidText, vars);
        effects.Add(new SendMessageEffect(Key(step, effects), node.Id, text, cfg.Choices ?? []));
        return new StepResult(
            new FlowState(FlowStatus.WaitingInput, node.Id, Freeze(vars), attempts, step, Deadline(cfg, now)),
            effects,
            [node.Id]);
    }

    private StepResult Leave(FlowGraph graph, Dictionary<string, string> vars, long step, DateTimeOffset now, FlowNode node, string port, List<FlowEffect> effects, List<string> visited, int attempts = 0)
    {
        var edge = graph.Edge(node.Id, port);
        if (edge is null)
        {
            return FailNew(vars, step, node.Id, "port-unconnected", effects, visited);
        }

        return Execute(graph, vars, step, now, edge.To, effects, visited, attempts);
    }

    private StepResult Execute(FlowGraph graph, Dictionary<string, string> vars, long step, DateTimeOffset now, string startNodeId, List<FlowEffect> effects, List<string> visited, int attempts)
    {
        var current = startNodeId;
        var budget = _limits.MaxStepsPerRun;
        while (true)
        {
            if (budget-- <= 0)
            {
                return FailNew(vars, step, current, "step-limit", effects, visited);
            }

            var node = graph.Node(current);
            if (node is null)
            {
                return FailNew(vars, step, current, "node-missing", effects, visited);
            }

            visited.Add(node.Id);
            var cfg = node.Config ?? new NodeConfig();
            switch (node.Type)
            {
                case NodeType.Message:
                    effects.Add(new SendMessageEffect(Key(step, effects), node.Id, TemplateRenderer.Render(cfg.Text, vars), []));
                    if (!TryNext(graph, node, FlowNames.PortNext, out current))
                    {
                        return FailNew(vars, step, node.Id, "port-unconnected", effects, visited);
                    }

                    continue;

                case NodeType.Question:
                    effects.Add(new SendMessageEffect(Key(step, effects), node.Id, TemplateRenderer.Render(cfg.Prompt, vars), cfg.Choices ?? []));
                    return new StepResult(new FlowState(FlowStatus.WaitingInput, node.Id, Freeze(vars), 0, step, Deadline(cfg, now)), effects, visited);

                case NodeType.Condition:
                    {
                        var port = FlowNames.PortElse;
                        foreach (var branch in cfg.Branches ?? [])
                        {
                            if (ConditionEvaluator.All(branch.All, vars, now))
                            {
                                port = branch.Port;
                                break;
                            }
                        }

                        if (!TryNext(graph, node, port, out current))
                        {
                            return FailNew(vars, step, node.Id, "port-unconnected", effects, visited);
                        }

                        continue;
                    }

                case NodeType.Knowledge:
                    {
                        var query = cfg.QueryVariable is { } qv && vars.TryGetValue(qv, out var qvalue) ? qvalue : vars.GetValueOrDefault(FlowNames.InputText, string.Empty);
                        var key = Key(step, effects);
                        effects.Add(new LookupKnowledgeEffect(key, node.Id, query, cfg.SourceKey));
                        return new StepResult(new FlowState(FlowStatus.AwaitingEffect, node.Id, Freeze(vars), 0, step, PendingEffectKey: key), effects, visited);
                    }

                case NodeType.Integration:
                    {
                        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var (name, binding) in cfg.Parameters ?? new Dictionary<string, ParamBinding>())
                        {
                            var v = binding.Variable is not null ? vars.GetValueOrDefault(binding.Variable) : binding.Literal;
                            if (v is not null)
                            {
                                parameters[name] = v;
                            }
                        }

                        var key = Key(step, effects);
                        effects.Add(new CallCapabilityEffect(key, node.Id, cfg.OperationKey ?? string.Empty, parameters));
                        return new StepResult(new FlowState(FlowStatus.AwaitingEffect, node.Id, Freeze(vars), 0, step, PendingEffectKey: key), effects, visited);
                    }

                case NodeType.Ai:
                    effects.Add(new DelegateToAgentEffect(Key(step, effects), node.Id));
                    return new StepResult(new FlowState(FlowStatus.Delegated, node.Id, Freeze(vars), 0, step), effects, visited);

                case NodeType.Handoff:
                    {
                        if (!string.IsNullOrWhiteSpace(cfg.Text))
                        {
                            effects.Add(new SendMessageEffect(Key(step, effects), node.Id, TemplateRenderer.Render(cfg.Text, vars), []));
                        }

                        var summary = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var name in cfg.SummaryVariables ?? [])
                        {
                            if (vars.TryGetValue(name, out var value))
                            {
                                summary[name] = value;
                            }
                        }

                        effects.Add(new RequestHandoffEffect(Key(step, effects), node.Id, cfg.ReasonCode ?? "flow_handoff", summary));
                        return new StepResult(new FlowState(FlowStatus.HandedOff, node.Id, Freeze(vars), 0, step), effects, visited);
                    }

                case NodeType.End:
                    if (!string.IsNullOrWhiteSpace(cfg.Text))
                    {
                        effects.Add(new SendMessageEffect(Key(step, effects), node.Id, TemplateRenderer.Render(cfg.Text, vars), []));
                    }

                    effects.Add(new FinishEffect(Key(step, effects), node.Id));
                    return new StepResult(new FlowState(FlowStatus.Completed, node.Id, Freeze(vars), 0, step), effects, visited);

                default:
                    return FailNew(vars, step, node.Id, "node-type-unknown", effects, visited);
            }
        }
    }

    private static bool TryNext(FlowGraph graph, FlowNode node, string port, out string next)
    {
        var edge = graph.Edge(node.Id, port);
        next = edge?.To ?? string.Empty;
        return edge is not null;
    }

    private static string Key(long step, List<FlowEffect> effects) => $"{step}:{effects.Count}";

    private static DateTimeOffset? Deadline(NodeConfig cfg, DateTimeOffset now) => cfg.TimeoutSeconds is > 0 and var s ? now.AddSeconds(s) : null;

    private string Clip(string value) => value.Length <= _limits.MaxVariableLength ? value : value[.._limits.MaxVariableLength];

    private static IReadOnlyDictionary<string, string> Freeze(Dictionary<string, string> vars) => new Dictionary<string, string>(vars, StringComparer.Ordinal);

    private static StepResult Ignore(FlowState state) => new(state, [], [], Ignored: true);

    private static StepResult Fail(FlowState state, string nodeId, string code, List<FlowEffect> effects, List<string> visited) =>
        new(state with { Status = FlowStatus.Failed, NodeId = nodeId, Step = state.Step + 1, WaitUntil = null, PendingEffectKey = null, FailureCode = code }, effects, visited);

    private static StepResult FailNew(Dictionary<string, string> vars, long step, string nodeId, string code, List<FlowEffect> effects, List<string> visited) =>
        new(new FlowState(FlowStatus.Failed, nodeId, Freeze(vars), 0, step, FailureCode: code), effects, visited);
}
