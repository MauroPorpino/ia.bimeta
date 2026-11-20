using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using static IaBimeta.Workflows.Tests.Samples;

namespace IaBimeta.Workflows.Tests;

public sealed class InterpreterTests
{
    private readonly FlowInterpreter _i = new();

    [Fact]
    public void Start_runs_messages_until_the_first_question_and_waits()
    {
        var r = _i.Start(Menu(), T0, new InboundInput("oi"));

        Assert.Equal(FlowStatus.WaitingInput, r.State.Status);
        Assert.Equal("menu", r.State.NodeId);
        Assert.Equal(["hello", "menu"], r.Visited);
        Assert.Equal(["Olá! Sou o assistente.", "Escolha: 1) Preço 2) Horário 3) Falar com atendente"], r.Effects.OfType<SendMessageEffect>().Select(m => m.Text));
        Assert.Equal(3, r.Effects.OfType<SendMessageEffect>().Last().Choices.Count);
        Assert.Equal("oi", r.State.Variables[FlowNames.InputText]);
    }

    [Fact]
    public void Same_state_and_input_always_give_the_same_result()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        var a = _i.Advance(g, s, new InboundInput(ChoiceId: "preco"), T0.AddMinutes(1));
        var b = _i.Advance(g, s, new InboundInput(ChoiceId: "preco"), T0.AddMinutes(1));

