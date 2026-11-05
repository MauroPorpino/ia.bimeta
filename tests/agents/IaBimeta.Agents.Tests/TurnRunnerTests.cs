using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents;

/// <summary>
/// O <see cref="TurnRunner"/> sobre os dublês (sem banco): o caminho feliz, a decisão de handoff, cada recusa de <c>RegisterAiReply</c>, o humano que assume com a chamada em voo, o <c>BeginTurn</c> recusado, o
/// grupo sem pendentes e o isolamento entre sessões (Mauro e Pedro). Nada aqui chama um provedor de verdade.
/// </summary>
public sealed class TurnRunnerTests
{
    public static IEnumerable<object[]> MotivosDeHandoff => HandoffReasons.Selectable.Select(m => new object[] { m });

    // 1. Resposta feliz

    [Fact]
    public async Task Resposta_feliz_registra_com_o_grant_do_Conversations_e_completa_o_turno()
    {
        var h = new RunnerHarness();
        var snapshot = Snapshots.OneText("Preciso de jejum para o exame?");
        h.Grant(snapshot, epoch: 5);
        AiGenerationResult? chamada = null;
        h.Gateway.Behavior = _ => chamada = ScriptedGateway.Reply("Em geral não precisa de jejum.");
        var turno = h.SeedRunning(lane: 7);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Replied, "replied"), resultado);

        var begin = Assert.Single(h.Turns.Begins);
        Assert.Equal((turno.SessionId, turno.Id, 7L, h.Options.MaxInputMessages), (begin.SessionId, begin.TurnId, begin.LeaseToken, begin.MaxSnapshotMessages));

        var registro = Assert.Single(h.Turns.Registers);
        Assert.Equal(
            (turno.SessionId, turno.Id, 5L, snapshot.StateVersion, snapshot.LastInboundSeq, ReplyKind.Generated, snapshot.ConversationId, "Em geral não precisa de jejum."),
            (registro.SessionId, registro.TurnId, registro.Epoch, registro.ExpectedStateVersion, registro.BasedOnInboundSeq, registro.ReplyKind, registro.TargetConversationId, registro.Text));
        Assert.Equal(new[] { chamada!.AiRequestId }, registro.AiRequestIds);

        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, "replied"), (final.State, final.EndReason));
        Assert.Empty(h.Turns.Ends); // a resposta aceita já libera o turno ativo no Conversations
        Assert.Empty(h.Handoff.Requests);
    }

    [Fact]
    public async Task O_pedido_ao_modelo_e_estruturado_correlacionado_pelo_turno_e_termina_numa_mensagem_do_cliente()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText("Qual o horário de coleta?"));
        var turno = h.SeedRunning();

        await h.RunAsync(turno);

        var pedido = Assert.Single(h.Gateway.Requests);
        Assert.Equal(AiOutputMode.StructuredJson, pedido.OutputMode);
        Assert.Same(AgentPrompt.DecisionSchema, pedido.StructuredOutput);
        Assert.Equal(turno.Id.ToString(), pedido.CorrelationId);
        Assert.Equal(AiMessageRole.User, pedido.Messages[^1].Role);
        Assert.Equal("Qual o horário de coleta?", pedido.Messages[^1].Text);
        Assert.Equal((h.Options.MaxOutputTokens, h.Options.ModelCallTimeout), (pedido.MaxOutputTokens, pedido.Timeout));
        Assert.Contains(AgentPrompt.BaseLayer, pedido.SystemInstruction);
        Assert.Contains("segunda a sexta", pedido.SystemInstruction); // as instruções PUBLICADAS do tenant
    }

    // 2. Decisão de handoff

    [Theory]
    [MemberData(nameof(MotivosDeHandoff))]
    public async Task Decisao_de_handoff_registra_o_aviso_antes_e_pede_o_atendente_pelo_TurnId(string motivo)
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText("Quanto custa o exame?"), epoch: 4);
        h.Gateway.Behavior = _ => ScriptedGateway.Handoff(motivo);
        var ordem = new List<string>();
        h.Turns.DuringRegister = _ => ordem.Add("aviso");
        h.Handoff.Behavior = _ =>
        {
            ordem.Add("handoff");
            return new RequestHandoffResult(RequestHandoffOutcome.Completed, Guid.NewGuid(), Guid.NewGuid());
        };
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.HandedOff, "handoff-" + motivo), resultado);
        Assert.Equal(new[] { "aviso", "handoff" }, ordem); // depois do handoff a IA já não responde: o aviso sai ANTES

        var aviso = Assert.Single(h.Turns.Registers);
        Assert.Equal((ReplyKind.Informative, Definitions.Default().HandoffNoticeText, 4L), (aviso.ReplyKind, aviso.Text, aviso.Epoch));
        Assert.Empty(aviso.AiRequestIds);

        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.SessionId, turno.Id, "ai-decision." + motivo), (pedido.SessionId, pedido.RequestId, pedido.Reason));

        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, "handoff:handoff-" + motivo, "ai-decision." + motivo, true), (final.State, final.EndReason, final.HandoffReason, final.NoticeDone));
    }

    [Fact]
    public async Task Decisao_de_handoff_sem_aviso_na_definicao_nao_registra_nenhuma_resposta()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText("Quero falar com uma pessoa."));
        h.Definitions.Published = Definitions.Default() with { HandoffNoticeText = null };
        h.Gateway.Behavior = _ => ScriptedGateway.Handoff(HandoffReasons.CustomerRequest);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);
        Assert.Empty(h.Turns.Registers);
        Assert.Equal("ai-decision.customer_request", Assert.Single(h.Handoff.Requests).Reason);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, false), (final.State, final.NoticeDone));
    }

    // 3. As recusas do RegisterAiReply

    [Theory]
    [InlineData(RegisterReplyOutcome.StaleInput, "stale-input")]
    [InlineData(RegisterReplyOutcome.StaleState, "stale-state")]
    public async Task Resposta_defasada_e_descartada_e_o_sucessor_cobre_as_pendentes(RegisterReplyOutcome recusa, string codigo)
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText(), epoch: 3);
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(recusa, null, 0, 2);
        var turno = h.SeedRunning(supersedeCount: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, codigo), resultado);
        var fim = Assert.Single(h.Turns.Ends);
        Assert.Equal((TurnEndReason.Discarded, 3L), (fim.Reason, fim.Epoch));

        var turnos = h.Store.OfSession(turno.SessionId);
        Assert.Equal(2, turnos.Count);
        var antigo = turnos.Single(t => t.Id == turno.Id);
        Assert.Equal((AiTurnState.Discarded, codigo), (antigo.State, antigo.EndReason));

        // T7: cada descarte exige entrada ou mudança nova; o sucessor nasce devido já e herda a rajada (+1).
        var sucessor = turnos.Single(t => t.Id != turno.Id);
        Assert.Equal((AiTurnState.Scheduled, h.Clock.GetUtcNow(), 2), (sucessor.State, sucessor.FireAt, sucessor.SupersedeCount));
        Assert.True(sucessor.IsDue(h.Clock.GetUtcNow()));
        Assert.Empty(h.Handoff.Requests);
    }

    [Theory]
    [InlineData(RegisterReplyOutcome.TurnNotActive, "turn-not-active")]
    [InlineData(RegisterReplyOutcome.NotAiResponder, "not-ai-responder")]
    public async Task Resposta_de_turno_substituido_ou_com_humano_no_comando_e_descartada_sem_sucessor(RegisterReplyOutcome recusa, string codigo)
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(recusa, null, 0, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, codigo), resultado); // T7b: quem substituiu ou cancelou cuida do que vem depois
        Assert.Single(h.Store.OfSession(turno.SessionId));
        Assert.Equal(AiTurnState.Discarded, h.Fresh(turno).State);
        Assert.Empty(h.Turns.Ends);
        Assert.Empty(h.Handoff.Requests);
    }

    [Fact]
    public async Task Sessao_inexistente_no_registro_cancela_o_turno()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.SessionNotFound, null, null, null);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Cancelled, "session-not-found"), resultado);
        Assert.Equal(AiTurnState.Cancelled, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Forma_nao_aceita_e_defeito_e_transfere_ao_atendente_e_o_turno_morre()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Definitions.Published = Definitions.Default() with { HandoffNoticeText = null };
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.ShapeNotAllowed, null, 0, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "reply-shape-not-allowed"), resultado);
        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.Id, HandoffReasons.AiProcessingFailed), (pedido.RequestId, pedido.Reason));
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Dead, "reply-shape-not-allowed"), (final.State, final.EndReason));
    }

    [Fact]
    public async Task Resposta_ja_registrada_completa_o_turno_sem_sucessor_quando_tudo_esta_coberto()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.AlreadyRegistered, Guid.NewGuid(), 1, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Replied, "replied"), resultado);
        Assert.Single(h.Store.OfSession(turno.SessionId));
        Assert.Equal(AiTurnState.Completed, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Resposta_ja_registrada_com_mensagem_posterior_a_cobertura_cria_o_sucessor()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.AlreadyRegistered, Guid.NewGuid(), 1, 2); // TRN-1: o processo caiu depois de aceita
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        var turnos = h.Store.OfSession(turno.SessionId);
        Assert.Equal(2, turnos.Count);
        var sucessor = turnos.Single(t => t.Id != turno.Id);
        Assert.Equal((AiTurnState.Scheduled, h.Clock.GetUtcNow(), 0), (sucessor.State, sucessor.FireAt, sucessor.SupersedeCount));
    }

    // 4. O humano assume (ou o turno é substituído) com a chamada em voo

    [Fact]
    public async Task Humano_que_assume_durante_a_chamada_nao_recebe_resposta_da_IA()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning();
        h.Gateway.DuringCall = () => h.Store.Mutate(turno.Id, t => t.Cancel(h.Clock.GetUtcNow(), "mode-changed")); // o handler do HandlingModeChangedV1 cancela o turno

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Lost, "state-changed"), resultado);
        Assert.Empty(h.Turns.Registers); // nenhuma resposta sai depois de o humano assumir
        Assert.Empty(h.Handoff.Requests);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Cancelled, "mode-changed"), (final.State, final.EndReason)); // o executor não sobrescreve o fim
    }

    [Fact]
    public async Task Humano_que_assume_entre_a_chamada_e_o_registro_e_barrado_pelo_Conversations_e_o_executor_sai_sem_gravar_por_cima()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning();
        h.Turns.DuringRegister = _ => h.Store.Mutate(turno.Id, t => t.Cancel(h.Clock.GetUtcNow(), "mode-changed"));
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.TurnNotActive, null, 0, 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.Lost, resultado.Outcome);
        Assert.Equal((AiTurnState.Cancelled, "mode-changed"), (h.Fresh(turno).State, h.Fresh(turno).EndReason));
    }

    [Fact]
    public async Task Conversations_que_responde_NotAiResponder_descarta_a_resposta()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.NotAiResponder, null, 0, 1);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, "not-ai-responder"), resultado);
        Assert.Single(h.Turns.Registers); // a tentativa existiu, e foi o Conversations quem a recusou
    }

    [Fact]
    public async Task Turno_substituido_durante_a_chamada_nao_registra_e_libera_o_turno_ativo()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText(), epoch: 6);
        var turno = h.SeedRunning();
        h.Gateway.DuringCall = () =>
        {
            AiTurn? sucessor = null;
            h.Store.Mutate(turno.Id, t => sucessor = t.Supersede(h.Clock.GetUtcNow(), h.Options, h.Clock.GetUtcNow())); // T6: mensagem nova
            h.Store.Seed(sucessor!);
        };

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Lost, "state-changed"), resultado);
        Assert.Empty(h.Turns.Registers);
        var fim = Assert.Single(h.Turns.Ends);
        Assert.Equal((TurnEndReason.Superseded, 6L), (fim.Reason, fim.Epoch));
        Assert.Equal(AiTurnState.Superseded, h.Fresh(turno).State);
    }

    // 5. BeginTurn recusado

    [Theory]
    [InlineData(BeginTurnOutcome.SessionNotFound, "session-not-found")]
    [InlineData(BeginTurnOutcome.SessionClosed, "session-closed")]
    [InlineData(BeginTurnOutcome.NotAiResponder, "not-ai-responder")]
    public async Task BeginTurn_recusado_cancela_o_turno_sem_chamar_o_modelo(BeginTurnOutcome recusa, string codigo)
    {
        var h = new RunnerHarness();
        h.Turns.OnBegin = _ => new BeginTurnResult(recusa, null, null);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Cancelled, codigo), resultado); // T4b: sem sucessor
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
        Assert.Single(h.Store.OfSession(turno.SessionId));
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Cancelled, codigo), (final.State, final.EndReason));
    }

    [Fact]
    public async Task BeginTurn_com_token_de_lane_antigo_devolve_o_turno_a_Ready_sem_gastar_tentativa()
    {
        var h = new RunnerHarness();
        h.Turns.OnBegin = _ => new BeginTurnResult(BeginTurnOutcome.LeaseTokenStale, null, null);
        var turno = h.SeedRunning(attempts: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "lease-token-stale"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 1, h.Clock.GetUtcNow() + h.Options.CleanShutdownRetry), (final.State, final.Attempts, final.NextAttemptAt));
        Assert.Empty(h.Gateway.Requests);
    }

    // 6. Nada pendente

    [Fact]
    public async Task Snapshot_sem_pendentes_descarta_o_turno_sem_chamar_o_modelo_e_libera_o_turno_ativo()
    {
        var snapshot = Snapshots.Snapshot(new[] { Snapshots.Customer(1, "Oi", pending: false), Snapshots.Outbound(2, "Olá!") }, lastInbound: 1, answered: 1);
        var h = new RunnerHarness().Grant(snapshot, epoch: 2);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Discarded, "nothing-pending"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Registers);
        var fim = Assert.Single(h.Turns.Ends);
        Assert.Equal((TurnEndReason.Discarded, 2L), (fim.Reason, fim.Epoch));
        Assert.Equal(AiTurnState.Discarded, h.Fresh(turno).State);
    }

    // 12. Isolamento: Mauro (#1001) e Pedro (#1002)

    [Fact]
    public async Task O_pedido_de_cada_turno_so_tem_o_contexto_da_propria_sessao_e_a_resposta_vai_so_para_ela()
    {
        var sessaoMauro = Guid.Parse("11111111-0000-0000-0000-000000001001");
        var conversaMauro = Guid.Parse("22222222-0000-0000-0000-000000001001");
        var sessaoPedro = Guid.Parse("11111111-0000-0000-0000-000000001002");
        var conversaPedro = Guid.Parse("22222222-0000-0000-0000-000000001002");

        var mauro = Snapshots.Snapshot(
                new[]
                {
                    Snapshots.Customer(1, "Qual o preparo do exame A?", pending: false),
                    Snapshots.Outbound(2, "O exame A pede 8 horas de jejum."),
                    Snapshots.Customer(3, "Preciso ficar em jejum para esse exame?"),
                },
                lastInbound: 3,
                answered: 1) with
        { SessionId = sessaoMauro, ConversationId = conversaMauro };
        var pedro = Snapshots.Snapshot(new[] { Snapshots.Customer(1, "Qual o preparo do exame B?") }, lastInbound: 1) with
        { SessionId = sessaoPedro, ConversationId = conversaPedro };

        var h = new RunnerHarness();
        h.Turns.OnBegin = c => FakeTurnCommands.Granted(c.SessionId == sessaoMauro ? mauro : pedro);
        var turnoMauro = h.SeedRunning(sessionId: sessaoMauro, lane: 1);
        var turnoPedro = h.SeedRunning(sessionId: sessaoPedro, lane: 2);

        await h.RunAsync(turnoMauro);
        await h.RunAsync(turnoPedro);

        string Contexto(AiTurn t)
        {
            var pedido = h.Gateway.Requests.Single(r => r.CorrelationId == t.Id.ToString());
            return pedido.SystemInstruction + "\n" + string.Join("\n", pedido.Messages.Select(m => m.Text));
        }

        var deMauro = Contexto(turnoMauro);
        Assert.Contains("exame A", deMauro);
        Assert.Contains("Preciso ficar em jejum para esse exame?", deMauro);
        Assert.DoesNotContain("exame B", deMauro);

        var dePedro = Contexto(turnoPedro);
        Assert.Contains("exame B", dePedro);
        Assert.DoesNotContain("exame A", dePedro);

        // O histórico de Pedro (as mensagens do pedido) não tem nada de Mauro. A palavra "jejum" também está no FAQ publicado do tenant (instruções, não conversa), por isso só as mensagens contam.
        var mensagensDePedro = string.Join("\n", h.Gateway.Requests.Single(r => r.CorrelationId == turnoPedro.Id.ToString()).Messages.Select(m => m.Text));
        Assert.DoesNotContain("jejum", mensagensDePedro);
        Assert.Equal("Qual o preparo do exame B?", mensagensDePedro);

        Assert.Equal(2, h.Turns.Registers.Count);
        var respostaMauro = Assert.Single(h.Turns.Registers, r => r.TurnId == turnoMauro.Id);
        var respostaPedro = Assert.Single(h.Turns.Registers, r => r.TurnId == turnoPedro.Id);
        Assert.Equal((sessaoMauro, conversaMauro, 3L), (respostaMauro.SessionId, respostaMauro.TargetConversationId, respostaMauro.BasedOnInboundSeq));
        Assert.Equal((sessaoPedro, conversaPedro, 1L), (respostaPedro.SessionId, respostaPedro.TargetConversationId, respostaPedro.BasedOnInboundSeq));
    }
}
