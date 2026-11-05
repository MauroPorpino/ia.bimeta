using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Agents.Testing;

namespace IaBimeta.Agents;

/// <summary>
/// O aviso de transferência (ajuste da C2): ele afirma ao cliente que ele vai ser transferido, e depois do handoff a IA já não pode falar (as cercas do Conversations). Por isso só sai se as regras de
/// roteamento aceitam o pedido AGORA (verificação somente de leitura, <c>CheckOnly</c>); recusado, seja de vez ou por agora, o cliente não recebe uma promessa que não se cumpre.
/// </summary>
public sealed class TurnRunnerNoticeTests
{
    private static RunnerHarness HandoffDecidido(string motivo = "price")
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText("Quanto custa o exame?"), epoch: 4);
        h.Gateway.Behavior = _ => ScriptedGateway.Handoff(motivo);
        return h;
    }

    [Fact]
    public async Task A_ordem_e_verificar_depois_avisar_depois_pedir_de_verdade()
    {
        var h = HandoffDecidido();
        var ordem = new List<string>();
        h.Handoff.CheckBehavior = _ => { ordem.Add("verificacao"); return new RequestHandoffResult(RequestHandoffOutcome.Routable); };
        h.Turns.DuringRegister = _ => ordem.Add("aviso");
        h.Handoff.Behavior = _ => { ordem.Add("pedido"); return new RequestHandoffResult(RequestHandoffOutcome.Completed, Guid.NewGuid(), Guid.NewGuid()); };
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);
        Assert.Equal(new[] { "verificacao", "aviso", "pedido" }, ordem);

        // A verificação leva o mesmo motivo e o mesmo TurnId, e só ela é marcada como somente leitura.
        var verificacao = Assert.Single(h.Handoff.Checks);
        Assert.Equal((turno.SessionId, turno.Id, "ai-decision.price", true), (verificacao.SessionId, verificacao.RequestId, verificacao.Reason, verificacao.CheckOnly));
        Assert.False(Assert.Single(h.Handoff.Requests).CheckOnly);
        Assert.True(h.Fresh(turno).NoticeDone);
    }

    [Theory]
    [InlineData(RequestHandoffOutcome.NotRoutable, false, "no-route")] // nenhuma fila atende: recusa DEFINITIVA
    [InlineData(RequestHandoffOutcome.NotRoutable, false, "queue-does-not-cover-session")]
    [InlineData(RequestHandoffOutcome.NotRoutable, true, "queue-paused")] // recusa por agora
    [InlineData(RequestHandoffOutcome.NotRoutable, true, "queue-closed")]
    [InlineData(RequestHandoffOutcome.Refused, false, "request-id-reused")]
    [InlineData(RequestHandoffOutcome.Refused, true, "concurrent-update")]
    [InlineData(RequestHandoffOutcome.Pending, false, null)]
    [InlineData(RequestHandoffOutcome.AlreadyApplied, false, null)]
    public async Task Verificacao_recusada_nao_envia_aviso_nenhum_e_o_pedido_de_verdade_segue(RequestHandoffOutcome desfecho, bool retentavel, string? codigo)
    {
        var h = HandoffDecidido();
        h.Handoff.CheckBehavior = _ => new RequestHandoffResult(desfecho, null, null, retentavel, codigo);
        h.Handoff.Behavior = _ => new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "no-route");
        var turno = h.SeedRunning();

        await h.RunAsync(turno);

        // O cliente NÃO foi informado de uma transferência que as regras recusam: nenhuma resposta foi registrada e o aviso não ficou como feito.
        Assert.Empty(h.Turns.Registers);
        Assert.False(h.Fresh(turno).NoticeDone);
        Assert.Single(h.Handoff.Checks);
        Assert.Single(h.Handoff.Requests); // o pedido de verdade ainda é feito (a recusa é auditada e classificada por quem decide)
    }

    [Fact]
    public async Task Recusa_definitiva_depois_da_verificacao_deixa_o_turno_Dead_sem_que_o_cliente_tenha_sido_avisado()
    {
        var h = HandoffDecidido("coverage");
        h.Handoff.CheckBehavior = _ => new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "no-route");
        h.Handoff.Behavior = _ => new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "no-route");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal((TurnRunOutcome.Dead, "handoff-refused-no-route"), (resultado.Outcome, resultado.Code));
        Assert.Empty(h.Turns.Registers);
    }

    [Fact]
    public async Task Humano_ja_no_comando_na_verificacao_cancela_o_turno_sem_aviso_e_sem_pedido()
    {
        var h = HandoffDecidido();
        h.Handoff.CheckBehavior = _ => new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "already-handled-by-human");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal((TurnRunOutcome.Cancelled, "handled-by-human"), (resultado.Outcome, resultado.Code));
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
    }

    [Fact]
    public async Task Sessao_encerrada_na_verificacao_cancela_o_turno_sem_aviso_e_sem_pedido()
    {
        var h = HandoffDecidido();
        h.Handoff.CheckBehavior = _ => new RequestHandoffResult(RequestHandoffOutcome.SessionEnded, null, null, false, "session-closed");
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        Assert.Equal((TurnRunOutcome.Cancelled, "session-ended"), (resultado.Outcome, resultado.Code));
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
    }

    [Fact]
    public async Task Sem_aviso_na_definicao_nao_ha_o_que_prometer_e_a_verificacao_nem_e_feita()
    {
        var h = HandoffDecidido();
        h.Definitions.Published = Definitions.Default() with { HandoffNoticeText = null };
        var turno = h.SeedRunning();

        await h.RunAsync(turno);

        Assert.Empty(h.Handoff.Checks);
        Assert.Empty(h.Turns.Registers);
        Assert.Single(h.Handoff.Requests);
    }

    // Retomada: a fila que estava pausada volta, e o aviso sai ANTES do pedido (a retomada verifica de novo)

    [Fact]
    public async Task Fila_pausada_na_primeira_tentativa_sem_aviso_e_na_retomada_com_a_fila_aberta_o_aviso_sai_antes_do_pedido()
    {
        var h = HandoffDecidido("schedule");
        var ordem = new List<string>();
        h.Turns.DuringRegister = _ => ordem.Add("aviso");
        var fila = Guid.NewGuid();

        // Primeira tentativa: a fila está pausada (por agora). Nenhum aviso; o pedido de verdade volta transitório.
        h.Handoff.CheckBehavior = _ => new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, true, "queue-paused");
        h.Handoff.Behavior = _ => new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, true, "queue-paused");
        var turno = h.SeedRunning();
        var primeira = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.Retrying, primeira.Outcome);
        Assert.Empty(h.Turns.Registers);
        var esperando = h.Fresh(turno);
        Assert.Equal((AiTurnState.Ready, false, "ai-decision.schedule"), (esperando.State, esperando.NoticeDone, esperando.HandoffReason));

        // A fila volta: a retomada reivindica o turno, verifica de novo (agora roteável), avisa e só então pede.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Handoff.CheckBehavior = _ => { ordem.Add("verificacao"); return new RequestHandoffResult(RequestHandoffOutcome.Routable); };
        h.Handoff.Behavior = _ => { ordem.Add("pedido"); return new RequestHandoffResult(RequestHandoffOutcome.Completed, Guid.NewGuid(), fila); };
        var reivindicados = await h.Store.ClaimDueAsync("teste", 4, default);
        h.Turns.OnBegin = _ => FakeTurnCommands.Granted(Snapshots.OneText("Vocês abrem sábado?"), epoch: 9);

        var segunda = await h.RunAsync(Assert.Single(reivindicados));

        Assert.Equal(TurnRunOutcome.HandedOff, segunda.Outcome);
        Assert.Equal(new[] { "verificacao", "aviso", "pedido" }, ordem);
        Assert.True(h.Fresh(turno).NoticeDone);
        Assert.Single(h.Turns.Registers);
    }

    [Fact]
    public async Task O_aviso_que_ja_saiu_nunca_se_repete_na_retomada_e_nem_se_verifica_de_novo()
    {
        var h = new RunnerHarness();
        var turno = h.SeedRunning(handoffReason: "ai-decision.price", noticeDone: true);

        var resultado = await h.RunAsync(turno);

        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);
        Assert.Empty(h.Handoff.Checks);
        Assert.Empty(h.Turns.Begins);
        Assert.Empty(h.Turns.Registers);
        Assert.Single(h.Handoff.Requests);
    }

    // Falha permanente do modelo (PRO-5) também respeita a verificação

    [Fact]
    public async Task Falha_permanente_do_modelo_so_avisa_a_transferencia_se_a_fila_aceita_agora()
    {
        var recusada = new RunnerHarness().Grant(Snapshots.OneText());
        recusada.Gateway.Behavior = _ => ScriptedGateway.Failed(IaBimeta.AiGateway.Contracts.AiErrorKind.InvalidCredentials, retryable: false, code: "http-401");
        recusada.Handoff.CheckBehavior = _ => new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "no-route");
        var aceita = new RunnerHarness().Grant(Snapshots.OneText());
        aceita.Gateway.Behavior = _ => ScriptedGateway.Failed(IaBimeta.AiGateway.Contracts.AiErrorKind.InvalidCredentials, retryable: false, code: "http-401");

        await recusada.RunAsync(recusada.SeedRunning());
        await aceita.RunAsync(aceita.SeedRunning());

        Assert.Empty(recusada.Turns.Registers); // não promete o que não vai acontecer
        Assert.Single(aceita.Turns.Registers); // avisa quando a fila aceita
    }

    [Fact]
    public async Task Mensagem_do_cliente_chegada_antes_do_aviso_continua_recusando_o_aviso_defasado_e_a_transferencia_segue()
    {
        var h = HandoffDecidido();
        h.Turns.OnRegister = _ => new RegisterAiReplyResult(RegisterReplyOutcome.StaleInput, null, 0, 2);
        var turno = h.SeedRunning();

        var resultado = await h.RunAsync(turno);

        // O aviso defasado nunca sai, e o pedido de transferência continua (o humano vê a mensagem nova).
        Assert.Equal(TurnRunOutcome.HandedOff, resultado.Outcome);
        Assert.False(h.Fresh(turno).NoticeDone);
        Assert.Single(h.Handoff.Requests);
    }
}
