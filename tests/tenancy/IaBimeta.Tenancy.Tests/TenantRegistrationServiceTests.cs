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

        public Task<(IReadOnlyList<Tenant> Items, int Total)> ListCommercialAsync(string? search, TenantStatus? status, int skip, int take, CancellationToken cancellationToken)
        {
            var itens = Tenants.Where(t => t.Kind == TenantKind.Commercial && (status is null || t.Status == status)).ToList();
            return Task.FromResult(((IReadOnlyList<Tenant>)itens.Skip(skip).Take(take).ToList(), itens.Count));
        }

        public Task<IReadOnlyList<Tenant>> ListAllCommercialAsync(CancellationToken cancellationToken) =>
            Task.FromResult((IReadOnlyList<Tenant>)Tenants.Where(t => t.Kind == TenantKind.Commercial).ToList());

        public Task<Tenant?> FindPlatformAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Tenants.FirstOrDefault(t => t.Kind == TenantKind.Platform));

        public List<TenantStatusChange> StatusChanges { get; } = new();

        public Task SaveStatusChangeAsync(TenantStatusChange change, CancellationToken cancellationToken)
        {
            StatusChanges.Add(change);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TenantStatusChange>> ListStatusChangesAsync(TenantId tenantId, int take, CancellationToken cancellationToken) =>
            Task.FromResult((IReadOnlyList<TenantStatusChange>)StatusChanges.Where(c => c.TenantId == tenantId).Take(take).ToList());
    }

    [Theory]
    [InlineData("bimeta-platform")]
    [InlineData("platform")]
    [InlineData("admin")]
    [InlineData("  Bimeta-Platform  ")]
    public async Task Cadastro_comercial_antigo_recusa_identificadores_reservados_e_nao_grava_nada(string slug)
    {
        var repositorio = new RepositorioEmMemoria();
        var servico = new TenantRegistrationService(repositorio, TimeProvider.System);

        await Assert.ThrowsAsync<TenancyValidationException>(() => servico.RegisterAsync(slug, "Nome qualquer"));

        Assert.Empty(repositorio.Tenants);
    }

    [Fact]
    public async Task O_tenant_tecnico_continua_nascendo_pelo_mecanismo_proprio_da_plataforma()
    {
        var repositorio = new RepositorioEmMemoria();
        var admin = new TenantAdministrationService(repositorio, new RequestContextAccessorNulo(), new AuditoriaEmMemoria(), TimeProvider.System);

        var info = await admin.EnsurePlatformTenantAsync("Plataforma BImeta");

        Assert.Equal(Tenant.PlatformSlug, info.Slug);
        Assert.Equal(TenantKind.Platform, Assert.Single(repositorio.Tenants).Kind);
    }

    private sealed class RequestContextAccessorNulo : IRequestContextAccessor
    {
        public RequestContext? Current => null;
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