        Assert.Equal(a.State.Status, b.State.Status);
        Assert.Equal(a.State.NodeId, b.State.NodeId);
        Assert.Equal(a.Effects.Select(e => e.Key), b.Effects.Select(e => e.Key));
        Assert.Equal(a.Visited, b.Visited);
    }

    [Theory]
    [InlineData("2", "horario")]
    [InlineData("Horário", "horario")]
    [InlineData("horario", "horario")]
    [InlineData("PREÇO", "preco")]
    public void Choice_answers_accept_number_label_or_id_ignoring_case_and_accents(string typed, string expected)
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        var r = _i.Advance(g, s, new InboundInput(Text: typed), T0);

        Assert.Equal(expected, r.State.Variables["opcao"]);
    }

    [Fact]
    public void Invalid_answer_repeats_the_question_then_leaves_by_the_invalid_port()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;

        var first = _i.Advance(g, s, new InboundInput(Text: "talvez"), T0);
        Assert.Equal(FlowStatus.WaitingInput, first.State.Status);
        Assert.Equal(1, first.State.Attempts);
        Assert.StartsWith("Não entendi.", first.Effects.OfType<SendMessageEffect>().Single().Text);

        var second = _i.Advance(g, first.State, new InboundInput(Text: "sei lá"), T0);
        Assert.Equal(FlowStatus.HandedOff, second.State.Status);
        Assert.Contains(second.Effects, e => e is RequestHandoffEffect { ReasonCode: "customer_request" });
    }

    [Fact]
    public void Price_path_calls_the_operation_then_shows_the_mapped_result()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        s = _i.Advance(g, s, new InboundInput(ChoiceId: "preco"), T0).State;
        Assert.Equal("exame", s.NodeId);
        Assert.Equal(T0.AddSeconds(600), s.WaitUntil);

        var call = _i.Advance(g, s, new InboundInput(Text: "HEM-01"), T0);
        var effect = Assert.Single(call.Effects.OfType<CallCapabilityEffect>());
        Assert.Equal("exam.price", effect.OperationKey);
        Assert.Equal("HEM-01", effect.Parameters["code"]);
        Assert.Equal(FlowStatus.AwaitingEffect, call.State.Status);

        var done = _i.Advance(g, call.State, new CapabilityResultInput(effect.Key, true, new Dictionary<string, string> { ["valor"] = "12,50" }), T0);
        Assert.Equal(FlowStatus.Completed, done.State.Status);
        Assert.Equal("O exame HEM-01 custa R$ 12,50.", done.Effects.OfType<SendMessageEffect>().First().Text);
        Assert.Equal("Até logo!", done.Effects.OfType<SendMessageEffect>().Last().Text);
        Assert.IsType<FinishEffect>(done.Effects.Last());
    }

    [Fact]
    public void Integration_failure_takes_the_failure_port_and_records_only_the_code()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        s = _i.Advance(g, s, new InboundInput(ChoiceId: "preco"), T0).State;
        var call = _i.Advance(g, s, new InboundInput(Text: "X"), T0);
        var key = call.Effects.OfType<CallCapabilityEffect>().Single().Key;

        var r = _i.Advance(g, call.State, new CapabilityResultInput(key, false, ErrorCode: "timeout"), T0);

        Assert.Equal(FlowStatus.HandedOff, r.State.Status);
        Assert.Equal("timeout", r.State.Variables["preco.error"]);
        Assert.Contains("Não consegui consultar agora.", r.Effects.OfType<SendMessageEffect>().Select(m => m.Text));
    }

    [Fact]
    public void Question_timeout_takes_the_timeout_port_only_after_the_deadline()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        s = _i.Advance(g, s, new InboundInput(ChoiceId: "preco"), T0).State;

        var early = _i.Advance(g, s, new TimeoutInput(), T0.AddSeconds(599));
        Assert.True(early.Ignored);

        var late = _i.Advance(g, s, new TimeoutInput(), T0.AddSeconds(600));
        Assert.Equal(FlowStatus.Completed, late.State.Status);
    }

    [Fact]
    public void Knowledge_found_stores_the_answer_and_not_found_hands_the_session_to_the_agent()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        var ask = _i.Advance(g, s, new InboundInput(Text: "qual o endereço?", ChoiceId: null), T0);
        // Texto que não é uma opção: inválido; use uma opção que cai no else da condição.
        Assert.Equal(1, ask.State.Attempts);

        var fl = Graph("q", [new("q", NodeType.Knowledge, new NodeConfig()), Msg("r", "{{knowledge.answer}}"), new("ia", NodeType.Ai), new("end", NodeType.End)], [E("q", "found", "r"), E("q", "not_found", "ia"), E("r", "next", "end")]);
        var start = _i.Start(fl, T0, new InboundInput("Qual o endereço?"));
        var lookup = Assert.Single(start.Effects.OfType<LookupKnowledgeEffect>());
        Assert.Equal("Qual o endereço?", lookup.Query);

        var found = _i.Advance(fl, start.State, new KnowledgeResultInput(lookup.Key, true, "Rua A, 10."), T0);
        Assert.Equal("Rua A, 10.", found.Effects.OfType<SendMessageEffect>().Single().Text);
        Assert.Equal(FlowStatus.Completed, found.State.Status);

        var miss = _i.Advance(fl, start.State, new KnowledgeResultInput(lookup.Key, false), T0);
        Assert.Equal(FlowStatus.Delegated, miss.State.Status);
        Assert.IsType<DelegateToAgentEffect>(miss.Effects.Single());
    }

    [Fact]
    public void Duplicate_late_or_foreign_inputs_are_ignored_without_changing_state()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        s = _i.Advance(g, s, new InboundInput(ChoiceId: "preco"), T0).State;
        var call = _i.Advance(g, s, new InboundInput(Text: "X"), T0);
        var key = call.Effects.OfType<CallCapabilityEffect>().Single().Key;

        var wrongKey = _i.Advance(g, call.State, new CapabilityResultInput("99:0", true), T0);
        Assert.True(wrongKey.Ignored);
        Assert.Same(call.State, wrongKey.State);

        var done = _i.Advance(g, call.State, new CapabilityResultInput(key, true, new Dictionary<string, string> { ["valor"] = "1" }), T0);
        var duplicate = _i.Advance(g, done.State, new CapabilityResultInput(key, true), T0);
        Assert.True(duplicate.Ignored);
        Assert.Empty(duplicate.Effects);

        var inboundWhileAwaiting = _i.Advance(g, call.State, new InboundInput(Text: "oi?"), T0);
        Assert.True(inboundWhileAwaiting.Ignored);
    }

    [Fact]
    public void Effect_keys_are_unique_per_step_and_index()
    {
        var g = Menu();
        var r = _i.Start(g, T0);
        Assert.Equal(["1:0", "1:1"], r.Effects.Select(e => e.Key));
        var next = _i.Advance(g, r.State, new InboundInput(ChoiceId: "preco"), T0);
        Assert.All(next.Effects, e => Assert.StartsWith("2:", e.Key));
    }

    [Fact]
    public void Templates_are_rendered_in_one_pass_and_never_recursively()
    {
        var vars = new Dictionary<string, string> { ["nome"] = "{{segredo}}", ["segredo"] = "X" };
        Assert.Equal("Oi {{segredo}}!", TemplateRenderer.Render("Oi {{nome}}!", vars));
        Assert.Equal("Oi !", TemplateRenderer.Render("Oi {{inexistente}}!", vars));
    }

    [Fact]
    public void A_runaway_loop_without_waits_stops_at_the_step_limit()
    {
        var g = Graph("a", [Msg("a", "x"), Msg("b", "y")], [E("a", "next", "b"), E("b", "next", "a")]);
        var r = new FlowInterpreter(new FlowLimits(MaxStepsPerRun: 10)).Start(g, T0);

        Assert.Equal(FlowStatus.Failed, r.State.Status);
        Assert.Equal("step-limit", r.State.FailureCode);
    }

    [Fact]
    public void Typed_answers_are_validated_and_normalised()
    {
        FlowLimits l = new();
        string? P(AnswerType t, string text, string? pattern = null) => AnswerParser.Parse(new NodeConfig(AnswerType: t, Pattern: pattern), new InboundInput(text), l);

        Assert.Equal("12.5", P(AnswerType.Number, "12,5"));
        Assert.Null(P(AnswerType.Number, "doze"));
        Assert.Equal("a@b.com", P(AnswerType.Email, " A@B.com "));
        Assert.Null(P(AnswerType.Email, "a@b"));
        Assert.Equal("+5511987654321", P(AnswerType.Phone, "+55 (11) 98765-4321"));
        Assert.Null(P(AnswerType.Phone, "123"));
        Assert.Equal("2026-03-09", P(AnswerType.Date, "09/03/2026"));
        Assert.Null(P(AnswerType.Date, "31/02/2026"));
        Assert.Equal("ABC123", P(AnswerType.Pattern, "ABC123", "^[A-Z]{3}[0-9]{3}$"));
        Assert.Null(P(AnswerType.Pattern, "abc", "^[A-Z]{3}[0-9]{3}$"));
        Assert.Null(AnswerParser.Parse(new NodeConfig(), new InboundInput(MediaKind: "image"), l));
    }

    [Fact]
    public void Conditions_use_only_the_fixed_declarative_operators()
    {
        var vars = new Dictionary<string, string> { ["n"] = "10", ["t"] = "Raio X de Tórax" };
        Assert.True(ConditionEvaluator.One(new FlowCondition("n", ConditionOperator.GreaterThan, "9"), vars, T0));
        Assert.False(ConditionEvaluator.One(new FlowCondition("n", ConditionOperator.LessThan, "9"), vars, T0));
        Assert.True(ConditionEvaluator.One(new FlowCondition("t", ConditionOperator.Contains, "torax"), vars, T0));
        Assert.True(ConditionEvaluator.One(new FlowCondition("t", ConditionOperator.In, Values: ["raio x de torax", "outro"]), vars, T0));
        Assert.True(ConditionEvaluator.One(new FlowCondition("zzz", ConditionOperator.IsEmpty), vars, T0));
        // 2026-01-05 é segunda-feira; 12:00 UTC está dentro de 08:00–18:00; 20:00 não.
        var schedule = new BusinessSchedule("UTC", [new BusinessWindow([1, 2, 3, 4, 5], "08:00", "18:00")]);
        Assert.True(ConditionEvaluator.One(new FlowCondition(Op: ConditionOperator.WithinBusinessHours, Schedule: schedule), vars, T0));
        Assert.False(ConditionEvaluator.One(new FlowCondition(Op: ConditionOperator.WithinBusinessHours, Schedule: schedule), vars, T0.AddHours(8)));
        Assert.False(ConditionEvaluator.One(new FlowCondition(Op: ConditionOperator.WithinBusinessHours, Schedule: schedule), vars, T0.AddDays(5)));
    }

    [Fact]
    public void Cancel_is_idempotent_and_terminal_states_ignore_everything()
    {
        var g = Menu();
        var s = _i.Start(g, T0).State;
        var c = _i.Cancel(s, "human-takeover");
        Assert.Equal(FlowStatus.Cancelled, c.State.Status);
        Assert.True(_i.Cancel(c.State, "again").Ignored);
        Assert.True(_i.Advance(g, c.State, new InboundInput("oi"), T0).Ignored);
    }
}

