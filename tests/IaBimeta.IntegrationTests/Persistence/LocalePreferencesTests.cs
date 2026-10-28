using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Preferências de idioma com PostgreSQL real: o idioma padrão do tenant (Tenancy) e o idioma preferido do usuário
/// (Identity), com BCP 47 na forma canônica, permissão, auditoria, isolamento entre tenants e restrições no próprio banco.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LocalePreferencesTests(PostgresFixture banco)
{
    private async Task<Locale> IdiomaDoTenantAsync(TenantInfo tenant)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<ITenantSettings>().GetDefaultLocaleAsync();
    }

    private async Task DefinirIdiomaDoTenantAsync(TenantInfo tenant, string idioma, RequestContext? contexto = null)
    {
        await using var escopo = banco.NovoEscopo(contexto ?? PostgresFixture.Contexto(tenant));
        await escopo.ServiceProvider.GetRequiredService<TenantSettingsService>().SetDefaultLocaleAsync(idioma);
    }

    private async Task<string?> IdiomaDoUsuarioNoBancoAsync(TenantInfo tenant, Guid usuarioId)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var usuario = await escopo.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(usuarioId, default);
        return usuario?.PreferredLocale?.ToString();
    }

    // Idioma padrão do tenant

    [Fact]
    public async Task Tenant_sem_idioma_nasce_com_o_inicial_da_plataforma_e_o_escolhido_vale_em_forma_canonica()
    {
        var semEscolha = await banco.RegistrarTenantAsync("idiomapadrao");
        await using var escopo = banco.NovoEscopo();
        var slug = $"idiomaescolhido-{Guid.NewGuid().ToString("N")[..8]}";
        var escolhido = await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().RegisterAsync(slug, "Tenant com idioma", Locale.Parse("en-us"));

        Assert.Equal(Locale.Parse("pt-BR"), await IdiomaDoTenantAsync(semEscolha));
        Assert.Equal(Locale.Parse("en-US"), await IdiomaDoTenantAsync(escolhido));
    }

    [Fact]
    public async Task Cada_tenant_enxerga_e_altera_so_o_proprio_idioma_padrao()
    {
        var a = await banco.RegistrarTenantAsync("idiomaa");
        var b = await banco.RegistrarTenantAsync("idiomab");

        await DefinirIdiomaDoTenantAsync(a, "EN-us");
        await DefinirIdiomaDoTenantAsync(b, "es-ES");

        Assert.Equal(Locale.Parse("en-US"), await IdiomaDoTenantAsync(a));
        Assert.Equal(Locale.Parse("es-ES"), await IdiomaDoTenantAsync(b));

        // Trocar o de um não toca no do outro.
        await DefinirIdiomaDoTenantAsync(a, "fr-FR");
        Assert.Equal((Locale.Parse("fr-FR"), Locale.Parse("es-ES")), (await IdiomaDoTenantAsync(a), await IdiomaDoTenantAsync(b)));
    }

    [Fact]
    public async Task Trocar_o_idioma_do_tenant_exige_a_permissao_de_escopo_do_tenant_e_tudo_vai_para_a_auditoria()
    {
        var tenant = await banco.RegistrarTenantAsync("idiomaperm");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Organização")).Id;
        var semPermissao = PostgresFixture.ContextoDeUsuario(tenant, Guid.NewGuid(), new PermissionGrant(Permission.UserManage, org), new PermissionGrant(Permission.StructureManage, org));
        var comOrganizacao = PostgresFixture.ContextoDeUsuario(tenant, Guid.NewGuid(), new PermissionGrant(Permission.TenantSettingsManage, org));
        var admin = PostgresFixture.ContextoDeUsuario(tenant, Guid.NewGuid(), new PermissionGrant(Permission.TenantSettingsManage, null));

        await Assert.ThrowsAsync<AccessDeniedException>(() => DefinirIdiomaDoTenantAsync(tenant, "en-US", semPermissao));
        await Assert.ThrowsAsync<AccessDeniedException>(() => DefinirIdiomaDoTenantAsync(tenant, "en-US", comOrganizacao));
        Assert.Equal(Locale.Parse("pt-BR"), await IdiomaDoTenantAsync(tenant));

        await DefinirIdiomaDoTenantAsync(tenant, "de-DE", admin);

        Assert.Equal(Locale.Parse("de-DE"), await IdiomaDoTenantAsync(tenant));
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var eventos = await escopo.ServiceProvider.GetRequiredService<AuditDbContext>().Events
            .Where(e => e.Operation == "tenancy.tenant.default-locale.set")
            .Select(e => e.Outcome)
            .ToListAsync();
        Assert.Equal(2, eventos.Count(o => o == AuditOutcome.Denied));
        Assert.Equal(1, eventos.Count(o => o == AuditOutcome.Success));
    }

    [Theory]
    [InlineData("pt_BR")]
    [InlineData("")]
    [InlineData("não é um idioma")]
    public async Task Idioma_do_tenant_fora_do_formato_e_recusado_pelo_aplicativo(string entrada)
    {
        var tenant = await banco.RegistrarTenantAsync("idiomaruim");

        await Assert.ThrowsAsync<TenancyValidationException>(() => DefinirIdiomaDoTenantAsync(tenant, entrada));

        Assert.Equal(Locale.Parse("pt-BR"), await IdiomaDoTenantAsync(tenant));
    }

    [Fact]
    public async Task O_banco_recusa_idioma_padrao_do_tenant_fora_do_formato_ou_nulo()
    {
        var tenant = await banco.RegistrarTenantAsync("idiomabanco");
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var formato = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("update tenancy.tenants set default_locale = 'pt_BR' where id = {0}", tenant.TenantId.Value));
        var nulo = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("update tenancy.tenants set default_locale = null where id = {0}", tenant.TenantId.Value));

        Assert.Equal("ck_tenants_default_locale", formato.ConstraintName);
        Assert.Equal(PostgresErrorCodes.NotNullViolation, nulo.SqlState);
    }

    // Idioma preferido do usuário

    [Fact]
    public async Task O_usuario_define_o_proprio_idioma_e_isso_nao_alcanca_os_usuarios_de_outro_tenant()
    {
        var a = await banco.RegistrarTenantAsync("usuarioa");
        var b = await banco.RegistrarTenantAsync("usuariob");
        var orgA = (await banco.CriarOrganizacaoAsync(a, "Org A")).Id;
        var orgB = (await banco.CriarOrganizacaoAsync(b, "Org B")).Id;
        var email = $"mesmo{Guid.NewGuid().ToString("N")[..8]}@teste.local"; // o mesmo e-mail nos dois tenants
        var deA = await banco.CriarUsuarioAsync(a, orgA, Role.User, email);
        var deB = await banco.CriarUsuarioAsync(b, orgB, Role.User, email);

        await using (var escopo = banco.NovoEscopo(PostgresFixture.ContextoDeUsuario(a, deA.Id)))
        {
            Assert.Equal(Locale.Parse("en-US"), await escopo.ServiceProvider.GetRequiredService<UserService>().SetMyPreferredLocaleAsync("EN-us"));
        }

        Assert.Equal("en-US", await IdiomaDoUsuarioNoBancoAsync(a, deA.Id));
        Assert.Null(await IdiomaDoUsuarioNoBancoAsync(b, deB.Id));

        // Uma sessão do tenant B com o id de um usuário do tenant A: o usuário não existe lá, e o de A não é tocado.
        await using (var escopo = banco.NovoEscopo(PostgresFixture.ContextoDeUsuario(b, deA.Id)))
        {
            await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => escopo.ServiceProvider.GetRequiredService<UserService>().SetMyPreferredLocaleAsync("ja-JP"));
        }

        Assert.Equal("en-US", await IdiomaDoUsuarioNoBancoAsync(a, deA.Id));
        Assert.Null(await IdiomaDoUsuarioNoBancoAsync(b, deB.Id));
    }

    [Fact]
    public async Task Limpar_a_preferencia_do_usuario_a_remove_do_banco()
    {
        var tenant = await banco.RegistrarTenantAsync("usuariolimpa");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Org")).Id;
        var usuario = await banco.CriarUsuarioAsync(tenant, org, Role.User);

        await using (var escopo = banco.NovoEscopo(PostgresFixture.ContextoDeUsuario(tenant, usuario.Id)))
        {
            var servico = escopo.ServiceProvider.GetRequiredService<UserService>();
            await servico.SetMyPreferredLocaleAsync("fr-FR");
            Assert.Null(await servico.SetMyPreferredLocaleAsync(null));
        }

        Assert.Null(await IdiomaDoUsuarioNoBancoAsync(tenant, usuario.Id));
    }

    [Fact]
    public async Task O_administrador_cria_o_usuario_com_o_idioma_preferido_gravado_na_forma_canonica()
    {
        var tenant = await banco.RegistrarTenantAsync("usuarioidioma");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Org")).Id;

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var criado = await escopo.ServiceProvider.GetRequiredService<UserService>()
            .CreateAsync(org, $"idioma{Guid.NewGuid().ToString("N")[..8]}@teste.local", "Com idioma", PostgresFixture.PasswordPadrao, Role.User, "PT-pt");

        Assert.Equal(Locale.Parse("pt-PT"), criado.PreferredLocale);
        Assert.Equal("pt-PT", await IdiomaDoUsuarioNoBancoAsync(tenant, criado.Id));
    }

    [Fact]
    public async Task O_banco_recusa_idioma_preferido_do_usuario_fora_do_formato()
    {
        var tenant = await banco.RegistrarTenantAsync("usuariobanco");
        var org = (await banco.CriarOrganizacaoAsync(tenant, "Org")).Id;
        var usuario = await banco.CriarUsuarioAsync(tenant, org, Role.User);
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var falha = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("update identity.users set preferred_locale = 'pt_BR' where tenant_id = {0} and id = {1}", tenant.TenantId.Value, usuario.Id));

        Assert.Equal("ck_users_preferred_locale", falha.ConstraintName);
    }
}
