using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Testing;

/// <summary>O relógio dos testes: o tempo só anda quando o teste manda.</summary>
internal sealed class FakeClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}

/// <summary>O contexto confiável do host: tenant, origem e (opcionalmente) um usuário com permissões. Só os hosts criam o <see cref="RequestContext"/>; os testes o criam pelo acesso interno.</summary>
internal static class TestContexts
{
    public static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    public static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-00000000000b"));

    public static RequestContextHolder System(TenantId? tenant = null)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant ?? TenantA, "teste"));
        return holder;
    }

    public static RequestContextHolder User(TenantId? tenant = null, params Permission[] permissions)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant ?? TenantA, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), permissions.Select(p => new PermissionGrant(p, null)))));
        return holder;
    }
}

/// <summary>
/// O "banco" dos turnos em memória, com a MESMA semântica do real: cada leitura entrega uma cópia, a gravação confere a versão lida (concorrência otimista) e o índice único de um turno aberto por
/// sessão. <see cref="Mutate"/> altera a linha por fora, como o handler dos eventos faria ao mesmo tempo que o executor.
/// </summary>
internal sealed class InMemoryTurnStore : IAiTurnStore
{
    private readonly Dictionary<Guid, AiTurn> _rows = new();
    private readonly Dictionary<Guid, (AiTurn Tracked, int LoadedVersion)> _tracked = new();
    private readonly List<AiTurn> _added = new();
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;
    private long _lane;

    public InMemoryTurnStore(AgentsOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>Uma ação que roda UMA vez, logo antes da próxima gravação: simula uma alteração concorrente (a outra operação grava primeiro).</summary>
    public Action? BeforeNextSave { get; set; }

    public int SaveCount { get; private set; }

    public IReadOnlyList<AiTurn> All => _rows.Values.Select(r => r.Clone()).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();

    public AiTurn Get(Guid id) => _rows[id].Clone();

    public IReadOnlyList<AiTurn> OfSession(Guid sessionId) => _rows.Values.Where(r => r.SessionId == sessionId).Select(r => r.Clone()).OrderBy(r => r.CreatedAt).ToList();

    /// <summary>Grava direto, como se outra operação tivesse confirmado (sem passar pelo rastreamento).</summary>
    public void Seed(AiTurn turn) => _rows[turn.Id] = turn.Clone();

    public void Mutate(Guid id, Action<AiTurn> change)
    {
        var linha = _rows[id].Clone();
        change(linha);
        _rows[id] = linha;
    }

    public Task<AiTurn?> FindAsync(Guid turnId, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(turnId, out var linha))
        {
            return Task.FromResult<AiTurn?>(null);
        }

        var copia = linha.Clone();
        _tracked[turnId] = (copia, copia.Version);
        return Task.FromResult<AiTurn?>(copia);
    }

    public Task<AiTurn?> FindOpenBySessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var linha = _rows.Values.FirstOrDefault(r => r.SessionId == sessionId && r.IsOpen);
        if (linha is null)
        {
            return Task.FromResult<AiTurn?>(null);
        }

