using System.Text.Json;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Tests.Testing;

/// <summary>O "banco" compartilhado entre os contextos (usuários e tenants) de um cenário: as linhas são as mesmas, o filtro por tenant é o do contexto de cada harness.</summary>
internal sealed class SharedRows
{
    public List<IntegrationConnection> Connections { get; } = [];

    public List<CapabilityDefinition> Definitions { get; } = [];

    public List<CapabilityExecution> Executions { get; } = [];
}

/// <summary>
/// O motor e os serviços de administração montados sobre dublês em memória, com a MESMA composição da produção: cifra real (AES-GCM), conector simulado, relógio controlável. <see cref="As"/> devolve outro harness sobre
/// as mesmas linhas, com outro contexto (outro usuário ou tenant).
/// </summary>
internal sealed class Harness
{
    public static readonly OutboundOptions DefaultOutbound = new();

    public Harness(RequestContextHolder? context = null, SharedRows? rows = null, FakeClock? clock = null, FakeAudit? audit = null, ScriptedConnector? connector = null, CapabilitiesOptions? options = null, bool secretsConfigured = true)
    {
        Context = context ?? TestContexts.User(TestContexts.TenantA, TestContexts.AllIntegration);
        Rows = rows ?? new SharedRows();
        Clock = clock ?? new FakeClock();
        Audit = audit ?? new FakeAudit();
        Connector = connector ?? new ScriptedConnector();
        Options = options ?? new CapabilitiesOptions { RetryBaseDelayMs = 0, RetryMaxDelayMs = 0 };
        Protector = secretsConfigured ? TestSecrets.Protector() : new Infrastructure.AesGcmSecretProtector(new Infrastructure.CapabilitiesSecretOptions());

        Connections = new InMemoryConnections(Context, Rows.Connections);
        Definitions = new InMemoryDefinitions(Context, Rows.Definitions);
        Executions = new InMemoryExecutions(Context, Rows.Executions);
        Policy = new OutboundDestinationPolicy(Options.Outbound);

        var auth = new CapabilitiesAuthorizer(Context, Audit);
        var registry = new ConnectorRegistry([Connector]);
        Engine = new CapabilityExecutionService(auth, Definitions, Connections, Executions, Protector, registry, Options, Clock);
        Admin = new ConnectionService(auth, Connections, Protector, registry, Policy, Options, Clock);
        Defs = new DefinitionService(auth, Definitions, Connections, Options, Clock);
        History = new ExecutionHistoryService(auth, Executions, Clock);
    }

    public RequestContextHolder Context { get; }

    public SharedRows Rows { get; }

    public FakeClock Clock { get; }

    public FakeAudit Audit { get; }

    public ScriptedConnector Connector { get; }

    public CapabilitiesOptions Options { get; }

    public ISecretProtector Protector { get; }

    public InMemoryConnections Connections { get; }

    public InMemoryDefinitions Definitions { get; }

    public InMemoryExecutions Executions { get; }

    public OutboundDestinationPolicy Policy { get; }

    public CapabilityExecutionService Engine { get; }

    public ConnectionService Admin { get; }

    public DefinitionService Defs { get; }

    public ExecutionHistoryService History { get; }

    public Guid UserId => Context.Current!.Principal!.UserId;

    public TenantId Tenant => Context.Current!.TenantId;

    /// <summary>Outro contexto sobre as MESMAS linhas, relógio, auditoria e conector.</summary>
    public Harness As(RequestContextHolder context, bool? secretsConfigured = null) =>
        new(context, Rows, Clock, Audit, Connector, Options, secretsConfigured ?? Protector.IsConfigured);

