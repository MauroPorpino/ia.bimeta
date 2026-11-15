using System.Net;
using System.Text.Json;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure;
using IaBimeta.Capabilities.Infrastructure.Outbound;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Tests.Testing;

/// <summary>O contexto confiável do host: só os hosts criam o <see cref="RequestContext"/>; os testes o criam pelo acesso interno.</summary>
internal static class TestContexts
{
    public static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    public static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-00000000000b"));
    public static readonly Guid OrgA = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    public static readonly Guid OrgB = Guid.Parse("0b000000-0000-0000-0000-00000000000b");

    public static RequestContextHolder System(TenantId? tenant = null)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant ?? TenantA, "teste"));
        return holder;
    }

    /// <summary>Um usuário com as permissões dadas, todas com escopo de TENANT (organização nula).</summary>
    public static RequestContextHolder User(TenantId? tenant = null, params Permission[] permissions) =>
        User(tenant, permissions.Select(p => new PermissionGrant(p, null)).ToArray());

    public static RequestContextHolder User(TenantId? tenant, params PermissionGrant[] grants)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant ?? TenantA, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), grants)));
        return holder;
    }

    public static readonly Permission[] AllIntegration =
    [
        Permission.IntegrationRead, Permission.IntegrationManage, Permission.IntegrationTest, Permission.IntegrationExecute, Permission.IntegrationExecuteWrite,
    ];
}

internal sealed class FakeClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
}

internal sealed class FakeAudit : IAuditWriter
{
    public List<AuditEntry> Entries { get; } = [];

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) => WriteAsync(entry, cancellationToken);
}

internal sealed class InMemoryConnections : IConnectionStore
{
    private readonly IRequestContextAccessor _context;

    public InMemoryConnections(IRequestContextAccessor context, List<IntegrationConnection>? rows = null)
    {
        _context = context;
        Rows = rows ?? [];
    }

    public List<IntegrationConnection> Rows { get; }

    private IEnumerable<IntegrationConnection> Mine => Rows.Where(c => c.TenantId == _context.GetRequired().TenantId);

    public Task<IntegrationConnection?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Mine.FirstOrDefault(c => c.Id == id));

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IntegrationConnection>>(Mine.ToList());

    public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Mine.Count());

    public void Add(IntegrationConnection connection) => Rows.Add(connection);

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        var duplicadas = Rows.GroupBy(c => (c.TenantId, c.Key)).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToList();
        if (duplicadas.Count > 0)
        {
            // Como o banco: a gravação que viola o índice único não persiste.
            duplicadas.ForEach(d => Rows.Remove(d));
            throw new CapabilitiesConflictException("key-in-use", "Já existe uma conexão com esta chave.");
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryDefinitions : IDefinitionStore
{
    private readonly IRequestContextAccessor _context;

    public InMemoryDefinitions(IRequestContextAccessor context, List<CapabilityDefinition>? rows = null)
    {
        _context = context;
        Rows = rows ?? [];
    }

    public List<CapabilityDefinition> Rows { get; }

    private IEnumerable<CapabilityDefinition> Mine => Rows.Where(d => d.TenantId == _context.GetRequired().TenantId);

    public Task<CapabilityDefinition?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Mine.FirstOrDefault(d => d.Id == id));

    public Task<CapabilityDefinition?> FindPublishedAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Mine.FirstOrDefault(d => d.Key == key && d.Status == DefinitionStatus.Published));

    public Task<CapabilityDefinition?> FindDraftAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Mine.FirstOrDefault(d => d.Key == key && d.Status == DefinitionStatus.Draft));

    public Task<IReadOnlyList<CapabilityDefinition>> ListVersionsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CapabilityDefinition>>(Mine.Where(d => d.Key == key).OrderByDescending(d => d.Version).ToList());

    public Task<IReadOnlyList<CapabilityDefinition>> ListLatestAsync(Guid? connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CapabilityDefinition>>(
            Mine.Where(d => connectionId is null || d.ConnectionId == connectionId).GroupBy(d => d.Key).Select(g => g.OrderByDescending(d => d.Version).First()).ToList());

    public Task<IReadOnlyList<CapabilityDefinition>> ListPublishedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CapabilityDefinition>>(Mine.Where(d => d.Status == DefinitionStatus.Published).ToList());

    public Task<int> CountKeysAsync(CancellationToken cancellationToken) => Task.FromResult(Mine.Select(d => d.Key).Distinct().Count());

    public Task<int> MaxVersionAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Mine.Where(d => d.Key == key).Select(d => d.Version).DefaultIfEmpty(0).Max());

    public void Add(CapabilityDefinition definition) => Rows.Add(definition);

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Rows.Where(d => d.Status == DefinitionStatus.Published).GroupBy(d => (d.TenantId, d.Key)).Any(g => g.Count() > 1))
        {
            throw new CapabilitiesConflictException("key-in-use", "duas publicadas");
        }

        return Task.CompletedTask;
    }

    public async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken) => await work();
}

