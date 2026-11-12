using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using static IaBimeta.Workflows.Tests.Samples;

namespace IaBimeta.Workflows.Tests;

public sealed class ValidatorTests
{
    private static string[] Codes(FlowGraph g, IFlowCatalogView? catalog = null) => FlowValidator.Validate(g, null, catalog).Problems.Select(p => p.Code).ToArray();

    [Fact]
    public void The_reference_flow_is_valid()
    {
        var result = FlowValidator.Validate(Menu());
        Assert.True(result.IsValid, string.Join(", ", result.Problems.Select(p => p.NodeId + ":" + p.Code)));
    }

    [Fact]
    public void Unconnected_port_unreachable_node_and_dangling_edge_are_reported_with_the_node()
    {
        var g = Graph("a", [Msg("a", "x"), Msg("b", "y"), Msg("orfao", "z"), new("fim", NodeType.End)], [E("a", "next", "b"), E("b", "next", "fim"), E("orfao", "next", "nada")]);
        var problems = FlowValidator.Validate(g).Problems;

        Assert.Contains(problems, p => p.Code == "unreachable-node" && p.NodeId == "orfao");
        Assert.Contains(problems, p => p.Code == "edge-endpoint");
        var open = Graph("a", [Msg("a", "x")], []);
        Assert.Contains(FlowValidator.Validate(open).Problems, p => p is { Code: "port-unconnected", NodeId: "a" });
    }

    [Fact]
    public void A_cycle_without_a_question_is_rejected_but_a_question_loop_is_allowed()
    {
        var bad = Graph("a", [Msg("a", "x"), Msg("b", "y")], [E("a", "next", "b"), E("b", "next", "a")]);
        Assert.Contains("cycle-without-wait", Codes(bad));

        var good = Graph(
            "q",
            [Ask("q", "Tudo bem?", "r", AnswerType.Choice, choices: [new("sim", "Sim"), new("nao", "Não")]), new("fim", NodeType.End)],
            [E("q", "answered", "q"), E("q", "invalid", "fim")],
            new FlowVariable("r", VariableClass.Public));
        Assert.DoesNotContain("cycle-without-wait", Codes(good));
    }

    [Fact]
    public void Undeclared_variables_and_wrong_classes_are_rejected()
    {
        var g = Graph("m", [Msg("m", "Oi {{ninguem}}"), new("fim", NodeType.End)], [E("m", "next", "fim")]);
        Assert.Contains("variable-undeclared", Codes(g));

        var free = Graph("q", [Ask("q", "Nome?", "nome"), new("fim", NodeType.End)], [E("q", "answered", "fim"), E("q", "invalid", "fim")], new FlowVariable("nome", VariableClass.Public));
        Assert.Contains("variable-class", Codes(free));
    }

    [Fact]
    public void Integration_requires_an_eligible_operation_a_unique_prefix_and_exactly_one_binding_source()
    {
        IFlowCatalogView catalog = new Catalog(("ok.read", true), ("private.read", false));
        FlowGraph G(string key, string prefix = "r", ParamBinding? b = null) => Graph(
            "i",
            [new("i", NodeType.Integration, new NodeConfig(OperationKey: key, ResultPrefix: prefix, Parameters: new Dictionary<string, ParamBinding> { ["p"] = b ?? new ParamBinding(Literal: "1") })), new("fim", NodeType.End)],
            [E("i", "success", "fim"), E("i", "failure", "fim")]);

        Assert.DoesNotContain("operation-not-eligible", Codes(G("ok.read"), catalog));
        Assert.Contains("operation-not-eligible", Codes(G("private.read"), catalog));
        Assert.Contains("operation-missing", Codes(G("nao.existe"), catalog));
        Assert.Contains("result-prefix", Codes(G("ok.read", "input"), catalog));
        Assert.Contains("parameter-binding", Codes(G("ok.read", b: new ParamBinding(Variable: "a", Literal: "b")), catalog));
    }

    [Fact]
    public void Limits_and_bad_shapes_are_rejected()
    {
        var many = Enumerable.Range(0, 5).Select(i => Msg("n" + i, "x")).ToList();
        var tiny = new FlowLimits(MaxNodes: 3);
        Assert.Contains(FlowValidator.Validate(Graph("n0", many, []), tiny).Problems, p => p.Code == "node-count");

        Assert.Contains("schema-version", Codes(new FlowGraph(99, "a", [Msg("a", "x")], [])));
        var longText = Graph("a", [Msg("a", new string('x', 5000)), new("f", NodeType.End)], [E("a", "next", "f")]);
        Assert.Contains("text-length", Codes(longText));

        var badPattern = Graph("q", [Ask("q", "x", "v", AnswerType.Pattern, pattern: "(?<=a)b"), new("f", NodeType.End)], [E("q", "answered", "f"), E("q", "invalid", "f")], new FlowVariable("v", VariableClass.Personal));
        Assert.Contains("pattern", Codes(badPattern));
    }

    private sealed class Catalog(params (string Key, bool Eligible)[] ops) : IFlowCatalogView
    {
        public FlowOperationInfo? Operation(string key) => ops.Where(o => o.Key == key).Select(o => new FlowOperationInfo(true, o.Eligible)).FirstOrDefault();
    }
}