    public IntegrationConnection SeedConnection(Guid? organizationId = null, bool enabled = true, ConnectorAuthType auth = ConnectorAuthType.None, string key = "erp", string url = "https://erp.example.com/api", string[]? allowedHeaders = null)
    {
        var conexao = IntegrationConnection.Create(
            Tenant, organizationId, key, "ERP de teste", url, auth, new Dictionary<string, string>(), new Dictionary<string, string>(), allowedHeaders ?? [], "/health", 5, 4096, null, Clock.GetUtcNow());
        if (auth == ConnectorAuthType.ApiKey)
        {
            var protegido = Protector.Protect("""{"apiKey":"CHAVE-SECRETISSIMA"}""", new SecretBinding("connection-secret", Tenant, conexao.Id, $"{(int)auth}:1"));
            conexao.SetSecret(protegido.Ciphertext, protegido.KeyId, Clock.GetUtcNow());
        }

        conexao.SetEnabled(enabled, Clock.GetUtcNow());
        Rows.Connections.Add(conexao);
        return conexao;
    }

    public CapabilityDefinition SeedDefinition(
        IntegrationConnection conexao,
        string key,
        CapabilityOperationSpec spec,
        CapabilityEffect? effect = null,
        CapabilityCaller callers = CapabilityCaller.Operator | CapabilityCaller.Agent,
        bool requiresConfirmation = true,
        bool publish = true,
        int version = 1,
        CapabilityDataClass dataClass = CapabilityDataClass.Public)
    {
        var definicao = CapabilityDefinition.CreateDraft(Tenant, conexao.Id, key, version, key, "Descrição de " + key, effect ?? (spec.Method == "GET" ? CapabilityEffect.Read : CapabilityEffect.Write), requiresConfirmation, callers, spec, null, Clock.GetUtcNow(), dataClass);
        if (publish)
        {
            definicao.Publish(null, Clock.GetUtcNow());
        }

        Rows.Definitions.Add(definicao);
        return definicao;
    }

    /// <summary>A organização do atendimento que o código confiável do chamador informa (as conexões dos testes são do tenant inteiro, então qualquer escopo as alcança).</summary>
    public static readonly Guid Scope = Guid.Parse("5c0be000-0000-4000-8000-000000000001");

    public static IReadOnlyDictionary<string, JsonElement> P(object valores) => Specs.Values(valores);

    public CapabilityInvocation Read(string key, object valores, CapabilityCaller caller = CapabilityCaller.Operator, Guid? session = null) =>
        new(key, P(valores), caller, null, session, null, Scope);

    public CapabilityInvocation Write(string key, object valores, string idempotencyKey = "chave-0000001", CapabilityCaller caller = CapabilityCaller.Operator, Guid? session = null) =>
        new(key, P(valores), caller, idempotencyKey, session, null, Scope);

    /// <summary>Tudo que o motor deixou gravado (execuções e auditoria) como texto, para provar que um valor sensível não está lá.</summary>
    public string Everything() =>
        JsonSerializer.Serialize(Rows.Executions.Select(e => new
        {
            e.Id,
            e.DefinitionKey,
            e.DefinitionVersion,
            e.ConnectionId,
            e.OrganizationId,
            e.Effect,
            e.Caller,
            e.RequestedBy,
            e.ServiceSessionId,
            e.IdempotencyKey,
            e.Fingerprint,
            e.Status,
            e.ErrorCode,
            e.UpstreamStatus,
            e.Attempts,
            e.DurationMs,
            e.IsTest,
            e.ConfirmedBy,
            e.PendingCiphertext,
        })) + JsonSerializer.Serialize(Audit.Entries);

    public const string Pedido = "pedidos.consultar";
    public const string Cancelamento = "pedidos.cancelar";

    /// <summary>Conexão e duas operações típicas (leitura e escrita comprovada) prontas, no contexto atual.</summary>
    public (IntegrationConnection Connection, CapabilityDefinition Read, CapabilityDefinition Write) SeedTypical(Guid? organizationId = null, bool confirmWrites = true, CapabilityCaller callers = CapabilityCaller.Operator | CapabilityCaller.Agent)
    {
        var conexao = SeedConnection(organizationId);
        var leitura = SeedDefinition(conexao, Pedido, Specs.Read(), callers: callers);
        var escrita = SeedDefinition(conexao, Cancelamento, Specs.Write(), callers: callers, requiresConfirmation: confirmWrites);
        return (conexao, leitura, escrita);
    }
}
