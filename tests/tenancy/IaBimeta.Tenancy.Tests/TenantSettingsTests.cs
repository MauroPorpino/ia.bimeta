using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

/// <summary>Idioma padrão do tenant (BCP 47): valor inicial, configuração, permissão e isolamento pelo contexto.</summary>
public class TenantSettingsTests
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

    private sealed class Cenario
    {
        public RepositorioEmMemoria Repositorio { get; } = new();
        public AuditoriaEmMemoria Auditoria { get; } = new();
        public RequestContextHolder Holder { get; } = new();

        public TenantSettingsService Servico => new(Repositorio, Holder, Auditoria);

        public async Task<TenantId> RegistrarAsync(string slug, Locale? idioma = null)
        {
            var info = await new TenantRegistrationService(Repositorio, TimeProvider.System).RegisterAsync(slug, $"Tenant {slug}", idioma);
            return info.TenantId;
        }
    }

    [Fact]
    public async Task O_tenant_nasce_com_o_idioma_inicial_da_plataforma_quando_nao_escolhe()
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-central");
        c.Holder.Set(Contextos.DeSistema(tenant));

        Assert.Equal(Tenant.FallbackDefaultLocale, await c.Servico.GetDefaultLocaleAsync());
        Assert.Equal("pt-BR", Tenant.FallbackDefaultLocale.ToString());
    }

    [Fact]
    public async Task O_tenant_pode_nascer_com_o_idioma_escolhido()
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-global", Locale.Parse("en-US"));
        c.Holder.Set(Contextos.DeSistema(tenant));

        Assert.Equal(Locale.Parse("en-US"), await c.Servico.GetDefaultLocaleAsync());
    }

    [Fact]
    public async Task Cada_contexto_enxerga_so_o_idioma_do_proprio_tenant()
    {
        var c = new Cenario();
        var a = await c.RegistrarAsync("lab-a", Locale.Parse("en-US"));
        var b = await c.RegistrarAsync("lab-b", Locale.Parse("es-ES"));

        c.Holder.Set(Contextos.DeSistema(a));
        var doA = await c.Servico.GetDefaultLocaleAsync();
        c.Holder.Set(Contextos.DeSistema(b));
        var doB = await c.Servico.GetDefaultLocaleAsync();

        Assert.Equal((Locale.Parse("en-US"), Locale.Parse("es-ES")), (doA, doB));
    }

    [Fact]
    public async Task O_administrador_do_tenant_troca_o_idioma_na_forma_canonica_e_a_troca_e_auditada()
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-central");
        c.Holder.Set(Contextos.DeUsuario(tenant, new PermissionGrant(Permission.TenantSettingsManage, null)));

        var novo = await c.Servico.SetDefaultLocaleAsync("FR-fr");

        Assert.Equal(Locale.Parse("fr-FR"), novo);
        Assert.Equal(Locale.Parse("fr-FR"), await c.Servico.GetDefaultLocaleAsync());
        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal(("tenancy.tenant.default-locale.set", AuditOutcome.Success), (evento.Operation, evento.Outcome));
    }

    [Fact]
    public async Task Repetir_o_mesmo_idioma_nao_muda_nada_nem_audita_de_novo()
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-central", Locale.Parse("de-DE"));
        c.Holder.Set(Contextos.DeSistema(tenant));

        await c.Servico.SetDefaultLocaleAsync("de-de");

        Assert.Empty(c.Auditoria.Eventos);
    }

    [Fact]
    public async Task Sem_a_permissao_de_configurar_o_tenant_a_troca_e_negada_e_auditada_sem_mudar_nada()
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-central");
        var org = Guid.NewGuid();
        c.Holder.Set(Contextos.DeUsuario(
            tenant,
            new PermissionGrant(Permission.OrganizationRead, org),
            new PermissionGrant(Permission.StructureManage, org),
            new PermissionGrant(Permission.UserManage, org)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.SetDefaultLocaleAsync("en-US"));

        Assert.Equal(Tenant.FallbackDefaultLocale, await c.Servico.GetDefaultLocaleAsync());
        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal(AuditOutcome.Denied, evento.Outcome);
    }

    [Fact]
    public async Task A_permissao_com_escopo_de_organizacao_nao_basta_porque_a_configuracao_e_do_tenant()
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-central");
        c.Holder.Set(Contextos.DeUsuario(tenant, new PermissionGrant(Permission.TenantSettingsManage, Guid.NewGuid())));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.SetDefaultLocaleAsync("en-US"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pt_BR")]
    [InlineData("portugues")]
    [InlineData("pt-BR-BR")]
    public async Task Idioma_que_nao_e_BCP_47_e_recusado_e_o_anterior_continua(string? entrada)
    {
        var c = new Cenario();
        var tenant = await c.RegistrarAsync("lab-central", Locale.Parse("en-US"));
        c.Holder.Set(Contextos.DeSistema(tenant));

        await Assert.ThrowsAsync<TenancyValidationException>(() => c.Servico.SetDefaultLocaleAsync(entrada));

        Assert.Equal(Locale.Parse("en-US"), await c.Servico.GetDefaultLocaleAsync());
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_nada_e_lido_nem_alterado()
    {
        var c = new Cenario();
        await c.RegistrarAsync("lab-central");

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.GetDefaultLocaleAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.SetDefaultLocaleAsync("en-US"));
    }

    [Fact]
    public void O_dominio_do_tenant_troca_o_idioma_e_recusa_nulo()
    {
        var tenant = Tenant.Register(new TenantId(Guid.NewGuid()), "lab-central", "Lab", DateTimeOffset.UtcNow);

        tenant.ChangeDefaultLocale(Locale.Parse("it-IT"));

        Assert.Equal(Locale.Parse("it-IT"), tenant.DefaultLocale);
        Assert.Throws<ArgumentNullException>(() => tenant.ChangeDefaultLocale(null!));
    }
}
