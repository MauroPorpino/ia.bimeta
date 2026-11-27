using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Tenancy;

/// <summary>
/// P1-02 (revisão do Codex, Etapa 02): o EF Core reaproveita a entidade já rastreada no MESMO DbContext, então a leitura do tenant pelo gate podia devolver Status e ReactivatedAt desatualizados depois de uma
/// suspensão feita em outro escopo. O gate agora consulta o estado persistido por projeção sem rastreamento. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantOperationGateRastreamentoTests(PostgresFixture banco)
{
    private sealed class ProvedorFalso : IAiProvider
    {
        public string ProviderId => PostgresFixture.ProvedorDeIADeTeste;

        public int Chamadas { get; private set; }

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult(AiCompletionResult.Success(new AiCompletion("ok", AiStopReason.Completed), new AiUsage(10, 0, 0, 5), TimeSpan.FromMilliseconds(1), "req"));
        }
    }

    private async Task SuspenderEmOutroEscopoAsync(TenantInfo tenant, string motivo = "teste de rastreamento")
    {
        await using var outro = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var repo = outro.ServiceProvider.GetRequiredService<ITenantRepository>();
        var entidade = (await repo.FindByIdAsync(tenant.TenantId, default))!;
        await repo.SaveStatusChangeAsync(entidade.SuspendWithReason(motivo, null, DateTimeOffset.UtcNow), default);
    }

    private async Task ReativarEmOutroEscopoAsync(TenantInfo tenant, string motivo = "teste de reativacao")
    {
        await using var outro = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var repo = outro.ServiceProvider.GetRequiredService<ITenantRepository>();
        var entidade = (await repo.FindByIdAsync(tenant.TenantId, default))!;
        await repo.SaveStatusChangeAsync(entidade.ReactivateWithReason(motivo, null, DateTimeOffset.UtcNow), default);
    }

    [Fact]
    public async Task Suspensao_feita_em_outro_escopo_e_reconhecida_mesmo_com_a_entidade_ja_rastreada_e_o_AiGateway_nao_alcanca_o_provedor()
    {
        var tenant = await banco.RegistrarTenantAsync("gaterast");
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var sp = escopo.ServiceProvider;
        var gate = sp.GetRequiredService<ITenantOperationGate>();
        var repo = sp.GetRequiredService<ITenantRepository>();

        // 1. O escopo original lê o tenant (a entidade passa a ser rastreada, como no TurnRunner) e o gate o vê ativo.
        Assert.Equal(TenantStatus.Active, (await repo.FindByIdAsync(tenant.TenantId, default))!.Status);
        Assert.True((await gate.GetAsync(tenant.TenantId)).IsOperational);

        // 2. Suspensão gravada em OUTRO escopo.
        await SuspenderEmOutroEscopoAsync(tenant);

        // Reprodução do defeito: a leitura pela entidade rastreada (o que o gate fazia) continua vendo "Active".
        Assert.Equal(TenantStatus.Active, (await repo.FindByIdAsync(tenant.TenantId, default))!.Status);

        // 3 e 4. O gate, no escopo original, reconhece a suspensão.
        Assert.False((await gate.GetAsync(tenant.TenantId)).IsOperational);

        // 5. O AiGateway, com a mesma porta, falha fechado sem tocar o provedor.
        var provedor = new ProvedorFalso();
        var gateway = new AiGatewayService(
            sp.GetRequiredService<IRequestContextAccessor>(),
            sp.GetRequiredService<ITenantAiConfigurationReader>(),
            new AiModelSelector(AiModelCatalog.Create(PostgresFixture.ModelosDeIADeTeste().Select(m => m.ToDescriptor())), new AiProviderRegistry(new IAiProvider[] { provedor })),
            sp.GetRequiredService<IAiCredentialResolver>(),
            sp.GetRequiredService<IAiUsageLedger>(),
            TimeProvider.System,
            null,
            gate);
        var resultado = await gateway.GenerateAsync(new AiGenerationRequest("turno", "sistema", new[] { new AiMessage(AiMessageRole.User, "oi") }, 100, TimeSpan.FromSeconds(5)));

        Assert.False(resultado.IsSuccess);
        Assert.Equal(AiFailureCodes.TenantSuspended, resultado.Failure!.Code);
        Assert.Equal(0, provedor.Chamadas);
    }

    [Fact]
    public async Task A_marca_de_reativacao_tambem_e_atualizada_no_escopo_original()
    {
        var tenant = await banco.RegistrarTenantAsync("gaterea");
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var sp = escopo.ServiceProvider;
        var gate = sp.GetRequiredService<ITenantOperationGate>();
        var repo = sp.GetRequiredService<ITenantRepository>();

        await repo.FindByIdAsync(tenant.TenantId, default); // rastreia
        var inicial = await gate.GetAsync(tenant.TenantId);
        Assert.True(inicial.IsOperational);
        Assert.Null(inicial.OperationalSince);

        await SuspenderEmOutroEscopoAsync(tenant);
        Assert.False((await gate.GetAsync(tenant.TenantId)).IsOperational);

        var antesDaReativacao = DateTimeOffset.UtcNow;
        await ReativarEmOutroEscopoAsync(tenant);

        var depois = await gate.GetAsync(tenant.TenantId);
        Assert.True(depois.IsOperational);
        Assert.NotNull(depois.OperationalSince);
        Assert.True(depois.OperationalSince >= antesDaReativacao.AddSeconds(-1));

        // Uma segunda suspensão e reativação move a marca (nunca fica a antiga).
        await SuspenderEmOutroEscopoAsync(tenant, "segunda suspensao");
        await Task.Delay(20);
        await ReativarEmOutroEscopoAsync(tenant, "segunda reativacao");
        var novamente = await gate.GetAsync(tenant.TenantId);
        Assert.True(novamente.OperationalSince > depois.OperationalSince);
    }

    [Fact]
    public async Task Tenant_inexistente_continua_falhando_fechado()
    {
        await using var escopo = banco.NovoEscopo();
        var estado = await escopo.ServiceProvider.GetRequiredService<ITenantOperationGate>().GetAsync(new TenantId(Guid.NewGuid()));
        Assert.False(estado.IsOperational);
    }
}
