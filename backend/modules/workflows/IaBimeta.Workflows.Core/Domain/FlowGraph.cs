namespace IaBimeta.Workflows.Core.Domain;

// O contrato do grafo (ADR 0037 §2). É o MESMO documento que o editor visual lê e grava e que o servidor valida: nenhuma regra de negócio mora no editor.

public enum NodeType
{
    Message = 1,
    Question = 2,
    Condition = 3,
    Knowledge = 4,
    Ai = 5,
    Integration = 6,
    Handoff = 7,
    End = 8,
}

/// <summary>Classe de uma variável (ADR 0037 §5): <c>Personal</c> é cifrada em repouso e nunca aparece em log, auditoria ou resposta administrativa.</summary>
public enum VariableClass
{
    Public = 1,
    Personal = 2,
}

public enum AnswerType
{
    Text = 1,
    Choice = 2,
    Number = 3,
    Email = 4,
    Phone = 5,
    Date = 6,
    Pattern = 7,
}

/// <summary>Operadores FIXOS das condições declarativas. Não há script nem expressão arbitrária (ADR 0037 §2).</summary>
public enum ConditionOperator
{
    Equals = 1,
    NotEquals = 2,
    Contains = 3,
    IsEmpty = 4,
    IsNotEmpty = 5,
    GreaterThan = 6,
    LessThan = 7,
    In = 8,
    WithinBusinessHours = 9,
}

public sealed record FlowChoice(string Id, string Label);

/// <param name="Days">Dias da semana de 1 (segunda) a 7 (domingo).</param>
/// <param name="From">Hora inicial <c>HH:mm</c> (inclusive).</param>
/// <param name="To">Hora final <c>HH:mm</c> (exclusive).</param>
public sealed record BusinessWindow(IReadOnlyList<int> Days, string From, string To);

public sealed record BusinessSchedule(string TimeZone, IReadOnlyList<BusinessWindow> Windows);

public sealed record FlowCondition(
    string? Variable = null,
    ConditionOperator Op = ConditionOperator.Equals,
    string? Value = null,
    IReadOnlyList<string>? Values = null,
    BusinessSchedule? Schedule = null);

/// <summary>Um ramo do nó de condição: todas as condições precisam valer (E). A porta de saída tem o nome <see cref="Port"/>.</summary>
public sealed record ConditionBranch(string Port, IReadOnlyList<FlowCondition> All);

/// <summary>Valor de um parâmetro de operação: de uma variável ou literal. Nunca texto livre do cliente como molde de chamada (ADR 0035 §2).</summary>
public sealed record ParamBinding(string? Variable = null, string? Literal = null);

/// <summary>Configuração plana de um nó; cada tipo usa só os campos que lhe cabem (o validador recusa o resto não nulo).</summary>
public sealed record NodeConfig(
    string? Text = null,
    string? Prompt = null,
    AnswerType AnswerType = AnswerType.Text,
    string? Variable = null,
    IReadOnlyList<FlowChoice>? Choices = null,
    string? Pattern = null,
    int MaxAttempts = 2,
    int? TimeoutSeconds = null,
    string? InvalidText = null,
    IReadOnlyList<ConditionBranch>? Branches = null,
    string? QueryVariable = null,
    string? SourceKey = null,
    string? OperationKey = null,
    IReadOnlyDictionary<string, ParamBinding>? Parameters = null,
    string? ResultPrefix = null,
    string? ReasonCode = null,
    IReadOnlyList<string>? SummaryVariables = null);

/// <summary>Posição no canvas: só do editor; fora do hash e do significado do fluxo.</summary>
public sealed record FlowPosition(double X, double Y);

public sealed record FlowNode(string Id, NodeType Type, NodeConfig? Config = null, string? Label = null, FlowPosition? Position = null);

public sealed record FlowEdge(string From, string Port, string To);

public sealed record FlowVariable(string Name, VariableClass Class = VariableClass.Public);

public sealed record FlowGraph(
    int SchemaVersion,
    string Start,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<FlowVariable>? Variables = null)
{
    public const int CurrentSchemaVersion = 1;

    public FlowNode? Node(string id)
    {
        foreach (var n in Nodes)
        {
            if (string.Equals(n.Id, id, StringComparison.Ordinal))
            {
                return n;
            }
        }

        return null;
    }

    public FlowEdge? Edge(string from, string port)
    {
        foreach (var e in Edges)
        {
            if (string.Equals(e.From, from, StringComparison.Ordinal) && string.Equals(e.Port, port, StringComparison.Ordinal))
            {
                return e;
            }
        }

        return null;
    }
}

/// <summary>Nomes de variáveis do sistema e portas fixas.</summary>
public static class FlowNames
{
    /// <summary>O texto da última mensagem do cliente (sempre disponível, classe pessoal).</summary>
    public const string InputText = "input.text";

    /// <summary>A resposta aprovada devolvida pelo nó de conhecimento (classe pública).</summary>
    public const string KnowledgeAnswer = "knowledge.answer";

    public const string PortNext = "next";
    public const string PortAnswered = "answered";
    public const string PortInvalid = "invalid";
    public const string PortTimeout = "timeout";
    public const string PortElse = "else";
    public const string PortFound = "found";
    public const string PortNotFound = "not_found";
    public const string PortSuccess = "success";
    public const string PortFailure = "failure";
}

/// <summary>Limites configuráveis de tamanho e execução (ADR 0037 §2 e §4).</summary>
public sealed record FlowLimits(
    int MaxNodes = 200,
    int MaxEdges = 600,
    int MaxTextLength = 4096,
    int MaxChoices = 10,
    int MaxChoiceLabelLength = 24,
    int MaxStepsPerRun = 50,
    int MaxVariables = 100,
    int MaxVariableLength = 2000,
    int MaxDocumentBytes = 262144,
    int MaxAttemptsPerQuestion = 5,
    int MaxTimeoutSeconds = 172800,
    int MaxBranchesPerCondition = 10,
    int MaxParameters = 20);