internal sealed class InMemoryExecutions : IExecutionStore
{
    private readonly IRequestContextAccessor _context;
    private readonly object _lock = new();

    public InMemoryExecutions(IRequestContextAccessor context, List<CapabilityExecution>? rows = null)
    {
        _context = context;
        Rows = rows ?? [];
    }

    public List<CapabilityExecution> Rows { get; }

    // Instantâneo sob a MESMA trava de TryAddAsync: execuções simultâneas acrescentam linhas enquanto outras contam as em andamento, e enumerar a lista viva falhava com "Collection was modified" (defeito do dublê, não do código sob teste).
    private IEnumerable<CapabilityExecution> Mine
    {
        get
        {
            var tenant = _context.GetRequired().TenantId;
            lock (_lock)
            {
                return Rows.Where(e => e.TenantId == tenant).ToList();
            }
        }
    }

    public Task<(bool Added, CapabilityExecution Execution)> TryAddAsync(CapabilityExecution execution, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (execution.IdempotencyKey is not null && Rows.FirstOrDefault(e => e.TenantId == execution.TenantId && e.IdempotencyKey == execution.IdempotencyKey) is { } existente)
            {
                return Task.FromResult((false, existente));
            }

            Rows.Add(execution);
            return Task.FromResult((true, execution));
        }
    }

    public Task<CapabilityExecution?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Mine.FirstOrDefault(e => e.Id == id));

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<int> CountSinceAsync(DateTimeOffset since, Guid? connectionId, bool writesOnly, CancellationToken cancellationToken) =>
        Task.FromResult(Mine.Count(e => e.CreatedAt >= since && (connectionId is null || e.ConnectionId == connectionId) && (!writesOnly || e.Effect == CapabilityEffect.Write)));

    public Task<int> CountInFlightAsync(Guid connectionId, DateTimeOffset notBefore, CancellationToken cancellationToken) =>
        Task.FromResult(Mine.Count(e => e.ConnectionId == connectionId && e.Status == CapabilityExecutionStatus.InProgress && e.CreatedAt >= notBefore));

    private IEnumerable<CapabilityExecution> Filtered(ExecutionFilter f, IReadOnlySet<Guid>? orgs) =>
        Mine.Where(e => (orgs is null || (e.OrganizationId is { } o && orgs.Contains(o)))
            && (f.ConnectionId is null || e.ConnectionId == f.ConnectionId)
            && (f.Key is null || e.DefinitionKey == f.Key)
            && (f.Status is null || e.Status == f.Status)
            && (f.IsTest is null || e.IsTest == f.IsTest));

    public Task<IReadOnlyList<CapabilityExecution>> ListAsync(ExecutionFilter filter, IReadOnlySet<Guid>? organizations, int take, int skip, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CapabilityExecution>>(Filtered(filter, organizations).OrderByDescending(e => e.CreatedAt).Skip(skip).Take(take).ToList());

    public Task<int> CountAsync(ExecutionFilter filter, IReadOnlySet<Guid>? organizations, CancellationToken cancellationToken) => Task.FromResult(Filtered(filter, organizations).Count());

    public Task<IReadOnlyList<ConnectionStats>> StatsAsync(DateTimeOffset since, IReadOnlySet<Guid>? organizations, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConnectionStats>>(
            Filtered(new ExecutionFilter(null, null, null, null), organizations).Where(e => e.CreatedAt >= since).GroupBy(e => e.ConnectionId)
                .Select(g => new ConnectionStats(g.Key, g.Count(), g.Count(e => e.Status == CapabilityExecutionStatus.Failed), g.Count(e => e.Status == CapabilityExecutionStatus.Unknown), g.Max(e => e.CreatedAt)))
                .ToList());
}

/// <summary>O conector simulado do motor: devolve o que o teste roteirizar e registra cada chamada (sem rede).</summary>
internal sealed class ScriptedConnector : ICapabilityConnector
{
    public string Kind => IntegrationConnection.RestJsonKind;

    public List<ConnectorCall> Calls { get; } = [];

    /// <summary>Por padrão responde com sucesso e um corpo que satisfaz o mapeamento da operação (um valor por campo declarado).</summary>
    public Func<ConnectorCall, ConnectorResult> Script { get; set; } = call => Ok(Synthesize(call.Operation));

    private static string Synthesize(CapabilityOperationSpec spec)
    {
        var campos = spec.Response.Select(f => $"\"{f.Pointer.TrimStart('/')}\":{(f.Type is CapabilityFieldType.Integer or CapabilityFieldType.Number ? "1" : f.Type == CapabilityFieldType.Boolean ? "true" : "\"x\"")}");
        return "{" + string.Join(",", campos) + "}";
    }

    public Func<ConnectorConnection, ConnectorResult> Probe { get; set; } = _ => Ok("{}");

    public TaskCompletionSource? Gate { get; set; }

    public static ConnectorResult Ok(string json) => new(ConnectorOutcome.Success, 200, JsonDocument.Parse(json).RootElement.Clone(), 1, 5, true);

    public static ConnectorResult Fail(ConnectorOutcome outcome, int? status = null, bool mayHaveExecuted = true) => new(outcome, status, null, 1, 5, mayHaveExecuted);

    public async Task<ConnectorResult> InvokeAsync(ConnectorCall call, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        if (Gate is not null)
        {
            await Gate.Task;
        }

        return Script(call);
    }

    public Task<ConnectorResult> ProbeAsync(ConnectorConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(Probe(connection));
}

/// <summary>O transporte de saída simulado do conector: respostas roteirizadas por chamada e o registro de tudo que "saiu".</summary>
internal sealed class FakeOutbound : IOutboundHttp
{
    public List<OutboundRequest> Requests { get; } = [];

    /// <summary>Resposta (ou exceção) por tentativa, na ordem. A última se repete.</summary>
    public List<Func<OutboundRequest, OutboundResponse>> Script { get; } = [];

    public Func<OutboundRequest, OutboundResponse>? ForUri { get; set; }

    public Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken)
    {
        int indice;
        lock (Requests)
        {
            Requests.Add(request);
            indice = Requests.Count - 1;
        }

        var passo = ForUri ?? Script[Math.Min(indice, Script.Count - 1)];
        return Task.FromResult(passo(request));
    }

    public static OutboundResponse Json(int status, string json, TimeSpan? retryAfter = null) => new(status, "application/json", System.Text.Encoding.UTF8.GetBytes(json), retryAfter);
}

internal sealed class FakeResolver : IHostResolver
{
    public Dictionary<string, IPAddress[]> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        Asked.Add(host);
        return Task.FromResult(Map.TryGetValue(host, out var enderecos) ? enderecos : throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
    }
}

internal static class TestSecrets
{
    public static CapabilitiesSecretOptions Options(string keyId = "k1") => new()
    {
        ActiveKeyId = keyId,
        Keys = { [keyId] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()), ["k0"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(200 - i)).ToArray()) },
    };

    internal static ISecretProtector Protector(string keyId = "k1") => new AesGcmSecretProtector(Options(keyId));
}
