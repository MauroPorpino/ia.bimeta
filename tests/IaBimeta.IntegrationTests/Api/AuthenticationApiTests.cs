using System.Net;
using System.Net.Http.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Autenticação por sessão pela API real (HTTP) com PostgreSQL real: credenciais válidas e inválidas, token opaco,
/// expiração, revogação e a regra de que o tenant nunca vem do cliente.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthenticationApiTests(PostgresFixture banco)
{
    private sealed record Cenario(TenantInfo Tenant, OrganizationView Organizacao, UsuarioCriado Admin);

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        return new Cenario(tenant, organizacao, admin);
    }

    private static async Task<(HttpStatusCode Status, string Corpo)> LoginBrutoAsync(ApiServer api, object corpo)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/auth/login", corpo);
        return (resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
    }

    // Login

    [Fact]
    public async Task Login_valido_devolve_token_opaco_com_expiracao_e_sem_cache()
    {
        var c = await MontarAsync("login");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        using var resposta = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = c.Admin.TenantSlug, email = c.Admin.Email, password = c.Admin.Senha });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
        var login = (await resposta.Content.ReadFromJsonAsync<LoginDto>())!;
        Assert.StartsWith("ibs_", login.Token);
        Assert.Equal(47, login.Token.Length);
        Assert.InRange(login.ExpiresAt, DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow.AddHours(9));
    }

    [Fact]
    public async Task Cada_login_gera_um_token_diferente()
    {
        var c = await MontarAsync("tokens");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        var tokens = new[] { await api.LoginAsync(c.Admin), await api.LoginAsync(c.Admin), await api.LoginAsync(c.Admin) };

        Assert.Equal(3, tokens.Distinct().Count());
    }

    [Fact]
    public async Task Com_a_configuracao_de_producao_o_hash_usa_600_mil_iteracoes_e_o_login_regrava_o_hash_antigo()
    {
        var c = await MontarAsync("iteracoes");
        // Sem sobrescrever as iterações: vale o padrão de produção (600 mil), maior que as 10 mil do hash criado pelo teste.
        var configuracao = new Dictionary<string, string?> { ["ConnectionStrings:Tenancy"] = TestDatabase.ConnectionString };
        await using var api = await ApiServer.StartAsync("Production", configuracao);

        await api.LoginAsync(c.Admin);

        await using var escopo = banco.NovoEscopo();
        var hash = await escopo.ServiceProvider.GetRequiredService<IdentityDbContext>().Database
            .SqlQueryRaw<string>("select password_hash as \"Value\" from identity.users where id = {0}", c.Admin.Id)
            .SingleAsync();
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);

        // E a senha continua valendo depois da regravação.
        Assert.NotEmpty(await api.LoginAsync(c.Admin));
    }

    [Fact]
    public async Task Credenciais_invalidas_respondem_401_identicos_sem_revelar_o_que_existe()
    {
        var c = await MontarAsync("invalido");
        var suspenso = await banco.RegistrarTenantAsync("suspenso");
        var adminSuspenso = await banco.CriarAdministradorDoTenantAsync(suspenso);
        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().SuspendAsync(suspenso.Slug);
        }

        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        var respostas = new[]
        {
            await LoginBrutoAsync(api, new { tenant = c.Admin.TenantSlug, email = c.Admin.Email, password = "senha-errada-123" }),
            await LoginBrutoAsync(api, new { tenant = c.Admin.TenantSlug, email = "ninguem@teste.local", password = c.Admin.Senha }),
            await LoginBrutoAsync(api, new { tenant = "tenant-que-nao-existe", email = c.Admin.Email, password = c.Admin.Senha }),
            await LoginBrutoAsync(api, new { tenant = adminSuspenso.TenantSlug, email = adminSuspenso.Email, password = adminSuspenso.Senha }),
            await LoginBrutoAsync(api, new { tenant = c.Admin.TenantSlug, email = c.Admin.Email, password = "" }),
            await LoginBrutoAsync(api, new { tenant = (string?)null, email = c.Admin.Email, password = c.Admin.Senha }),
            await LoginBrutoAsync(api, new { tenant = c.Admin.TenantSlug, email = "nao-e-email", password = c.Admin.Senha }),
        };

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
        Assert.Single(respostas.Select(r => r.Corpo).Distinct());
        Assert.DoesNotContain("token", respostas[0].Corpo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(c.Admin.Email, respostas[0].Corpo);
    }

    [Fact]
    public async Task Credenciais_de_outro_tenant_nao_entram()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        var (statusCruzado, _) = await LoginBrutoAsync(api, new { tenant = b.Admin.TenantSlug, email = a.Admin.Email, password = a.Admin.Senha });

        Assert.Equal(HttpStatusCode.Unauthorized, statusCruzado);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    public async Task Corpo_de_login_malformado_responde_erro_do_cliente_e_nunca_500(string corpo)
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        using var conteudo = new StringContent(corpo, System.Text.Encoding.UTF8, "application/json");

        using var resposta = await api.Client.PostAsync("/auth/login", conteudo);

        Assert.InRange((int)resposta.StatusCode, 400, 499);
    }

    [Fact]
    public async Task Login_sem_corpo_responde_erro_do_cliente()
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        using var resposta = await api.Client.PostAsync("/auth/login", null);

        Assert.InRange((int)resposta.StatusCode, 400, 499);
    }

    // Token

    [Fact]
    public async Task Token_valido_autentica_e_me_devolve_o_usuario_e_as_concessoes_sem_segredos()
    {
        var c = await MontarAsync("me");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(c.Admin);

        using var resposta = await api.EnviarAsync(token, HttpMethod.Get, "/auth/me");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pbkdf2", corpo);
        Assert.DoesNotContain("password", corpo, StringComparison.OrdinalIgnoreCase);
        var eu = System.Text.Json.JsonSerializer.Deserialize<EuDto>(corpo, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(c.Admin.Id, eu.Id);
        Assert.Equal(c.Admin.Email, eu.Email);
        var concessao = Assert.Single(eu.Grants);
        Assert.Null(concessao.OrganizationId);
        Assert.Equal("OrganizationAdmin", concessao.Role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lixo")]
    [InlineData("ibs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ibs_curto")]
    public async Task Token_ausente_malformado_ou_desconhecido_responde_401_em_toda_rota_protegida(string? token)
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var guid = Guid.NewGuid();

        var rotas = new (HttpMethod Metodo, string Caminho)[]
        {
            (HttpMethod.Get, "/auth/me"),
            (HttpMethod.Post, "/auth/logout"),
            (HttpMethod.Get, "/organizations"),
            (HttpMethod.Get, $"/organizations/{guid}"),
            (HttpMethod.Post, "/organizations"),
            (HttpMethod.Get, $"/organizations/{guid}/business-units"),
            (HttpMethod.Get, $"/organizations/{guid}/users"),
            (HttpMethod.Get, $"/business-units/{guid}"),
            (HttpMethod.Get, $"/departments/{guid}"),
            (HttpMethod.Get, "/audit-events"),
        };

        foreach (var (metodo, caminho) in rotas)
        {
            using var requisicao = api.RequisicaoDe(token, metodo, caminho);
            if (metodo == HttpMethod.Post && caminho == "/organizations")
            {
                requisicao.Content = JsonContent.Create(new { name = "X" });
            }

            using var resposta = await api.Client.SendAsync(requisicao);
            Assert.True(resposta.StatusCode == HttpStatusCode.Unauthorized, $"{metodo} {caminho} respondeu {(int)resposta.StatusCode}");
        }
    }

    [Fact]
    public async Task Esquema_diferente_de_Bearer_ou_cabecalho_repetido_nao_autentica()
    {
        var c = await MontarAsync("esquema");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(c.Admin);

        using var basico = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        basico.Headers.TryAddWithoutValidation("Authorization", "Basic " + token);
        using var respostaBasico = await api.Client.SendAsync(basico);

        using var duplo = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        duplo.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + token, "Bearer " + token });
        using var respostaDupla = await api.Client.SendAsync(duplo);

        using var semEsquema = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        semEsquema.Headers.TryAddWithoutValidation("Authorization", token);
        using var respostaSemEsquema = await api.Client.SendAsync(semEsquema);

        Assert.Equal(HttpStatusCode.Unauthorized, respostaBasico.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, respostaDupla.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, respostaSemEsquema.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
    }

    [Fact]
    public async Task O_esquema_Bearer_nao_diferencia_maiusculas()
    {
        var c = await MontarAsync("caixa");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(c.Admin);

        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        requisicao.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
        using var resposta = await api.Client.SendAsync(requisicao);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    // Expiração e revogação

    [Fact]
    public async Task Sessao_expirada_e_recusada_na_API()
    {
        var c = await MontarAsync("expira");
        var relogio = new RelogioManual();
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao(), relogio);
        var token = await api.LoginAsync(c.Admin);

        relogio.Avancar(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));

        relogio.Avancar(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(token, HttpMethod.Get, "/organizations"));
    }

    [Fact]
    public async Task A_duracao_da_sessao_e_configuravel()
    {
        var c = await MontarAsync("duracao");
        var relogio = new RelogioManual();
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:SessionLifetime"] = "00:30:00";
        await using var api = await ApiServer.StartAsync("Production", configuracao, relogio);
        var token = await api.LoginAsync(c.Admin);

        relogio.Avancar(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
        relogio.Avancar(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
    }

    [Fact]
    public async Task Logout_revoga_a_sessao_e_o_token_deixa_de_valer_sem_afetar_as_outras_sessoes()
    {
        var c = await MontarAsync("logout");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var primeiro = await api.LoginAsync(c.Admin);
        var segundo = await api.LoginAsync(c.Admin);

        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(primeiro, HttpMethod.Post, "/auth/logout"));

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(primeiro, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(primeiro, HttpMethod.Post, "/auth/logout"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(segundo, HttpMethod.Get, "/auth/me"));
    }

    [Fact]
    public async Task Tenant_suspenso_derruba_as_sessoes_ja_abertas()
    {
        var c = await MontarAsync("suspende");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(c.Admin);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));

        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().SuspendAsync(c.Tenant.Slug);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
    }

    // O tenant nunca vem do cliente

    [Fact]
    public async Task Cabecalho_de_tenant_de_desenvolvimento_nao_vence_a_sessao_autenticada()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());
        var tokenDeA = await api.LoginAsync(a.Admin);

        // O cliente do tenant A tenta se passar pelo tenant B com o cabeçalho de desenvolvimento.
        using var requisicao = api.RequisicaoDe(tokenDeA, HttpMethod.Get, "/organizations");
        requisicao.Headers.Add("X-Dev-Tenant", b.Tenant.Slug);
        using var resposta = await api.Client.SendAsync(requisicao);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var lista = (await resposta.Content.ReadFromJsonAsync<List<OrganizacaoDto>>())!;
        Assert.Equal(new[] { a.Organizacao.Id }, lista.Select(o => o.Id));
    }

    [Fact]
    public async Task Em_producao_o_cabecalho_de_desenvolvimento_e_ignorado_mesmo_com_token_valido()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenDeA = await api.LoginAsync(a.Admin);

        using var requisicao = api.RequisicaoDe(tokenDeA, HttpMethod.Get, "/organizations");
        requisicao.Headers.Add("X-Dev-Tenant", b.Tenant.Slug);
        using var resposta = await api.Client.SendAsync(requisicao);

        var lista = (await resposta.Content.ReadFromJsonAsync<List<OrganizacaoDto>>())!;
        Assert.Equal(new[] { a.Organizacao.Id }, lista.Select(o => o.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.StatusAsync(null, HttpMethod.Get, "/organizations"));
    }

    [Fact]
    public async Task Tenant_informado_no_corpo_ou_na_consulta_nao_muda_o_tenant_da_sessao()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenDeA = await api.LoginAsync(a.Admin);
        var tokenDeB = await api.LoginAsync(b.Admin);

        var criada = await api.CriarAsync<OrganizacaoDto>(
            tokenDeA,
            $"/organizations?tenantId={b.Tenant.TenantId.Value}",
            new { name = "Tentativa de A", tenantId = b.Tenant.TenantId.Value, tenant = b.Tenant.Slug });

        var deA = await api.LerAsync<List<OrganizacaoDto>>(tokenDeA, "/organizations");
        var deB = await api.LerAsync<List<OrganizacaoDto>>(tokenDeB, "/organizations");
        Assert.Contains(deA, o => o.Id == criada.Id);
        Assert.DoesNotContain(deB, o => o.Id == criada.Id);
    }

    [Fact]
    public async Task Health_continua_respondendo_sem_token()
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        using var resposta = await api.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }
}