/// <summary>E2.5 (ADR 0041, regra 3): o marcador EXPLÍCITO de pergunta de texto livre, que o Conversations lê na decisão de saída do cliente. Vem do tipo de resposta do nó, nunca de texto.</summary>
public sealed class FreeTextMarkerTests
{
    private readonly FlowInterpreter _i = new();

    [Fact]
    public void Mensagem_simples_e_pergunta_de_escolha_nao_esperam_texto_livre_e_pergunta_de_texto_espera()
    {
        var g = Samples.Menu();
        var inicio = _i.Start(g, Samples.T0, new InboundInput("oi"));

        // "Olá!" é uma mensagem; "Escolha..." é pergunta de ESCOLHA: nenhuma espera texto livre.
        Assert.All(inicio.Effects.OfType<SendMessageEffect>(), m => Assert.False(m.ExpectsFreeText));

        // O próximo nó pergunta o código do exame (resposta de texto): termina esperando TEXTO LIVRE.
        var proximo = _i.Advance(g, inicio.State, new InboundInput(ChoiceId: "preco"), Samples.T0);
        Assert.True(Assert.Single(proximo.Effects.OfType<SendMessageEffect>()).ExpectsFreeText);
    }

    [Fact]
    public void Repetir_a_pergunta_de_texto_depois_de_resposta_invalida_mantem_o_marcador_e_a_de_escolha_nao()
    {
        var g = Samples.Menu();
        var escolha = _i.Start(g, Samples.T0).State;
        var repetidaEscolha = _i.Advance(g, escolha, new InboundInput(Text: "talvez"), Samples.T0);
        Assert.False(Assert.Single(repetidaEscolha.Effects.OfType<SendMessageEffect>()).ExpectsFreeText);

        var texto = _i.Advance(g, escolha, new InboundInput(ChoiceId: "preco"), Samples.T0).State;
        var repetidaTexto = _i.Advance(g, texto, new InboundInput(Text: "   "), Samples.T0);
        Assert.Contains(repetidaTexto.Effects.OfType<SendMessageEffect>(), m => m.ExpectsFreeText);
    }
}
