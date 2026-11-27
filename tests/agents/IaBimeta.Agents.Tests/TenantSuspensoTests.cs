using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents;

/// <summary>
/// Suspensão efetiva da empresa (Etapa 02, ADR 0043) no Agents: o <see cref="TurnRunner"/> não chama o modelo nem o Conversations com a empresa suspensa, devolve o turno à fila sem gastar tentativa e cancela o turno
/// que nasceu antes da reativação; o <see cref="TurnScheduler"/> não cria turno. Sem a porta, tudo continua como antes.
/// </summary>
public sealed class TenantSuspensoTests
{
    private sealed class PortaFalsa : ITenantOperationGate
    {
        public TenantOperationState Estado { get; set; } = TenantOperationState.Operational;

        public List<TenantId> Consultas { get; } = new();

        public Task<TenantOperationState> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
        {
            Consultas.Add(tenantId);
            return Task.FromResult(Estado);
        }
    }

    private static TurnRunner ComPorta(RunnerHarness h, ITenantOperationGate? porta) =>
        new(h.Store, h.Turns, h.Gateway, h.Handoff, h.Definitions, h.Gaps, h.Options, h.Clock, operationGate: porta);

    // TurnRunner

    [Fact]
    public async Task Runner_com_empresa_suspensa_devolve_o_turno_a_fila_sem_gastar_tentativa_e_sem_chamar_gateway_nem_conversations()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText("Preciso de jejum?"));
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var runner = ComPorta(h, porta);
        var turno = h.SeedRunning();
        var tentativasAntes = h.Fresh(turno).Attempts;

        var resultado = await runner.RunAsync(turno.Id, default);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Retrying, "tenant-suspended"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Begins);
        Assert.Empty(h.Turns.Registers);
        Assert.Empty(h.Handoff.Requests);
        var final = h.Fresh(turno);
        Assert.Equal(AiTurnState.Ready, final.State);
        Assert.Equal(tentativasAntes, final.Attempts);
        Assert.Equal(new[] { turno.TenantId }, porta.Consultas);
    }

    [Fact]
    public async Task Runner_com_empresa_suspensa_repete_o_adiamento_quantas_vezes_for_preciso_sem_esgotar_tentativas()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var runner = ComPorta(h, new PortaFalsa { Estado = new TenantOperationState(false, null) });
        var turno = h.SeedRunning();
        var tentativas = h.Fresh(turno).Attempts;

        for (var i = 0; i < 5; i++)
        {
            var corrente = h.Fresh(turno);
            if (corrente.State != AiTurnState.Running)
            {
                corrente.Claim(7, "teste", h.Clock.GetUtcNow(), h.Options.TurnMaxDuration);
                h.Store.Seed(corrente);
            }

            var r = await runner.RunAsync(turno.Id, default);
            Assert.Equal(TurnRunOutcome.Retrying, r.Outcome);
        }

        Assert.NotEqual(AiTurnState.Dead, h.Fresh(turno).State);
        Assert.Empty(h.Gateway.Requests);
        Assert.True(h.Fresh(turno).Attempts - tentativas <= 5); // as reivindicações do próprio teste contam; o adiamento em si não gasta
    }

    [Fact]
    public async Task Runner_turno_anterior_a_reativacao_e_cancelado_sem_reprocessar()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning(); // última mensagem recebida "agora"
        var reativadaDepois = h.Clock.GetUtcNow().AddMinutes(10);
        var runner = ComPorta(h, new PortaFalsa { Estado = new TenantOperationState(true, reativadaDepois) });

        var resultado = await runner.RunAsync(turno.Id, default);

        Assert.Equal(new TurnRunResult(TurnRunOutcome.Cancelled, "tenant-reactivated-stale"), resultado);
        Assert.Empty(h.Gateway.Requests);
        Assert.Empty(h.Turns.Begins);
        Assert.Equal(AiTurnState.Cancelled, h.Fresh(turno).State);
    }

    [Fact]
    public async Task Runner_turno_posterior_a_reativacao_executa_normalmente()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText("Qual o horário?"));
        var reativadaAntes = h.Clock.GetUtcNow().AddMinutes(-10);
        var runner = ComPorta(h, new PortaFalsa { Estado = new TenantOperationState(true, reativadaAntes) });
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("Das 8h às 18h.");
        var turno = h.SeedRunning();

        var resultado = await runner.RunAsync(turno.Id, default);

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
        Assert.Single(h.Gateway.Requests);
        Assert.Single(h.Turns.Registers);
    }

    [Fact]
    public async Task Runner_turno_exatamente_no_instante_da_reativacao_nao_e_cancelado()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var turno = h.SeedRunning();
        var runner = ComPorta(h, new PortaFalsa { Estado = new TenantOperationState(true, turno.LastReceivedAt) });
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("ok");

        var resultado = await runner.RunAsync(turno.Id, default);

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);
    }

    [Fact]
    public async Task Runner_empresa_operacional_que_nunca_foi_suspensa_executa_normalmente()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var runner = ComPorta(h, new PortaFalsa());
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("ok");
        var turno = h.SeedRunning();

        Assert.Equal(TurnRunOutcome.Replied, (await runner.RunAsync(turno.Id, default)).Outcome);
    }

    [Fact]
    public async Task Runner_sem_porta_configurada_se_comporta_como_antes()
    {
        var h = new RunnerHarness().Grant(Snapshots.OneText());
        var runner = ComPorta(h, null);
        h.Gateway.Behavior = _ => ScriptedGateway.Reply("ok");
        var turno = h.SeedRunning();

        Assert.Equal(TurnRunOutcome.Replied, (await runner.RunAsync(turno.Id, default)).Outcome);
    }

    // TurnScheduler

    private static TurnScheduler AgendadorCom(ITenantOperationGate? porta, out InMemoryTurnStore banco, out FakeClock relogio)
    {
        var opcoes = new AgentsOptions();
        relogio = new FakeClock();
        banco = new InMemoryTurnStore(opcoes, relogio);
        return new TurnScheduler(banco, TestContexts.System(TestContexts.TenantA), opcoes, relogio, porta);
    }

    [Fact]
    public async Task Agendador_com_empresa_suspensa_nao_cria_nenhum_turno()
    {
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var agendador = AgendadorCom(porta, out var banco, out var relogio);

        await agendador.OnInboundMessageAsync(Snapshots.SessionId, relogio.GetUtcNow(), default);

        Assert.Empty(banco.OfSession(Snapshots.SessionId));
        Assert.Equal(new[] { TestContexts.TenantA }, porta.Consultas);
    }

    [Fact]
    public async Task Agendador_com_empresa_suspensa_nao_estende_turno_ja_aberto()
    {
        var porta = new PortaFalsa();
        var agendador = AgendadorCom(porta, out var banco, out var relogio);
        await agendador.OnInboundMessageAsync(Snapshots.SessionId, relogio.GetUtcNow(), default);
        var antes = banco.OfSession(Snapshots.SessionId).Single();
        var disparo = antes.FireAt;
        var ultima = antes.LastReceivedAt;

        porta.Estado = new TenantOperationState(false, null);
        relogio.Advance(TimeSpan.FromSeconds(2));
        await agendador.OnInboundMessageAsync(Snapshots.SessionId, relogio.GetUtcNow(), default);

        var depois = Assert.Single(banco.OfSession(Snapshots.SessionId));
        Assert.Equal(disparo, depois.FireAt);
        Assert.Equal(ultima, depois.LastReceivedAt);
    }

    [Fact]
    public async Task Agendador_com_empresa_operacional_cria_o_turno()
    {
        var agendador = AgendadorCom(new PortaFalsa(), out var banco, out var relogio);

        await agendador.OnInboundMessageAsync(Snapshots.SessionId, relogio.GetUtcNow(), default);

        Assert.Single(banco.OfSession(Snapshots.SessionId));
    }

    [Fact]
    public async Task Agendador_sem_porta_cria_o_turno_como_antes()
    {
        var agendador = AgendadorCom(null, out var banco, out var relogio);

        await agendador.OnInboundMessageAsync(Snapshots.SessionId, relogio.GetUtcNow(), default);

        Assert.Single(banco.OfSession(Snapshots.SessionId));
    }
}
