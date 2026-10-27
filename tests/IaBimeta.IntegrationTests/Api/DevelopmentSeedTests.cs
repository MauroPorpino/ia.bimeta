using System.Net;
using System.Net.Http.Json;
using IaBimeta.Api;
using IaBimeta.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O administrador inicial por seed existe só em Development (TEN-1: nenhuma credencial padrão de produção). A partida
/// falha se o seed for ligado em qualquer outro ambiente, e a senha sintética nunca vai para a trilha nem para o log.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DevelopmentSeedTests(PostgresFixture banco)
{
    private const string SenhaSintetica = "Senha-Sintetica-De-Seed-2026!";

    private static Dictionary<string, string?> ConfiguracaoDoSeed(string slug, string email, string senha)
    {
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento();
        configuracao["Tenancy:Development:ApplyMigrations"] = "true";
        configuracao["Tenancy:Development:SeedTenants:0:Slug"] = slug;
        configuracao["Tenancy:Development:SeedTenants:0:Name"] = $"Tenant de seed {slug}";
        configuracao["Tenancy:Development:SeedAdministrators:0:TenantSlug"] = slug;
        configuracao["Tenancy:Development:SeedAdministrators:0:Email"] = email;
        configuracao["Tenancy:Development:SeedAdministrators:0:DisplayName"] = "Administrador de seed";
        configuracao["Tenancy:Development:SeedAdministrators:0:Password"] = senha;
        return configuracao;
    }

    private static string SlugNovo() => $"seed-{Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task Em_Development_o_seed_cria_o_administrador_do_tenant_que_consegue_entrar()
    {
        var slug = SlugNovo();
        var email = $"admin@{slug}.local";
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoDoSeed(slug, email, SenhaSintetica));

        var token = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaSintetica, slug));

        var eu = await api.LerAsync<EuDto>(token, "/auth/me");
        Assert.Equal(email, eu.Email);
        var concessao = Assert.Single(eu.Grants);
        Assert.Null(concessao.OrganizationId);
        Assert.Equal("OrganizationAdmin", concessao.Role);

        // Com concessão de tenant o administrador de seed cria organizações e lê a auditoria.
        var organizacao = await api.CriarAsync<OrganizacaoDto>(token, "/organizations", new { name = "Criada pelo seed" });
        Assert.Equal(new[] { organizacao.Id }, (await api.LerAsync<List<OrganizacaoDto>>(token, "/organizations")).Select(o => o.Id));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/audit-events"));
    }

    [Fact]
    public async Task O_seed_e_idempotente_e_nao_duplica_o_administrador_ao_subir_de_novo()
    {
        var slug = SlugNovo();
        var email = $"admin@{slug}.local";

        await using (var primeira = await ApiServer.StartAsync("Development", ConfiguracaoDoSeed(slug, email, SenhaSintetica)))
        {
            await primeira.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaSintetica, slug));
        }

        await using var segunda = await ApiServer.StartAsync("Development", ConfiguracaoDoSeed(slug, email, SenhaSintetica));
        await segunda.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaSintetica, slug));

        await using var escopo = banco.NovoEscopo();
        var quantos = await escopo.ServiceProvider.GetRequiredService<IdentityDbContext>().Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from identity.users where email = {0}", email)
            .SingleAsync();
        Assert.Equal(1, quantos);
    }

    [Fact]
    public async Task Seed_com_senha_fraca_nao_derruba_a_API_e_nao_cria_o_usuario()
    {
        var slug = SlugNovo();
        var email = $"admin@{slug}.local";

        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoDoSeed(slug, email, "123"));

        using var saude = await api.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, saude.StatusCode);
        using var login = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = slug, email, password = "123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task A_senha_de_seed_nunca_aparece_na_trilha_de_auditoria()
    {
        var slug = SlugNovo();
        var email = $"admin@{slug}.local";
        await using var api = await ApiServer.StartAsync("Development", ConfiguracaoDoSeed(slug, email, SenhaSintetica));
        var token = await api.LoginAsync(new UsuarioCriado(Guid.Empty, email, SenhaSintetica, slug));

        using var resposta = await api.EnviarAsync(token, HttpMethod.Get, "/audit-events?take=200");
        var texto = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("identity.tenant-admin.create", texto);
        Assert.Contains("development-seed", texto);
        Assert.DoesNotContain(SenhaSintetica, texto);
        Assert.DoesNotContain(token, texto);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_partida_falha_se_o_seed_de_administrador_for_ligado_fora_de_Development(string ambiente)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Tenancy"] = TestDatabase.ConnectionString,
            ["Tenancy:Development:SeedAdministrators:0:TenantSlug"] = "qualquer",
            ["Tenancy:Development:SeedAdministrators:0:Email"] = "admin@qualquer.local",
            ["Tenancy:Development:SeedAdministrators:0:DisplayName"] = "Admin",
            ["Tenancy:Development:SeedAdministrators:0:Password"] = SenhaSintetica,
        };

        var falha = Assert.Throws<InvalidOperationException>(() => ApiHost.Build(
            new[] { "--environment=" + ambiente },
            builder => builder.Configuration.AddInMemoryCollection(configuracao)));

        Assert.Contains("Development", falha.Message);
        Assert.DoesNotContain(SenhaSintetica, falha.Message);
    }

    [Fact]
    public async Task Em_producao_nenhum_administrador_e_criado_e_nao_ha_credencial_padrao()
    {
        var slug = SlugNovo();
        var tenant = await banco.RegistrarTenantAsync("producao");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        // Sem seed e sem usuário, ninguém entra, nem com as senhas e e-mails que o seed de desenvolvimento usaria.
        foreach (var (email, senha) in new[] { ("admin@demo-a.local", "DevLocal-Admin-2026!"), ($"admin@{slug}.local", SenhaSintetica) })
        {
            using var resposta = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = tenant.Slug, email, password = senha });
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }

        await using var escopo = banco.NovoEscopo();
        var usuarios = await escopo.ServiceProvider.GetRequiredService<IdentityDbContext>().Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from identity.users where tenant_id = {0}", tenant.TenantId.Value)
            .SingleAsync();
        Assert.Equal(0, usuarios);
    }
}
