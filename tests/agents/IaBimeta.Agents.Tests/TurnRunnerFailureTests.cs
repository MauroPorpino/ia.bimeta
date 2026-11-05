using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Agents;

/// <summary>
/// As falhas do <see cref="TurnRunner"/> (T9 e T10, PRO-5): transitória com recuo exponencial, permanente com handoff, capacidade, saída inválida, exceção do gateway, definição ausente e as guardas contra
/// executor que perdeu o turno. Nenhum fallback: o gateway é chamado uma vez por execução.
/// </summary>
public sealed class TurnRunnerFailureTests
{
    private static AiGenerationResult Transitoria(string? code = "http-503", TimeSpan? retryAfter = null) =>
        ScriptedGateway.Failed(AiErrorKind.Transient, retryable: true, code, retryAfter);

    // 7. Falha transitória: recuo exponencial

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(5, 120)] // 160 s, limitado por MaxRetryBackoff (2 min)
    [InlineData(8, 120)]
    public async Task Falha_transitoria_volta_a_Ready_com_recuo_exponencial_limitado_e_gasta_uma_tentativa(int tentativasGastas, int esperaEmSegundos)
    {
        var h = new RunnerHarness(o => o.MaxTurnAttempts = 20).Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => Transitoria();
        var turno = h.SeedRunning(attempts: tentativasGastas);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "ai-http-503"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, tentativasGastas + 1, h.Clock.GetUtcNow() + TimeSpan.FromSeconds(esperaEmSegundos)), (final.State, final.Attempts, final.NextAttemptAt));
        Assert.Single(h.Gateway.Requests); // nenhum fallback, nenhuma segunda chamada
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
    }

    [Theory]
    [InlineData(30, 30)] // a espera que o provedor sugeriu é maior que o recuo (5 s): vale a dele
    [InlineData(600, 120)] // ... mas limitada ao teto do recuo
    [InlineData(2, 5)] // menor que o recuo: vale o recuo
    public async Task A_espera_sugerida_pelo_provedor_vale_quando_e_maior_que_o_recuo_e_e_limitada_ao_teto(int sugeridaEmSegundos, int esperadaEmSegundos)
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => Transitoria(retryAfter: TimeSpan.FromSeconds(sugeridaEmSegundos));
        var turno = h.SeedRunning();

        await h.RunAsync(turno);

        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(esperadaEmSegundos), h.Fresh(turno).NextAttemptAt);
    }

    [Fact]
    public async Task Na_ultima_tentativa_a_falha_transitoria_vira_handoff_e_o_turno_morre()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText(), epoch: 2);
        h.Gateway.Behavior = _ => Transitoria();
        var turno = h.SeedRunning(attempts: h.Options.MaxTurnAttempts - 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "attempts-exhausted-ai-http-503"), resultado);
        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.Id, HandoffReasons.AiProcessingFailed), (pedido.RequestId, pedido.Reason)); // PRO-5
        var aviso = Assert.Single(h.Turns.Registers); // o aviso aprovado do tenant sai antes da transferência
        Assert.Equal((ReplyKind.Informative, Definitions.Default().HandoffNoticeText), (aviso.ReplyKind, aviso.Text));
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Dead, "attempts-exhausted-ai-http-503"), (final.State, final.EndReason));
    }

    // 7. Falha permanente: handoff imediato

    [Theory]
    [InlineData(AiErrorKind.InvalidCredentials, "ai-invalid-credentials")]
    [InlineData(AiErrorKind.QuotaOrBilling, "ai-quota-or-billing")]
    [InlineData(AiErrorKind.InvalidRequest, "ai-invalid-request")]
    [InlineData(AiErrorKind.ContextTooLong, "ai-context-too-long")]
    [InlineData(AiErrorKind.Refused, "ai-refused")]
    [InlineData(AiErrorKind.NotConfigured, "ai-not-configured")]
    [InlineData(AiErrorKind.TenantLimitExceeded, "ai-tenant-limit-exceeded")]
    public async Task Falha_permanente_transfere_ao_atendente_na_hora_sem_retentativa(AiErrorKind tipo, string motivoDoFim)
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        // O código do AiGateway nunca decide o desfecho: vale o tipo (o de limite de período não é o de capacidade).
        h.Gateway.Behavior = _ => ScriptedGateway.Failed(tipo, retryable: false, tipo == AiErrorKind.TenantLimitExceeded ? "period-call-limit" : null);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, motivoDoFim), resultado);
        Assert.Single(h.Gateway.Requests);
        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.Id, HandoffReasons.AiProcessingFailed), (pedido.RequestId, pedido.Reason));
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Dead, motivoDoFim, 0), (final.State, final.EndReason, final.Attempts));
    }

    // 7. Capacidade (limite de chamadas simultâneas do tenant)

    [Fact]
    public async Task Limite_de_simultaneidade_espera_a_capacidade_sem_gastar_tentativa()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => ScriptedGateway.Failed(AiErrorKind.TenantLimitExceeded, retryable: false, AiFailure.ConcurrencyLimitCode);
        var turno = h.SeedRunning(attempts: 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "capacity"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 1, h.Clock.GetUtcNow() + h.Options.CapacityRetry), (final.State, final.Attempts, final.NextAttemptAt));
        Assert.Empty(h.Handoff.Requests);
    }

    [Fact]
    public async Task Esperar_capacidade_alem_da_idade_maxima_do_turno_e_falha_permanente()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => ScriptedGateway.Failed(AiErrorKind.TenantLimitExceeded, retryable: false, AiFailure.ConcurrencyLimitCode);
        var turno = h.SeedRunning();
        h.Clock.Advance(h.Options.MaxTurnAge + TimeSpan.FromSeconds(1));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "capacity-wait-exceeded"), resultado);
        Assert.Equal(HandoffReasons.AiProcessingFailed, Assert.Single(h.Handoff.Requests).Reason);
    }

    // 7. Exceção do gateway e saída inválida

    [Fact]
    public async Task Excecao_do_gateway_e_falha_transitoria_contada_e_o_codigo_so_tem_o_tipo()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => throw new InvalidOperationException("dado sensível: Mauro, exame A");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "exception-InvalidOperationException"), resultado); // a mensagem da exceção nunca vai ao código
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 1), (final.State, final.Attempts));
        Assert.Single(h.Gateway.Requests);
    }

    [Fact]
    public async Task Excecao_na_ultima_tentativa_transfere_ao_atendente()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => throw new TimeoutException();
        var turno = h.SeedRunning(attempts: h.Options.MaxTurnAttempts - 1);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "attempts-exhausted-exception-TimeoutException"), resultado);
        Assert.Equal(HandoffReasons.AiProcessingFailed, Assert.Single(h.Handoff.Requests).Reason);
        Assert.Equal(AiTurnState.Dead, h.Fresh(turno).State);
    }

    [Theory]
    [InlineData("isto não é json", "invalid-json")]
    [InlineData("", "empty-output")]
    [InlineData("{\"action\":\"delete\",\"reply_text\":\"\",\"handoff_reason\":\"none\"}", "unknown-action")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"oi\",\"handoff_reason\":\"none\",\"extra\":\"x\"}", "invalid-shape")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"   \",\"handoff_reason\":\"none\"}", "empty-reply")]
    [InlineData("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"none\"}", "invalid-handoff-reason")]
    public async Task Saida_invalida_nao_executa_nada_e_conta_como_falha_da_tentativa(string saida, string motivoDaInvalidez)
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => ScriptedGateway.Output(saida);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "invalid-output-" + motivoDaInvalidez), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 1), (final.State, final.Attempts));
        Assert.Empty(h.Turns.Registers); // nada é executado
        Assert.Empty(h.Handoff.Requests);
    }

    [Fact]
    public async Task Saida_truncada_conta_como_falha_da_tentativa()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => ScriptedGateway.Output("{\"action\":\"reply\",\"reply_text\":\"Em ger", AiStopReason.Truncated);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "output-truncated"), resultado);
        Assert.Equal((AiTurnState.Ready, 1), (h.Fresh(turno).State, h.Fresh(turno).Attempts));
        Assert.Empty(h.Turns.Registers);
    }

    [Fact]
    public async Task Recusa_do_modelo_e_falha_permanente_e_transfere()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Gateway.Behavior = _ => ScriptedGateway.Output(string.Empty, AiStopReason.Refused);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "model-refused"), resultado);
        Assert.Equal(HandoffReasons.AiProcessingFailed, Assert.Single(h.Handoff.Requests).Reason);
        Assert.Single(h.Gateway.Requests);
    }

    // 9. Sem definição publicada

    [Fact]
    public async Task Sem_definicao_publicada_a_IA_nao_responde_e_transfere_sem_chamar_o_modelo()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        h.Definitions.Published = null;
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Dead, "ai-not-configured"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Registers); // sem definição não há nem aviso aprovado
        var pedido = Assert.Single(h.Handoff.Requests);
        Assert.Equal((turno.Id, HandoffReasons.AiNotConfigured), (pedido.RequestId, pedido.Reason));
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Dead, HandoffReasons.AiNotConfigured), (final.State, final.HandoffReason));
    }

    // 11. Guardas: o executor que não é mais dono do turno não grava nada

    [Fact]
    public async Task Turno_inexistente_e_ignorado()
    {
        var h = new RunnerHarness();

        var resultado = await h.Runner.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Skipped, "not-running"), resultado);
        Assert.Empty(h.Turns.Begins);
    }

    [Fact]
    public async Task Turno_agendado_ou_terminal_e_ignorado_e_nada_e_gravado()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var agendado = AiTurn.Schedule(TestContexts.TenantA, Snapshots.SessionId, h.Clock.GetUtcNow(), h.Clock.GetUtcNow(), h.Options, h.Clock.GetUtcNow());
        h.Store.Seed(agendado);
        var concluido = h.SeedRunning(sessionId: Guid.NewGuid());
        h.Store.Mutate(concluido.Id, t => t.Complete(h.Clock.GetUtcNow(), "replied"));

        var r1 = await h.Runner.RunAsync(agendado.Id, CancellationToken.None);
        var r2 = await h.Runner.RunAsync(concluido.Id, CancellationToken.None);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Skipped, "not-running"), r1);
        Assert.Equal(new TurnRunResult(TurnRunOutcome.Skipped, "not-running"), r2);
        Assert.Empty(h.Turns.Begins);
        Assert.Empty(h.Gateway.Requests);
        Assert.Equal(AiTurnState.Scheduled, h.Store.Get(agendado.Id).State);
        Assert.Equal(0, h.Store.SaveCount);
    }

    [Fact]
    public async Task Executor_que_perdeu_o_turno_para_outro_com_lane_maior_nao_registra_nem_sobrescreve()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning(lane: 7);
        // O lease venceu durante a chamada e outro executor reivindicou o turno com um token de lane maior (o "zumbi" é este).
        h.Gateway.DuringCall = () => h.Store.Mutate(turno.Id, t => t.Claim(99, "outro-executor", h.Clock.GetUtcNow(), h.Options.TurnMaxDuration));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Lost, "state-changed"), resultado);
        Assert.Empty(h.Turns.Registers);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Running, 99L, "outro-executor"), (final.State, final.LaneToken, final.ClaimedBy));
    }

    [Fact]
    public async Task Conflito_de_versao_com_outra_operacao_que_encerra_o_turno_nao_sobrescreve_o_fim()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning();
        // A outra operação (o handler do HandlingModeChangedV1) cancela o turno logo antes de o executor gravar o Completed.
        h.Store.BeforeNextSave = () => h.Store.Mutate(turno.Id, t => t.Cancel(h.Clock.GetUtcNow(), "mode-changed"));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Lost, "state-changed"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Cancelled, "mode-changed"), (final.State, final.EndReason));
        Assert.Equal(0, h.Store.SaveCount); // a gravação do executor foi recusada: nada ficou por cima
    }

    [Fact]
    public async Task Conflito_transitorio_com_o_turno_ainda_do_executor_reaplica_e_conclui()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning();
        // Chegou mensagem com o turno em andamento e sem como interrompê-lo (T12): a versão muda, mas o turno segue Running com a mesma lane.
        h.Store.BeforeNextSave = () => h.Store.Mutate(turno.Id, t => t.MarkInputPending(h.Clock.GetUtcNow()));

        var resultado = await h.RunAsync(turno);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Replied, "replied"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Completed, true), (final.State, final.InputPending));
        Assert.Equal(1, h.Store.SaveCount);
    }

    [Fact]
    public async Task Cancelamento_do_host_durante_a_chamada_devolve_o_turno_a_Ready_sem_gastar_tentativa()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning(attempts: 1);
        using var origem = new CancellationTokenSource();
        h.Gateway.DuringCall = () =>
        {
            origem.Cancel(); // o host encerra (T11)
            origem.Token.ThrowIfCancellationRequested(); // e a chamada honra o cancelamento
        };

        var resultado = await h.RunAsync(turno, origem.Token);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "shutdown"), resultado);
        var final = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, 1, h.Clock.GetUtcNow() + h.Options.CleanShutdownRetry), (final.State, final.Attempts, final.NextAttemptAt));
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
    }
}
