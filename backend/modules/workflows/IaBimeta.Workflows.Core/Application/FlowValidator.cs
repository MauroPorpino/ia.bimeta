using System.Text.RegularExpressions;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <param name="NodeId">O nó do problema (o editor o destaca), ou nulo para problema do grafo.</param>
/// <param name="Code">Código curto e estável (<c>cycle-without-wait</c>, <c>port-unconnected</c>...).</param>
public sealed record FlowProblem(string? NodeId, string Code, string Message);

public sealed record FlowValidationResult(IReadOnlyList<FlowProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>O que o validador sabe de uma operação do catálogo (o Capabilities, por porta; nulo = sem verificação de catálogo).</summary>
/// <param name="Eligible">Publicada, habilitada, de leitura, <c>Public</c> e autorizada para <c>Workflow</c> (ADR 0037 §2).</param>
/// <param name="Parameters">Os parâmetros declarados pela operação (nome e obrigatoriedade), se o catálogo os conhece.</param>
public sealed record FlowOperationInfo(bool Exists, bool Eligible, IReadOnlyList<FlowOperationParameter>? Parameters = null);

public sealed record FlowOperationParameter(string Name, bool Required);

public interface IFlowCatalogView
{
    FlowOperationInfo? Operation(string key);
}

/// <summary>Valida o grafo antes de salvar e de publicar (ADR 0037 §2). Todo problema é um código com o nó, para o editor apontar.</summary>
public static partial class FlowValidator
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_.]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static FlowValidationResult Validate(FlowGraph graph, FlowLimits? limits = null, IFlowCatalogView? catalog = null)
    {
        limits ??= new FlowLimits();
        var problems = new List<FlowProblem>();
        void Add(string? node, string code, string message) => problems.Add(new FlowProblem(node, code, message));

        if (graph.SchemaVersion != FlowGraph.CurrentSchemaVersion)
        {
            Add(null, "schema-version", "Versão de esquema do grafo não suportada.");
            return new FlowValidationResult(problems);
        }

        if (graph.Nodes.Count == 0 || graph.Nodes.Count > limits.MaxNodes)
        {
            Add(null, "node-count", $"O fluxo precisa ter de 1 a {limits.MaxNodes} nós.");
            return new FlowValidationResult(problems);
        }

        if (graph.Edges.Count > limits.MaxEdges)
        {
            Add(null, "edge-count", $"O fluxo aceita no máximo {limits.MaxEdges} conexões.");
            return new FlowValidationResult(problems);
        }

        var declared = new Dictionary<string, VariableClass>(StringComparer.Ordinal);
        ValidateVariables(graph, limits, declared, Add);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!NodeIdPattern().IsMatch(n.Id ?? string.Empty))
            {
                Add(n.Id, "node-id", "Identificador de nó inválido.");
            }
            else if (!ids.Add(n.Id!))
            {
                Add(n.Id, "node-id-duplicate", "Identificador de nó repetido.");
            }

            if (n.Label is { Length: > 100 })
            {
                Add(n.Id, "label-length", "Rótulo longo demais.");
            }
        }

        if (!ids.Contains(graph.Start ?? string.Empty))
        {
            Add(null, "start-missing", "O nó inicial não existe.");
        }

        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes.Where(n => n.Config?.ResultPrefix is not null))
        {
            prefixes.Add(n.Config!.ResultPrefix!);
        }

        bool Known(string v) =>
            declared.ContainsKey(v) || v is FlowNames.InputText or FlowNames.KnowledgeAnswer || prefixes.Any(p => v.StartsWith(p + ".", StringComparison.Ordinal));

        var branchPorts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Where(n => n.Id is not null && NodeIdPattern().IsMatch(n.Id)))
        {
            ValidateNode(node, limits, catalog, declared, Known, seenPrefixes, branchPorts, Add);
        }

        ValidateEdges(graph, branchPorts, Add);
        ValidateStructure(graph, ids, Add);
        return new FlowValidationResult(problems);
    }

    private static void ValidateVariables(FlowGraph graph, FlowLimits limits, Dictionary<string, VariableClass> declared, Action<string?, string, string> add)
    {
        var vars = graph.Variables ?? [];
        if (vars.Count > limits.MaxVariables)
        {
            add(null, "variable-count", $"O fluxo aceita no máximo {limits.MaxVariables} variáveis.");
        }

        foreach (var v in vars)
        {
            if (!VariablePattern().IsMatch(v.Name ?? string.Empty) || v.Name is FlowNames.InputText or FlowNames.KnowledgeAnswer)
            {
                add(null, "variable-name", "Nome de variável inválido ou reservado.");
            }
            else if (!declared.TryAdd(v.Name!, v.Class))
            {
                add(null, "variable-duplicate", $"Variável repetida: {v.Name}.");
            }
        }
    }

    private static void ValidateNode(
        FlowNode node,
        FlowLimits limits,
        IFlowCatalogView? catalog,
        Dictionary<string, VariableClass> declared,
        Func<string, bool> known,
        HashSet<string> seenPrefixes,
        Dictionary<string, HashSet<string>> branchPorts,
        Action<string?, string, string> add)
    {
        var id = node.Id;
        var cfg = node.Config ?? new NodeConfig();

        void Text(string? text, string field, bool required)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                if (required)
                {
                    add(id, "text-required", $"O campo '{field}' é obrigatório.");
                }

                return;
            }

            if (text.Length > limits.MaxTextLength)
            {
                add(id, "text-length", $"O campo '{field}' passa de {limits.MaxTextLength} caracteres.");
            }

            foreach (var v in TemplateRenderer.ReferencedVariables(text))
            {
                if (!known(v))
                {
                    add(id, "variable-undeclared", $"A variável '{v}' não foi declarada.");
                }
            }
        }

        void VarRef(string? name, string field)
        {
            if (name is not null && !known(name))
            {
                add(id, "variable-undeclared", $"A variável '{name}' (campo '{field}') não foi declarada.");
            }
        }

        switch (node.Type)
        {
            case NodeType.Message:
                Text(cfg.Text, "texto", required: true);
                break;

            case NodeType.Question:
                ValidateQuestion(node, cfg, limits, declared, Text, add);
                break;

            case NodeType.Condition:
                ValidateCondition(node, cfg, limits, VarRef, branchPorts, add);
                break;

            case NodeType.Knowledge:
                VarRef(cfg.QueryVariable, "variável da consulta");
                if (cfg.SourceKey is { Length: > 64 })
                {
                    add(id, "source-key", "Chave de fonte longa demais.");
                }

                break;

            case NodeType.Integration:
                ValidateIntegration(node, cfg, limits, catalog, seenPrefixes, VarRef, add);
                break;

            case NodeType.Handoff:
                Text(cfg.Text, "aviso", required: false);
                if (cfg.ReasonCode is not null && !KeyPattern().IsMatch(cfg.ReasonCode))
                {
                    add(id, "reason-code", "Código de motivo inválido.");
                }

                foreach (var v in cfg.SummaryVariables ?? [])
                {
                    VarRef(v, "resumo");
                }

                break;

            case NodeType.End:
                Text(cfg.Text, "texto final", required: false);
                break;

            case NodeType.Ai:
                break;

            default:
                add(id, "node-type", "Tipo de nó desconhecido.");
                break;
        }
    }

    private static void ValidateQuestion(FlowNode node, NodeConfig cfg, FlowLimits limits, Dictionary<string, VariableClass> declared, Action<string?, string, bool> text, Action<string?, string, string> add)
    {
        var id = node.Id;
        text(cfg.Prompt, "pergunta", true);
        text(cfg.InvalidText, "texto de resposta inválida", false);

        if (cfg.Variable is null || !declared.TryGetValue(cfg.Variable, out var cls))
        {
            add(id, "variable-undeclared", "A pergunta precisa de uma variável declarada para a resposta.");
        }
        else if (cfg.AnswerType != AnswerType.Choice && cls != VariableClass.Personal)
        {
            add(id, "variable-class", "A resposta livre do cliente vai em variável pessoal (cifrada em repouso).");
        }

        if (cfg.MaxAttempts < 1 || cfg.MaxAttempts > limits.MaxAttemptsPerQuestion)
        {
            add(id, "attempts", $"As tentativas vão de 1 a {limits.MaxAttemptsPerQuestion}.");
        }

        if (cfg.TimeoutSeconds is { } t && (t < 1 || t > limits.MaxTimeoutSeconds))
        {
            add(id, "timeout", $"O prazo vai de 1 a {limits.MaxTimeoutSeconds} segundos.");
        }

        if (cfg.AnswerType == AnswerType.Choice)
        {
            var choices = cfg.Choices ?? [];
            if (choices.Count < 2 || choices.Count > limits.MaxChoices)
            {
                add(id, "choices", $"A pergunta de escolha precisa de 2 a {limits.MaxChoices} opções.");
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in choices)
            {
                if (!PortPattern().IsMatch(c.Id ?? string.Empty) || !seenIds.Add(c.Id!))
                {
                    add(id, "choice-id", "Identificador de opção inválido ou repetido.");
                }

                if (string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > limits.MaxChoiceLabelLength || !seenLabels.Add(AnswerParser.Normalize(c.Label)))
                {
                    add(id, "choice-label", $"Rótulo de opção vazio, repetido ou com mais de {limits.MaxChoiceLabelLength} caracteres.");
                }
            }
        }
        else if (cfg.Choices is { Count: > 0 })
        {
            add(id, "choices-unexpected", "Só a pergunta de escolha tem opções.");
        }

        if (cfg.AnswerType == AnswerType.Pattern)
        {
            try
            {
                if (string.IsNullOrEmpty(cfg.Pattern) || cfg.Pattern.Length > 200)
                {
                    throw new ArgumentException("pattern");
                }

                _ = new Regex(cfg.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                add(id, "pattern", "Padrão vazio, longo demais ou não suportado (sem referências e olhar adiante).");
            }
        }
    }

    private static void ValidateCondition(FlowNode node, NodeConfig cfg, FlowLimits limits, Action<string?, string> varRef, Dictionary<string, HashSet<string>> branchPorts, Action<string?, string, string> add)
    {
        var id = node.Id;
        var branches = cfg.Branches ?? [];
        if (branches.Count is 0 || branches.Count > limits.MaxBranchesPerCondition)
        {
            add(id, "branches", $"A condição precisa de 1 a {limits.MaxBranchesPerCondition} ramos.");
        }

        var ports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in branches)
        {
            if (!PortPattern().IsMatch(b.Port ?? string.Empty) || b.Port == FlowNames.PortElse || !ports.Add(b.Port!))
            {
                add(id, "branch-port", "Nome de ramo inválido, reservado ou repetido.");
            }

            if (b.All is null || b.All.Count == 0)
            {
                add(id, "branch-empty", "Um ramo precisa de ao menos uma condição.");
                continue;
            }

            foreach (var c in b.All)
            {
                ValidateRule(id, c, varRef, add);
            }
        }

        ports.Add(FlowNames.PortElse);
        branchPorts[id] = ports;
    }

    private static void ValidateRule(string id, FlowCondition c, Action<string?, string> varRef, Action<string?, string, string> add)
    {
        if (c.Op == ConditionOperator.WithinBusinessHours)
        {
            var s = c.Schedule;
            var ok = s is { Windows.Count: > 0 };
            if (ok)
            {
                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(s!.TimeZone);
                }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    ok = false;
                }
            }

            if (ok)
            {
                foreach (var w in s!.Windows)
                {
                    if (w.Days is null || w.Days.Count == 0 || w.Days.Any(d => d is < 1 or > 7) ||
                        !ConditionEvaluator.TryParseTime(w.From, out var from) || !ConditionEvaluator.TryParseTime(w.To, out var to) || from >= to)
                    {
                        ok = false;
                    }
                }
            }

            if (!ok)
            {
                add(id, "schedule", "Horário de atendimento inválido (fuso, dias 1 a 7 e horas HH:mm com início antes do fim).");
            }

            return;
        }

        if (string.IsNullOrEmpty(c.Variable))
        {
            add(id, "condition-variable", "A condição precisa de uma variável.");
        }
        else
        {
            varRef(c.Variable, "condição");
        }

        var needsValue = c.Op is ConditionOperator.Equals or ConditionOperator.NotEquals or ConditionOperator.Contains or ConditionOperator.GreaterThan or ConditionOperator.LessThan;
        if (needsValue && string.IsNullOrEmpty(c.Value))
        {
            add(id, "condition-value", "A condição precisa de um valor.");
        }

        if (c.Op == ConditionOperator.In && (c.Values is null || c.Values.Count == 0 || c.Values.Count > 50))
        {
            add(id, "condition-values", "A condição 'em lista' precisa de 1 a 50 valores.");
        }
    }

    private static void ValidateIntegration(
        FlowNode node,
        NodeConfig cfg,
        FlowLimits limits,
        IFlowCatalogView? catalog,
        HashSet<string> seenPrefixes,
        Action<string?, string> varRef,
        Action<string?, string, string> add)
    {
        var id = node.Id;
        if (cfg.OperationKey is null || !KeyPattern().IsMatch(cfg.OperationKey))
        {
            add(id, "operation-key", "Chave de operação ausente ou inválida.");
        }
        else if (catalog is not null)
        {
            var info = catalog.Operation(cfg.OperationKey);
            if (info is not { Exists: true })
            {
                add(id, "operation-missing", "A operação não existe no catálogo ou não é elegível para fluxos (publicada, habilitada, de leitura, pública e autorizada para fluxos).");
            }
            else if (!info.Eligible)
            {
                add(id, "operation-not-eligible", "A operação não está publicada, habilitada, de leitura, pública e autorizada para fluxos.");
            }
            else if (info.Parameters is { } declared)
            {
                var bound = cfg.Parameters ?? new Dictionary<string, ParamBinding>();
                foreach (var name in bound.Keys.Where(k => !declared.Any(d => d.Name == k)))
                {
                    add(id, "parameter-unknown", $"A operação não tem o parâmetro '{name}'.");
                }

                foreach (var d in declared.Where(d => d.Required && !bound.ContainsKey(d.Name)))
                {
                    add(id, "parameter-missing", $"O parâmetro obrigatório '{d.Name}' não foi ligado.");
                }
            }
        }

        if (cfg.ResultPrefix is null || !VariablePattern().IsMatch(cfg.ResultPrefix) || cfg.ResultPrefix is "input" or "knowledge" || !seenPrefixes.Add(cfg.ResultPrefix!))
        {
            add(id, "result-prefix", "Prefixo de resultado ausente, inválido, reservado ou repetido.");
        }

        var parameters = cfg.Parameters ?? new Dictionary<string, ParamBinding>();
        if (parameters.Count > limits.MaxParameters)
        {
            add(id, "parameter-count", $"No máximo {limits.MaxParameters} parâmetros.");
        }

        foreach (var (name, binding) in parameters)
        {
            if (!KeyPattern().IsMatch(name) || (binding.Variable is null) == (binding.Literal is null))
            {
                add(id, "parameter-binding", $"O parâmetro '{name}' precisa de exatamente uma origem: variável ou valor fixo.");
                continue;
            }

            if (binding.Literal is { Length: > 500 })
            {
                add(id, "parameter-literal", $"O valor fixo de '{name}' é longo demais.");
            }

            varRef(binding.Variable, "parâmetro");
        }
    }

    private static string[] PortsOf(FlowNode node, Dictionary<string, HashSet<string>> branchPorts)
    {
        var cfg = node.Config ?? new NodeConfig();
        return node.Type switch
        {
            NodeType.Message => [FlowNames.PortNext],
            NodeType.Question => cfg.TimeoutSeconds is > 0 ? [FlowNames.PortAnswered, FlowNames.PortInvalid, FlowNames.PortTimeout] : [FlowNames.PortAnswered, FlowNames.PortInvalid],
            NodeType.Condition => branchPorts.TryGetValue(node.Id, out var p) ? [.. p] : [FlowNames.PortElse],
            NodeType.Knowledge => [FlowNames.PortFound, FlowNames.PortNotFound],
            NodeType.Integration => [FlowNames.PortSuccess, FlowNames.PortFailure],
            _ => [],
        };
    }

    private static void ValidateEdges(FlowGraph graph, Dictionary<string, HashSet<string>> branchPorts, Action<string?, string, string> add)
    {
        var byId = graph.Nodes.Where(n => n.Id is not null).GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var used = new HashSet<(string, string)>();
        foreach (var e in graph.Edges)
        {
            if (!byId.TryGetValue(e.From ?? string.Empty, out var from) || !byId.ContainsKey(e.To ?? string.Empty))
            {
                add(e.From, "edge-endpoint", "Uma conexão aponta para um nó que não existe.");
                continue;
            }

            if (!PortsOf(from, branchPorts).Contains(e.Port, StringComparer.Ordinal))
            {
                add(e.From, "port-invalid", $"A porta '{e.Port}' não existe neste nó.");
                continue;
            }

            if (!used.Add((e.From!, e.Port)))
            {
                add(e.From, "port-duplicate", $"A porta '{e.Port}' tem mais de uma conexão.");
            }
        }

        foreach (var n in byId.Values)
        {
            foreach (var port in PortsOf(n, branchPorts))
            {
                if (!used.Contains((n.Id, port)))
                {
                    add(n.Id, "port-unconnected", $"A saída '{port}' não está conectada.");
                }
            }
        }
    }

    private static void ValidateStructure(FlowGraph graph, HashSet<string> ids, Action<string?, string, string> add)
    {
        if (!ids.Contains(graph.Start ?? string.Empty))
        {
            return;
        }

        var adjacency = graph.Edges
            .Where(e => e.From is not null && e.To is not null)
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var reachable = new HashSet<string>(StringComparer.Ordinal) { graph.Start! };
        var queue = new Queue<string>([graph.Start!]);
        while (queue.Count > 0)
        {
            foreach (var next in adjacency.GetValueOrDefault(queue.Dequeue()) ?? [])
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        foreach (var n in graph.Nodes.Where(n => ids.Contains(n.Id) && !reachable.Contains(n.Id)))
        {
            add(n.Id, "unreachable-node", "Este nó não é alcançável a partir do início.");
        }

        // Todo ciclo precisa passar por um nó que espera entrada (pergunta): sem isso o fluxo giraria sem parar.
        var waits = graph.Nodes.Where(n => n.Type == NodeType.Question).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var color = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes.Where(n => ids.Contains(n.Id) && !waits.Contains(n.Id)))
        {
            if (HasCycle(n.Id, adjacency, waits, color))
            {
                add(n.Id, "cycle-without-wait", "Há um ciclo sem nenhuma pergunta: o fluxo giraria sem esperar o cliente.");
                break;
            }
        }
    }

    private static bool HasCycle(string start, Dictionary<string, List<string>> adjacency, HashSet<string> waits, Dictionary<string, int> color)
    {
        // DFS iterativa (grafo de até algumas centenas de nós; sem recursão para não estourar a pilha).
        var stack = new Stack<(string Node, int Index)>();
        if (color.GetValueOrDefault(start) != 0)
        {
            return false;
        }

        stack.Push((start, 0));
        color[start] = 1;
        while (stack.Count > 0)
        {
            var (node, index) = stack.Pop();
            var next = (adjacency.GetValueOrDefault(node) ?? []).Where(x => !waits.Contains(x)).ToList();
            if (index < next.Count)
            {
                stack.Push((node, index + 1));
                var target = next[index];
                switch (color.GetValueOrDefault(target))
                {
                    case 1:
                        return true;
                    case 0:
                        color[target] = 1;
                        stack.Push((target, 0));
                        break;
                }
            }
            else
            {
                color[node] = 2;
            }
        }

        return false;
    }
}
