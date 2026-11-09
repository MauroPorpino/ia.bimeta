using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents.Testing;

/// <summary>
/// A montagem de um <see cref="TurnRunner"/> sobre os dublês: o "banco" de turnos em memória (com concorrência otimista), o Conversations roteirizado, o AiGateway roteirizado, o ContactCenter e a definição
/// do agente. Semeia o turno <c>Running</c> que o executor teria reivindicado (com token de lane) e executa o turno. O relógio só anda quando o teste manda.
/// </summary>
internal sealed class RunnerHarness
{
    public RunnerHarness(Action<AgentsOptions>? configure = null)
    {
        Options = new AgentsOptions();
        configure?.Invoke(Options);
        Options.Validate();

        Clock = new FakeClock();
        Store = new InMemoryTurnStore(Options, Clock);
        Turns = new FakeTurnCommands();
        Gateway = new ScriptedGateway();
        Handoff = new FakeHandoff();
        Definitions = new FakeDefinitions();
        Gaps = new FakeGapRecorder();
        Runner = new TurnRunner(Store, Turns, Gateway, Handoff, Definitions, Gaps, Options, Clock);
    }

    public AgentsOptions Options { get; }

    public FakeClock Clock { get; }

    public InMemoryTurnStore Store { get; }

    public FakeTurnCommands Turns { get; }

    public ScriptedGateway Gateway { get; }

    public FakeHandoff Handoff { get; }

    public FakeDefinitions Definitions { get; }

    public FakeGapRecorder Gaps { get; }

    public TurnRunner Runner { get; }

    /// <summary>O Conversations concede o turno com este snapshot (e esta época).</summary>
    public RunnerHarness Grant(TurnSnapshot snapshot, long epoch = 1)
    {
        Turns.OnBegin = _ => FakeTurnCommands.Granted(snapshot, epoch);
        return this;
    }

    /// <summary>
    /// Semeia um turno <c>Running</c>, como o executor o deixaria ao reivindicar. <paramref name="attempts"/> são as tentativas já gastas (cada reivindicação de um turno que ainda estava em andamento, com o lease
    /// vencido, conta uma). <paramref name="waitingForContent"/> simula um turno que já esperou conteúdo pendente (o prazo adicional conta de agora).
    /// </summary>
    public AiTurn SeedRunning(
        Guid? sessionId = null,
        long lane = 7,
        int attempts = 0,
        int supersedeCount = 0,
        string? handoffReason = null,
        bool noticeDone = false,
        bool waitingForContent = false)
    {
        var agora = Clock.GetUtcNow();
        var turno = AiTurn.Schedule(TestContexts.TenantA, sessionId ?? Snapshots.SessionId, agora, agora, Options, agora, supersedeCount);
        turno.Claim(lane, "teste", agora, Options.TurnMaxDuration);

        if (waitingForContent)
        {
            turno.DeferForContent(agora, Options.ContentRecheck);
            turno.Claim(lane, "teste", agora, Options.TurnMaxDuration);
        }

        for (var i = 0; i < attempts; i++)
        {
            turno.Claim(lane, "teste", agora, Options.TurnMaxDuration);
        }

        if (handoffReason is not null)
        {
            turno.RecordHandoff(handoffReason, agora);
        }

        if (noticeDone)
        {
            turno.RecordNoticeDone(agora);
        }

        Store.Seed(turno);
        return turno;
    }

    public Task<TurnRunResult> RunAsync(AiTurn turno, CancellationToken cancellationToken = default) => Runner.RunAsync(turno.Id, cancellationToken);

    /// <summary>O turno como está no "banco" agora.</summary>
    public AiTurn Fresh(AiTurn turno) => Store.Get(turno.Id);

    /// <summary>Um snapshot com o grupo de mensagens informado (o <c>LastInboundSeq</c> é o maior da lista).</summary>
    public static TurnSnapshot Group(params SnapshotMessage[] messages) =>
        Snapshots.Snapshot(messages, lastInbound: messages.Where(m => m.InboundSeq is not null).Max(m => m.InboundSeq!.Value));
}
