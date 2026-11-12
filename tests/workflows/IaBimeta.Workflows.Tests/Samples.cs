using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    public static FlowNode Msg(string id, string text) => new(id, NodeType.Message, new NodeConfig(Text: text));

    public static FlowNode Ask(string id, string prompt, string variable, AnswerType type = AnswerType.Text, int? timeout = null, int attempts = 2, IReadOnlyList<FlowChoice>? choices = null, string? pattern = null) =>
        new(id, NodeType.Question, new NodeConfig(Prompt: prompt, Variable: variable, AnswerType: type, TimeoutSeconds: timeout, MaxAttempts: attempts, Choices: choices, Pattern: pattern, InvalidText: "Não entendi. " + prompt));

    public static FlowEdge E(string from, string port, string to) => new(from, port, to);

    public static FlowGraph Graph(string start, IEnumerable<FlowNode> nodes, IEnumerable<FlowEdge> edges, params FlowVariable[] vars) =>
        new(FlowGraph.CurrentSchemaVersion, start, nodes.ToList(), edges.ToList(), vars.ToList());

    /// <summary>Menu → (preço: consulta o ERP) | (horário: condição) | (outro: transfere). Cobre todos os tipos de nó.</summary>
    public static FlowGraph Menu() => Graph(
        "hello",
        [
            Msg("hello", "Olá! Sou o assistente."),
            Ask("menu", "Escolha: 1) Preço 2) Horário 3) Falar com atendente", "opcao", AnswerType.Choice, choices: [new("preco", "Preço"), new("horario", "Horário"), new("humano", "Falar com atendente")]),
            Ask("exame", "Qual o código do exame?", "codigo", timeout: 600),
            new("consulta", NodeType.Integration, new NodeConfig(OperationKey: "exam.price", ResultPrefix: "preco", Parameters: new Dictionary<string, ParamBinding> { ["code"] = new(Variable: "codigo") })),
            Msg("mostra", "O exame {{codigo}} custa R$ {{preco.valor}}."),
            new("route", NodeType.Condition, new NodeConfig(Branches: [new("preco", [new FlowCondition("opcao", ConditionOperator.Equals, "preco")]), new("horario", [new FlowCondition("opcao", ConditionOperator.Equals, "horario")]), new("humano", [new FlowCondition("opcao", ConditionOperator.Equals, "humano")])])),
            new("faq", NodeType.Knowledge, new NodeConfig(QueryVariable: null)),
            Msg("resposta", "{{knowledge.answer}}"),
            new("aberto", NodeType.Condition, new NodeConfig(Branches: [new("aberto", [new FlowCondition(Op: ConditionOperator.WithinBusinessHours, Schedule: new BusinessSchedule("UTC", [new BusinessWindow([1, 2, 3, 4, 5], "08:00", "18:00")]))])])),
            Msg("fechado", "Estamos fechados agora."),
            Msg("aberto-msg", "Estamos abertos até as 18h."),
            new("ia", NodeType.Ai),
            new("humano", NodeType.Handoff, new NodeConfig(Text: "Vou chamar um atendente.", ReasonCode: "customer_request", SummaryVariables: ["codigo"])),
            new("fim", NodeType.End, new NodeConfig(Text: "Até logo!")),
            Msg("falha", "Não consegui consultar agora."),
        ],
        [
            E("hello", "next", "menu"),
            E("menu", "answered", "route"),
            E("menu", "invalid", "humano"),
            E("route", "preco", "exame"), E("route", "horario", "aberto"), E("route", "humano", "humano"), E("route", "else", "faq"),
            E("exame", "answered", "consulta"),
            E("exame", "invalid", "humano"),
            E("exame", "timeout", "fim"),
            E("consulta", "success", "mostra"),
            E("consulta", "failure", "falha"),
            E("mostra", "next", "fim"),
            E("falha", "next", "humano"),
            E("faq", "found", "resposta"),
            E("faq", "not_found", "ia"),
            E("resposta", "next", "fim"),
            E("aberto", "aberto", "aberto-msg"),
            E("aberto", "else", "fechado"),
            E("aberto-msg", "next", "fim"),
            E("fechado", "next", "fim"),
        ],
        new FlowVariable("opcao", VariableClass.Public),
        new FlowVariable("codigo", VariableClass.Personal));
}
