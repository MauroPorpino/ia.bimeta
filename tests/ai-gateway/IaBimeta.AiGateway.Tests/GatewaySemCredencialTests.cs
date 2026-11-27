using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Tests;

/// <summary>
/// A Etapa 04: um provedor que declara <c>RequiresCredential = false</c> (o Ollama local) é chamado SEM resolver credencial e recebe <see cref="AiCredential.None"/>; o padrão do contrato continua exigindo a
/// credencial, e só o provedor que declara o contrário a dispensa.
/// </summary>
public class GatewaySemCredencialTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("c0000000-0000-0000-0000-000000000003"));

    private sealed class SemChave(string id, bool requires) : IAiProvider
    {
        public List<AiCompletionRequest> Requests { get; } = [];

        public string ProviderId => id;

        public bool RequiresCredential => requires;

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(AiCompletionResult.Success(new AiCompletion("ok", AiStopReason.Completed), new AiUsage(10, 0, 0, 5), TimeSpan.FromMilliseconds(5)));
        }
    }

    /// <summary>O provedor do contrato que NÃO declara nada: vale o padrão (exige credencial).</summary>
    private sealed class ProvedorPadrao(string id) : IAiProvider
    {
        public string ProviderId => id;

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("não deveria ser chamado");
    }

    private sealed class ResolverQueFalha : IAiCredentialResolver
    {
        public int Chamadas { get; private set; }

        public Task<AiCredential?> ResolveAsync(TenantId tenantId, string providerId, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult<AiCredential?>(null);
        }
    }

    private static (AiGatewayService Gateway, ResolverQueFalha Resolver, InMemoryAiUsageLedger Ledger) Monta(IAiProvider provedor, string modelo)
    {
        var catalogo = AiModelCatalog.Create([new AiModelDescriptor(provedor.ProviderId, modelo, "perfil", 8192, 1024, new AiModelPrices("USD", 0, 0, 0, 0), AiModelStatus.Active)]);
        var selector = new AiModelSelector(catalogo, new AiProviderRegistry([provedor]));
        var configuracoes = new FakeTenantAiConfigurationReader().Set(new AiTenantConfiguration(Tenant, provedor.ProviderId, modelo, AiCredentialSource.Byok, GatewayHarness.GenerousLimits));
        var resolver = new ResolverQueFalha();
        var ledger = new InMemoryAiUsageLedger();
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(Tenant, "teste-sistema"));
        return (new AiGatewayService(holder, configuracoes, selector, resolver, ledger, new ManualTimeProvider()), resolver, ledger);
    }

    [Fact]
    public void O_padrao_do_contrato_exige_credencial()
    {
        IAiProvider padrao = new ProvedorPadrao("provider-x");
        Assert.True(padrao.RequiresCredential);
    }

    [Fact]
    public async Task Provedor_sem_chave_e_chamado_sem_resolver_credencial_e_recebe_a_credencial_vazia()
    {
        var provedor = new SemChave("local-x", requires: false);
        var (gateway, resolver, _) = Monta(provedor, "modelo-local");

        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(resultado.IsSuccess);
        Assert.Equal(0, resolver.Chamadas);
        var pedido = Assert.Single(provedor.Requests);
        Assert.Equal(string.Empty, pedido.Credential.Secret);
        Assert.Same(AiCredential.None, pedido.Credential);
        Assert.Equal(0m, resultado.EstimatedCost!.Amount);
    }

    [Fact]
    public async Task Provedor_que_exige_credencial_continua_falhando_fechado_sem_ela_e_nunca_e_chamado()
    {
        var provedor = new SemChave("remoto-x", requires: true);
        var (gateway, resolver, ledger) = Monta(provedor, "modelo-remoto");

        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(AiErrorKind.InvalidCredentials, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure.Code);
        Assert.Equal(1, resolver.Chamadas);
        Assert.Empty(provedor.Requests);
    }

    [Fact]
    public async Task Sem_credencial_o_provedor_padrao_do_contrato_nunca_e_chamado()
    {
        var (gateway, _, _) = Monta(new ProvedorPadrao("provider-y"), "m");
        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());
        Assert.Equal(AiFailureCodes.CredentialMissing, resultado.Failure!.Code);
    }
}
