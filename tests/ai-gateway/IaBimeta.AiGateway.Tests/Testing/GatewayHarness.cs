using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Testing;

/// <summary>O relógio dos testes: o instante é controlado; os temporizadores (tempo limite) continuam reais e curtos.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Monta o AiGateway inteiro com dublês: dois tenants, dois provedores sintéticos (<c>provider-a</c> e <c>provider-b</c>) e um terceiro no catálogo SEM
/// adaptador, credenciais sintéticas (canários), contabilidade em memória e relógio controlado. Nenhum identificador real de provedor, nenhuma chave real e
/// nenhuma rede. Cada teste cria a sua montagem: nada é compartilhado entre testes.
/// </summary>
public sealed class GatewayHarness
{
    public const string ProviderAId = "provider-a";
    public const string ProviderBId = "provider-b";
    public const string ProviderWithoutAdapterId = "provider-c";

    public const string CredentialOfTenantA = "CANARIO-CHAVE-DO-TENANT-A";
    public const string CredentialOfTenantB = "CANARIO-CHAVE-DO-TENANT-B";

    public static readonly AiModelPrices Prices = new("USD", 0.10m, 0.01m, 0.125m, 0.50m);

    public static readonly AiTenantLimits GenerousLimits = new(
        MaxConcurrentCalls: 8, MaxCallsPerPeriod: 1000, MaxTokensPerPeriod: 1_000_000, Period: TimeSpan.FromHours(1), MaxInputMessages: 50, MaxOutputTokens: 500);

    public static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-000000000001"));
    public static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-000000000002"));

    public GatewayHarness(AiGatewayOptions? options = null)
    {
        ProviderA = new FakeAiProvider(ProviderAId);
        ProviderB = new FakeAiProvider(ProviderBId);

        Catalog = AiModelCatalog.Create(new[]
        {
            Model(ProviderAId, "model-a1", "profile-a", AiModelStatus.Active),
            Model(ProviderAId, "model-a2", "profile-a", AiModelStatus.Disabled),
            Model(ProviderBId, "model-b1", "profile-b", AiModelStatus.Active),
            Model(ProviderWithoutAdapterId, "model-c1", "profile-c", AiModelStatus.Active),
        });
        Providers = new AiProviderRegistry(new IAiProvider[] { ProviderA, ProviderB });
        Selector = new AiModelSelector(Catalog, Providers);

        Configurations
            .Set(new AiTenantConfiguration(TenantA, ProviderAId, "model-a1", AiCredentialSource.Byok, GenerousLimits))
            .Set(new AiTenantConfiguration(TenantB, ProviderBId, "model-b1", AiCredentialSource.Byok, GenerousLimits));
        Credentials
            .Set(TenantA, ProviderAId, CredentialOfTenantA)
            .Set(TenantB, ProviderBId, CredentialOfTenantB);

        Gateway = new AiGatewayService(Holder, Configurations, Selector, Credentials, Ledger, Clock, options);
    }

    public RequestContextHolder Holder { get; } = new();

    public ManualTimeProvider Clock { get; } = new();

    public FakeAiProvider ProviderA { get; }

    public FakeAiProvider ProviderB { get; }

    public AiModelCatalog Catalog { get; }

    public AiProviderRegistry Providers { get; }

    public AiModelSelector Selector { get; }

    public FakeTenantAiConfigurationReader Configurations { get; } = new();

    public FakeAiCredentialResolver Credentials { get; } = new();

    public InMemoryAiUsageLedger Ledger { get; } = new();

    public AiGatewayService Gateway { get; }

    public static AiModelDescriptor Model(string providerId, string modelId, string profile, AiModelStatus status) =>
        new(providerId, modelId, profile, ContextWindowTokens: 200_000, MaxOutputTokens: 4_000, Prices, status);

    /// <summary>Chama como contexto de SISTEMA do tenant (o Agents, no Worker).</summary>
    public void AsSystem(TenantId tenant) => Holder.Set(new RequestContext(tenant, "teste-sistema"));

    /// <summary>Chama como um USUÁRIO do tenant com as concessões dadas (nunca tem <see cref="Permission.SystemOperation"/>).</summary>
    public void AsUser(TenantId tenant, params PermissionGrant[] grants) =>
        Holder.Set(new RequestContext(tenant, "teste-sessao", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), grants)));

    public static AiGenerationRequest Request(
        string text = "Qual o horário de atendimento?",
        int maxOutputTokens = 300,
        TimeSpan? timeout = null,
        string correlationId = "turno-1",
        string system = "Você é a atendente virtual.") =>
        new(correlationId, system, new[] { new AiMessage(AiMessageRole.User, text) }, maxOutputTokens, timeout ?? TimeSpan.FromSeconds(5));
}