        var copia = linha.Clone();
        _tracked[copia.Id] = (copia, copia.Version);
        return Task.FromResult<AiTurn?>(copia);
    }

    public void Add(AiTurn turn) => _added.Add(turn);

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        var antes = BeforeNextSave;
        BeforeNextSave = null;
        antes?.Invoke();

        // Concorrência otimista: a linha precisa estar na versão lida.
        foreach (var (id, (rastreado, lida)) in _tracked)
        {
            if (rastreado.Version != lida && (!_rows.TryGetValue(id, out var linha) || linha.Version != lida))
            {
                Clear();
                throw new AiTurnConflictException();
            }
        }

        var depois = _rows.Values.Select(r => r.Clone()).ToDictionary(r => r.Id);
        foreach (var (id, (rastreado, lida)) in _tracked.ToList())
        {
            if (rastreado.Version != lida)
            {
                depois[id] = rastreado.Clone();
            }
        }

        foreach (var novo in _added)
        {
            depois[novo.Id] = novo.Clone();
        }

        // O índice único parcial: no máximo um turno aberto por sessão.
        if (depois.Values.Where(t => t.IsOpen).GroupBy(t => (t.TenantId, t.SessionId)).Any(g => g.Count() > 1))
        {
            Clear();
            throw new AiTurnConflictException();
        }

        _rows.Clear();
        foreach (var (id, turno) in depois)
        {
            _rows[id] = turno;
        }

        Clear();
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiTurn>> ClaimDueAsync(string owner, int max, CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var emAndamento = _rows.Values.Count(r => r.State == AiTurnState.Running && r.LeaseExpiresAt > agora);
        var vagas = Math.Min(max, _options.MaxConcurrentTurnsPerTenant - emAndamento);
        var reivindicados = new List<AiTurn>();
        foreach (var devido in _rows.Values.Where(r => r.IsDue(agora)).OrderBy(r => r.NextAttemptAt ?? r.FireAt).Take(Math.Max(0, vagas)).ToList())
        {
            var copia = devido.Clone();
            copia.Claim(++_lane, owner, agora, _options.TurnMaxDuration);
            _rows[copia.Id] = copia;
            reivindicados.Add(copia.Clone());
        }

        return Task.FromResult<IReadOnlyList<AiTurn>>(reivindicados);
    }

    private void Clear()
    {
        _tracked.Clear();
        _added.Clear();
    }
}

/// <summary>O Conversations roteirizado: cada comando tem um comportamento padrão (concedido, aceito) que o teste troca, e todas as chamadas ficam registradas.</summary>
internal sealed class FakeTurnCommands : ITurnCommands
{
    public Func<BeginTurnCommand, BeginTurnResult> OnBegin { get; set; } = _ => throw new InvalidOperationException("OnBegin não definido.");

    public Func<RegisterAiReplyCommand, RegisterAiReplyResult> OnRegister { get; set; } =
        c => new RegisterAiReplyResult(RegisterReplyOutcome.Accepted, Guid.NewGuid(), c.BasedOnInboundSeq, c.BasedOnInboundSeq);

    public Func<EndTurnCommand, EndTurnResult> OnEnd { get; set; } = _ => new EndTurnResult(EndTurnOutcome.Ended);

    public List<BeginTurnCommand> Begins { get; } = new();

    public List<RegisterAiReplyCommand> Registers { get; } = new();

    public List<EndTurnCommand> Ends { get; } = new();

    public List<CancelTurnCommand> Cancels { get; } = new();

    /// <summary>Uma ação que roda DENTRO do <c>RegisterAiReply</c>, antes de a resposta ser decidida (o humano assume, chega mensagem).</summary>
    public Action<RegisterAiReplyCommand>? DuringRegister { get; set; }

    public Task<BeginTurnResult> BeginTurnAsync(BeginTurnCommand command, CancellationToken cancellationToken = default)
    {
        Begins.Add(command);
        return Task.FromResult(OnBegin(command));
    }

    public Task<EndTurnResult> EndTurnAsync(EndTurnCommand command, CancellationToken cancellationToken = default)
    {
        Ends.Add(command);
        return Task.FromResult(OnEnd(command));
    }

    public Task<CancelTurnResult> CancelTurnAsync(CancelTurnCommand command, CancellationToken cancellationToken = default)
    {
        Cancels.Add(command);
        return Task.FromResult(new CancelTurnResult(true, true, 1));
    }

    public Task<RegisterAiReplyResult> RegisterAiReplyAsync(RegisterAiReplyCommand command, CancellationToken cancellationToken = default)
    {
        Registers.Add(command);
        DuringRegister?.Invoke(command);
        return Task.FromResult(OnRegister(command));
    }