public sealed class SerializerTests
{
    [Fact]
    public void Round_trip_keeps_the_graph_and_the_hash_ignores_positions_and_labels()
    {
        var g = Menu();
        var json = FlowGraphSerializer.Serialize(g);
        Assert.True(FlowGraphSerializer.TryParse(json, new FlowLimits(), out var back, out var error), error);
        Assert.Equal(FlowGraphSerializer.Hash(g), FlowGraphSerializer.Hash(back!));

        var moved = g with { Nodes = g.Nodes.Select(n => n with { Position = new FlowPosition(10, 20), Label = "renomeado" }).ToList() };
        Assert.Equal(FlowGraphSerializer.Hash(g), FlowGraphSerializer.Hash(moved));

        var changed = g with { Nodes = g.Nodes.Select(n => n.Id == "hello" ? n with { Config = new NodeConfig(Text: "Outro texto") } : n).ToList() };
        Assert.NotEqual(FlowGraphSerializer.Hash(g), FlowGraphSerializer.Hash(changed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ nao e json")]
    [InlineData("""{"schemaVersion":1,"start":"a","nodes":[],"edges":[],"surpresa":1}""")]
    [InlineData("""{"schemaVersion":1,"start":"a","nodes":[{"id":"a","type":"scriptNode"}],"edges":[]}""")]
    [InlineData("""{"schemaVersion":1,"start":"a","nodes":[{"id":"a","type":1}],"edges":[]}""")]
    public void Strict_parsing_refuses_unknown_fields_types_and_integers_without_echoing_the_content(string json)
    {
        Assert.False(FlowGraphSerializer.TryParse(json, new FlowLimits(), out var graph, out var error));
        Assert.Null(graph);
        Assert.DoesNotContain("surpresa", error);
    }

    [Fact]
    public void Oversized_documents_are_refused_before_parsing()
    {
        var json = FlowGraphSerializer.Serialize(Menu());
        Assert.False(FlowGraphSerializer.TryParse(json, new FlowLimits(MaxDocumentBytes: 100), out _, out var error));
        Assert.Equal("document-too-large", error);
    }
}

public sealed class SimulatorTests
{
    private readonly FlowSimulator _sim = new();

    [Fact]
    public void Price_scenario_uses_the_stub_never_a_real_call_and_reports_the_path()
    {
        var scenario = new SimulationScenario(
            [new(SimInputKind.Say, "oi"), new(SimInputKind.Choose, ChoiceId: "preco"), new(SimInputKind.Say, "HEM-01")],
            new Dictionary<string, NodeStub> { ["consulta"] = new(Success: true, Fields: new Dictionary<string, string> { ["valor"] = "9,90" }) });

        var r = _sim.Run(Menu(), scenario);

        Assert.Equal("completed", r.Outcome);
        Assert.Contains(r.Events, e => e is { Kind: "simulated-integration", Text: "exam.price" });
        Assert.Contains(r.Events, e => e is { Kind: "bot-message" } && e.Text == "O exame HEM-01 custa R$ 9,90.");
        Assert.Equal(["hello", "menu", "route", "exame", "consulta", "mostra", "fim"], r.Visited.Distinct());
    }

    [Fact]
    public void Missing_stub_means_failure_and_the_virtual_clock_fires_the_timeout()
    {
        var failure = _sim.Run(Menu(), new SimulationScenario([new(SimInputKind.Say, "oi"), new(SimInputKind.Choose, ChoiceId: "preco"), new(SimInputKind.Say, "X")]));
        Assert.Equal("handed_off", failure.Outcome);

        var timeout = _sim.Run(Menu(), new SimulationScenario([new(SimInputKind.Say, "oi"), new(SimInputKind.Choose, ChoiceId: "preco"), new(SimInputKind.Wait, Seconds: 601)]));
        Assert.Equal("completed", timeout.Outcome);
        Assert.Contains(timeout.Events, e => e.Kind == "time-passed");
    }

    [Fact]
    public void Running_out_of_inputs_reports_waiting_and_the_agent_delegate_is_only_a_marker()
    {
        var waiting = _sim.Run(Menu(), new SimulationScenario([new(SimInputKind.Say, "oi")]));
        Assert.Equal("waiting_for_input", waiting.Outcome);

        var viaFaq = _sim.Run(Menu(), new SimulationScenario([new(SimInputKind.Say, "oi"), new(SimInputKind.Say, "banana"), new(SimInputKind.Say, "banana")]));
        Assert.Equal("handed_off", viaFaq.Outcome);

        var delegated = _sim.Run(
            Graph("q", [new("q", NodeType.Knowledge, new NodeConfig()), new("ia", NodeType.Ai), new("f", NodeType.End)], [E("q", "found", "f"), E("q", "not_found", "ia")]),
            new SimulationScenario([new(SimInputKind.Say, "pergunta")]));
        Assert.Equal("delegated", delegated.Outcome);
        Assert.Contains(delegated.Events, e => e.Kind == "delegated-to-agent");
    }

    [Fact]
    public void The_simulator_is_deterministic()
    {
        var scenario = new SimulationScenario([new(SimInputKind.Say, "oi"), new(SimInputKind.Choose, ChoiceId: "horario")]);
        var a = _sim.Run(Menu(), scenario);
        var b = _sim.Run(Menu(), scenario);
        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Events.Select(e => e.Kind + e.Text), b.Events.Select(e => e.Kind + e.Text));
        Assert.Equal("completed", a.Outcome);
        Assert.Contains(a.Events, e => e.Text == "Estamos abertos até as 18h.");
    }

    [Fact]
    public void The_core_project_has_no_reference_to_network_ai_or_persistence_assemblies()
    {
        var references = typeof(FlowSimulator).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, n => n.StartsWith("System.Net.Http", StringComparison.Ordinal) || n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || n.Contains("OpenAI", StringComparison.OrdinalIgnoreCase));
    }
}
