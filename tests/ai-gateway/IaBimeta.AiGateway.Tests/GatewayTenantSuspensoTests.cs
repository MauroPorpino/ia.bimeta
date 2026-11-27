using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Tests;

/// <summary>Suspensão efetiva (Etapa 02, ADR 0043): empresa suspensa nunca chega ao provedor, à credencial nem à contabilidade.</summary>
public class GatewayTenantSuspensoTests
{
    private static readonly TenantId A = GatewayHarness.TenantA;

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

    private static AiGatewayService ComPorta(GatewayHarness h, ITenantOperationGate? porta) =>
        new(h.Holder, h.Configurations, h.Selector, h.Credentials, h.Ledger, h.Clock, null, porta);

    [Fact]
    public async Task Tenant_suspenso_falha_com_TenantSuspended_sem_chamar_provedor_credencial_nem_ledger()
    {
        var h = new GatewayHarness();
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var gateway = ComPorta(h, porta);
        h.AsSystem(A);

        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.False(resultado.IsSuccess);
        Assert.Equal(AiErrorKind.NotConfigured, resultado.Failure!.Kind);
        Assert.Equal(AiFailureCodes.TenantSuspended, resultado.Failure.Code);
        Assert.Equal("tenant-suspended", resultado.Failure.Code);
        Assert.Equal(0, h.ProviderA.CallCount);
        Assert.Equal(0, h.ProviderB.CallCount);
        Assert.Empty(h.Credentials.Resolutions);
        Assert.Empty(h.Ledger.Records);
        Assert.Equal(0, h.Ledger.InFlight(A));
        Assert.Equal(new[] { A }, porta.Consultas);
    }

    [Fact]
    public async Task A_porta_e_consultada_com_o_tenant_do_contexto_e_nao_com_o_de_outro()
    {
        var h = new GatewayHarness();
        var porta = new PortaFalsa();
        var gateway = ComPorta(h, porta);
        h.AsSystem(GatewayHarness.TenantB);

        await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.Equal(new[] { GatewayHarness.TenantB }, porta.Consultas);
    }

    [Fact]
    public async Task Tenant_operacional_continua_gerando_normalmente()
    {
        var h = new GatewayHarness();
        var gateway = ComPorta(h, new PortaFalsa { Estado = new TenantOperationState(true, DateTimeOffset.UtcNow.AddDays(-1)) });
        h.AsSystem(A);

        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(resultado.IsSuccess);
        Assert.Equal(1, h.ProviderA.CallCount);
        Assert.Single(h.Ledger.Records);
    }

    [Fact]
    public async Task Sem_porta_configurada_o_comportamento_e_o_de_sempre()
    {
        var h = new GatewayHarness();
        var gateway = ComPorta(h, null);
        h.AsSystem(A);

        var resultado = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(resultado.IsSuccess);
        Assert.Equal(1, h.ProviderA.CallCount);
    }

    [Fact]
    public async Task Reativado_depois_de_suspenso_volta_a_chamar_o_provedor()
    {
        var h = new GatewayHarness();
        var porta = new PortaFalsa { Estado = new TenantOperationState(false, null) };
        var gateway = ComPorta(h, porta);
        h.AsSystem(A);

        Assert.False((await gateway.GenerateAsync(GatewayHarness.Request())).IsSuccess);
        porta.Estado = new TenantOperationState(true, DateTimeOffset.UtcNow);
        var depois = await gateway.GenerateAsync(GatewayHarness.Request());

        Assert.True(depois.IsSuccess);
        Assert.Equal(1, h.ProviderA.CallCount);
    }

    [Fact]
    public async Task Contexto_de_usuario_continua_negado_mesmo_com_tenant_suspenso()
    {
        var h = new GatewayHarness();
        var gateway = ComPorta(h, new PortaFalsa { Estado = new TenantOperationState(false, null) });
        h.AsUser(A);

        await Assert.ThrowsAnyAsync<Exception>(() => gateway.GenerateAsync(GatewayHarness.Request()));
        Assert.Equal(0, h.ProviderA.CallCount);
    }
}
