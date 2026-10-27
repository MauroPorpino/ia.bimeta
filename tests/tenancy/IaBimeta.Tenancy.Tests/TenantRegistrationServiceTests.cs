using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

public class TenantRegistrationServiceTests
{
    private sealed class RepositorioEmMemoria : ITenantRepository
    {
        public List<Tenant> Tenants { get; } = new();

        public Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
        {
            Tenants.Add(tenant);
            return Task.CompletedTask;
        }

        public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(Tenants.FirstOrDefault(t => t.Slug == slug));

        public Task<Tenant?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Tenants.FirstOrDefault(t => t.Id == tenantId));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Registrar_devolve_tenant_ativo()
    {
        var repositorio = new RepositorioEmMemoria();
        var servico = new TenantRegistrationService(repositorio, TimeProvider.System);

        var info = await servico.RegisterAsync("lab-central", "Laboratório Central");

        Assert.Equal("lab-central", info.Slug);
        Assert.Equal(TenantStatus.Active, info.Status);
        Assert.Single(repositorio.Tenants);
    }

    [Fact]
    public async Task Slug_repetido_e_rejeitado()
    {
        var servico = new TenantRegistrationService(new RepositorioEmMemoria(), TimeProvider.System);
        await servico.RegisterAsync("lab-central", "Um");

        await Assert.ThrowsAsync<DuplicateTenantSlugException>(() => servico.RegisterAsync("lab-central", "Dois"));
    }

    [Fact]
    public async Task Suspender_tenant_inexistente_devolve_falso()
    {
        var servico = new TenantRegistrationService(new RepositorioEmMemoria(), TimeProvider.System);

        Assert.False(await servico.SuspendAsync("nao-existe"));
    }

    [Fact]
    public async Task Diretorio_normaliza_o_slug_e_enxerga_a_suspensao()
    {
        var repositorio = new RepositorioEmMemoria();
        var servico = new TenantRegistrationService(repositorio, TimeProvider.System);
        var diretorio = new TenantDirectory(repositorio);
        await servico.RegisterAsync("lab-central", "Laboratório Central");

        var ativo = await diretorio.FindBySlugAsync("  LAB-Central ");
        await servico.SuspendAsync("lab-central");
        var suspenso = await diretorio.FindBySlugAsync("lab-central");

        Assert.NotNull(ativo);
        Assert.Equal(TenantStatus.Active, ativo.Status);
        Assert.NotNull(suspenso);
        Assert.Equal(TenantStatus.Suspended, suspenso.Status);
        Assert.Null(await diretorio.FindBySlugAsync("   "));
    }
}