    public Task<RaiseSessionAttentionResult> RaiseSessionAttentionAsync(RaiseSessionAttentionCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A C2 não sinaliza rajada longa.");

    /// <summary>Um <c>BeginTurn</c> concedido com este snapshot.</summary>
    public static BeginTurnResult Granted(TurnSnapshot snapshot, long epoch = 1) =>
        new(BeginTurnOutcome.Granted, new TurnGrant(epoch, snapshot.LastInboundSeq, snapshot.StateVersion), snapshot);
}

/// <summary>O AiGateway roteirizado: devolve o que o teste manda, registra cada pedido (para inspecionar o que o modelo veria) e conta as chamadas.</summary>
internal sealed class ScriptedGateway : IAiGateway
{
    public Func<AiGenerationRequest, AiGenerationResult> Behavior { get; set; } = _ => Reply("resposta de teste");

    public List<AiGenerationRequest> Requests { get; } = new();

    /// <summary>Roda antes de devolver o resultado (a mensagem chega, o humano assume, enquanto a "chamada" está em voo).</summary>
    public Action? DuringCall { get; set; }

    public Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        DuringCall?.Invoke();
        return Task.FromResult(Behavior(request));
    }

    public static AiGenerationResult Reply(string text) => Output("{\"action\":\"reply\",\"reply_text\":" + System.Text.Json.JsonSerializer.Serialize(text) + ",\"handoff_reason\":\"none\"}");

    public static AiGenerationResult Handoff(string reason) => Output("{\"action\":\"handoff\",\"reply_text\":\"\",\"handoff_reason\":\"" + reason + "\"}");

    public static AiGenerationResult Output(string text, AiStopReason stop = AiStopReason.Completed) =>
        new(Guid.NewGuid(), "provedor-a", "modelo-a", new AiCompletion(text, stop), null, new AiUsage(100, 0, 0, 20), new AiCostEstimate(0.0001m, "USD"), TimeSpan.FromMilliseconds(20));

    public static AiGenerationResult Failed(AiErrorKind kind, bool retryable = false, string? code = null, TimeSpan? retryAfter = null) =>
        new(Guid.NewGuid(), "provedor-a", "modelo-a", null, new AiFailure(kind, retryable, retryAfter, code), AiUsage.None, null, TimeSpan.Zero);
}

internal sealed class FakeHandoff : IHandoffRequests
{
    /// <summary>O pedido DE VERDADE (cria o item). Padrão: aceito.</summary>
    public Func<RequestHandoffCommand, RequestHandoffResult> Behavior { get; set; } =
        _ => new RequestHandoffResult(RequestHandoffOutcome.Completed, Guid.NewGuid(), Guid.NewGuid());

    /// <summary>A verificação somente de leitura (<c>CheckOnly</c>) que antecede o aviso de transferência. Padrão: roteável agora.</summary>
    public Func<RequestHandoffCommand, RequestHandoffResult> CheckBehavior { get; set; } = _ => new RequestHandoffResult(RequestHandoffOutcome.Routable);

    /// <summary>Só os pedidos de verdade.</summary>
    public List<RequestHandoffCommand> Requests { get; } = new();

    /// <summary>Só as verificações (nada é gravado por elas).</summary>
    public List<RequestHandoffCommand> Checks { get; } = new();

    public Task<RequestHandoffResult> RequestHandoffAsync(RequestHandoffCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CheckOnly)
        {
            Checks.Add(command);
            return Task.FromResult(CheckBehavior(command));
        }

        Requests.Add(command);
        return Task.FromResult(Behavior(command));
    }
}

/// <summary>O registrador de lacunas dos testes: guarda o que o turno lhe entregou e pode falhar de propósito (o turno nunca pode depender dele).</summary>
internal sealed class FakeGapRecorder : IKnowledgeGapRecorder
{
    public List<KnowledgeGapCandidate> Candidates { get; } = new();

    public Func<KnowledgeGapCandidate, Task>? Behavior { get; set; }

    public async Task RecordAsync(KnowledgeGapCandidate candidate, CancellationToken cancellationToken)
    {
        Candidates.Add(candidate);
        if (Behavior is not null)
        {
            await Behavior(candidate);
        }
    }
}

internal sealed class FakeDefinitions : IAgentDefinitionReader
{
    public AgentDefinitionContent? Published { get; set; } = Definitions.Default();

    public Task<AgentDefinitionContent?> GetPublishedAsync(CancellationToken cancellationToken) => Task.FromResult(Published);
}

internal sealed class FakeAudit : IAuditWriter
{
    public List<AuditEntry> Entries { get; } = new();

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) => WriteAsync(entry, cancellationToken);
}

/// <summary>O armazenamento das versões da definição em memória (o mesmo contrato do real).</summary>
internal sealed class InMemoryDefinitionStore : IAgentDefinitionStore
{
    public List<AgentDefinitionVersion> Rows { get; } = new();

    public bool FailNextSave { get; set; }

    public Task<AgentDefinitionContent?> GetPublishedAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Rows.FirstOrDefault(v => v.Status == AgentDefinitionStatus.Published)?.Content);

    public Task<AgentDefinitionVersion?> FindDraftAsync(CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(v => v.Status == AgentDefinitionStatus.Draft));

    public Task<AgentDefinitionVersion?> FindPublishedVersionAsync(CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(v => v.Status == AgentDefinitionStatus.Published));

    public Task<int> NextNumberAsync(CancellationToken cancellationToken) => Task.FromResult((Rows.Count == 0 ? 0 : Rows.Max(v => v.Number)) + 1);

    public void Add(AgentDefinitionVersion version) => Rows.Add(version);

    public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken) => work();

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new AgentDefinitionConflictException();
        }

        return Task.CompletedTask;
    }
}

internal static class Definitions
{
    public static AgentDefinitionContent Default() => new(
        "Atenda com cortesia. Horário de funcionamento: segunda a sexta, das 7h às 17h.",
        new[] { new FaqEntry("Preciso de jejum para o hemograma?", "Em geral não; confirme com o atendimento.") },
        "Recebemos seu áudio, mas por enquanto só conseguimos ler mensagens de texto. Pode escrever sua dúvida?",
        "Vou transferir você para um atendente. Um momento, por favor.");
}

/// <summary>Montagem de snapshots e mensagens para os testes do turno.</summary>
internal static class Snapshots
{
    public static readonly Guid SessionId = Guid.Parse("11111111-0000-0000-0000-000000001001");
    public static readonly Guid ConversationId = Guid.Parse("22222222-0000-0000-0000-000000001001");
    public static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static SnapshotMessage Customer(
        long seq,
        string? text,
        MessageKind kind = MessageKind.Text,
        ContentState content = ContentState.Ready,
        bool pending = true,
        int minute = 0) =>
        new(Guid.NewGuid(), MessageDirection.Inbound, MessageAuthor.Customer, seq, seq, kind, content, content == ContentState.Ready ? text : null, false, pending, T0.AddMinutes(minute));

    public static SnapshotMessage Outbound(long seq, string text, MessageAuthor author = MessageAuthor.Ai, int minute = 0) =>
        new(Guid.NewGuid(), MessageDirection.Outbound, author, seq, null, MessageKind.Text, ContentState.Ready, text, false, false, T0.AddMinutes(minute));

    public static TurnSnapshot Snapshot(
        IReadOnlyList<SnapshotMessage> messages,
        long lastInbound = 1,
        long answered = 0,
        long stateVersion = 3,
        HandlingMode mode = HandlingMode.AiAutonomous,
        string locale = "pt-BR",
        LocaleSource source = LocaleSource.ChannelDefault,
        DeliveryState? lastDelivery = null,
        bool replyDeliveryFailed = false) =>
        new(
            SessionId,
            mode,
            stateVersion,
            lastInbound,
            answered,
            replyDeliveryFailed,
            lastDelivery,
            messages,
            ConversationId,
            Locale.Parse(locale),
            source,
            ChannelInstanceId.New(),
            Guid.NewGuid(),
            null);

    /// <summary>O snapshot mais comum: uma mensagem de texto do cliente, ainda sem resposta.</summary>
    public static TurnSnapshot OneText(string text = "Preciso de jejum para o exame?") => Snapshot(new[] { Customer(1, text) });
}
