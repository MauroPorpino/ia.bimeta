using System.Net;
using System.Net.Http.Json;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Proteção do login pela API real: bloqueio temporário por e-mail e tenant, limite de taxa por origem e a regra de que
/// uma falha de auditoria nunca vira sucesso, tudo com PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LoginProtectionApiTests(PostgresFixture banco)
{
    private sealed record Cenario(TenantInfo Tenant, UsuarioCriado Admin, UsuarioCriado Observador);

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var observador = await banco.CriarAdministradorDoTenantAsync(tenant);
        return new Cenario(tenant, admin, observador);
    }

    private static async Task<HttpResponseMessage> LoginAsync(ApiServer api, string tenant, string email, string senha) =>
        await api.Client.PostAsJsonAsync("/auth/login", new { tenant, email, password = senha });

    private static async Task ErrarAsync(ApiServer api, string tenant, string email, int vezes)
    {
        for (var i = 0; i < vezes; i++)
        {
            using var resposta = await LoginAsync(api, tenant, email, "senha-errada-123");
            Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        }
    }

    // Bloqueio temporário

    [Fact]
    public async Task Cinco_senhas_erradas_bloqueiam_e_a_senha_certa_tambem_responde_429_com_Retry_After()
    {
        var c = await MontarAsync("bloqueio");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        await ErrarAsync(api, c.Admin.TenantSlug, c.Admin.Email, 5);
        using var certa = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha);

        Assert.Equal(HttpStatusCode.TooManyRequests, certa.StatusCode);
        var espera = int.Parse(certa.Headers.GetValues("Retry-After").Single());
        Assert.InRange(espera, 850, 900);
        Assert.DoesNotContain("token", await certa.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task O_bloqueio_responde_igual_para_e_mail_que_existe_e_que_nao_existe()
    {
        var c = await MontarAsync("igual");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        await ErrarAsync(api, c.Admin.TenantSlug, c.Admin.Email, 5);
        await ErrarAsync(api, c.Admin.TenantSlug, "ninguem@teste.local", 5);
        using var existente = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, "qualquer-senha-123");
        using var inexistente = await LoginAsync(api, c.Admin.TenantSlug, "ninguem@teste.local", "qualquer-senha-123");

        Assert.Equal(HttpStatusCode.TooManyRequests, existente.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, inexistente.StatusCode);
        Assert.Equal(await existente.Content.ReadAsStringAsync(), await inexistente.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task O_bloqueio_nao_atinge_outro_usuario_nem_o_mesmo_e_mail_em_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        var mesmoEmailEmB = await banco.CriarAdministradorDoTenantAsync(b.Tenant, a.Admin.Email);
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());

        await ErrarAsync(api, a.Admin.TenantSlug, a.Admin.Email, 5);

        using var outroUsuarioDeA = await LoginAsync(api, a.Tenant.Slug, a.Observador.Email, a.Observador.Senha);
        using var mesmoEmailOutroTenant = await LoginAsync(api, b.Tenant.Slug, mesmoEmailEmB.Email, mesmoEmailEmB.Senha);
        using var bloqueado = await LoginAsync(api, a.Tenant.Slug, a.Admin.Email, a.Admin.Senha);
        Assert.Equal(HttpStatusCode.OK, outroUsuarioDeA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mesmoEmailOutroTenant.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);
    }

    [Fact]
    public async Task Passado_o_tempo_do_bloqueio_a_senha_certa_entra_na_API()
    {
        var c = await MontarAsync("passou");
        var relogio = new RelogioManual();
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao(), relogio);

        await ErrarAsync(api, c.Admin.TenantSlug, c.Admin.Email, 5);
        using (var bloqueado = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);
        }

        relogio.Avancar(TimeSpan.FromMinutes(16));

        Assert.NotEmpty(await api.LoginAsync(c.Admin));
    }

    [Fact]
    public async Task Os_limites_do_bloqueio_vem_da_configuracao()
    {
        var c = await MontarAsync("config");
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:MaxFailedLogins"] = "2";
        configuracao["Identity:LockoutDuration"] = "00:01:00";
        await using var api = await ApiServer.StartAsync("Production", configuracao);

        await ErrarAsync(api, c.Admin.TenantSlug, c.Admin.Email, 2);
        using var bloqueado = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);
        Assert.InRange(int.Parse(bloqueado.Headers.GetValues("Retry-After").Single()), 55, 60);
    }

    [Fact]
    public async Task O_bloqueio_entra_na_trilha_uma_vez_e_nao_afeta_as_sessoes_ja_abertas()
    {
        var c = await MontarAsync("trilha");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenDoObservador = await api.LoginAsync(c.Observador);
        var tokenDoAlvo = await api.LoginAsync(c.Admin);

        await ErrarAsync(api, c.Admin.TenantSlug, c.Admin.Email, 5);
        for (var i = 0; i < 4; i++)
        {
            using var bloqueado = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, "qualquer-senha-123");
            Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);
        }

        var eventos = await api.LerAsync<List<EventoDto>>(tokenDoObservador, "/audit-events?take=200&operation=identity.login.lockout");
        Assert.Single(eventos);
        Assert.Equal("Denied", eventos.Single().Outcome);

        // O bloqueio vale para novos logins: a sessão que já estava aberta continua válida.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(tokenDoAlvo, HttpMethod.Get, "/auth/me"));
    }

    // Limite de taxa por origem

    [Fact]
    public async Task Alem_do_limite_por_origem_o_login_responde_429_mesmo_com_e_mails_diferentes()
    {
        var c = await MontarAsync("taxa");
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:LoginRateLimit:PermitLimit"] = "3";
        configuracao["Identity:LoginRateLimit:WindowSeconds"] = "60";
        await using var api = await ApiServer.StartAsync("Production", configuracao);

        var status = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var resposta = await LoginAsync(api, c.Admin.TenantSlug, $"alvo{i}@teste.local", "senha-errada-123");
            status.Add(resposta.StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests }, status);
    }

    [Fact]
    public async Task O_limite_de_taxa_do_login_nao_atinge_o_health_nem_as_rotas_autenticadas()
    {
        var c = await MontarAsync("livre");
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:LoginRateLimit:PermitLimit"] = "1";
        await using var api = await ApiServer.StartAsync("Production", configuracao);
        var token = await api.LoginAsync(c.Admin);

        using (var estourou = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, estourou.StatusCode);
        }

        for (var i = 0; i < 5; i++)
        {
            using var saude = await api.Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, saude.StatusCode);
            Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
        }
    }

    // Falha de auditoria nunca vira sucesso

    [Fact]
    public async Task Com_a_auditoria_fora_do_ar_nenhuma_operacao_responde_sucesso_e_a_leitura_continua()
    {
        var c = await MontarAsync("auditoria");
        string tokenValido;
        await using (var saudavel = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao()))
        {
            tokenValido = await saudavel.LoginAsync(c.Admin);
        }

        await using var api = await ApiServer.StartAsync(
            "Production",
            ApiServer.ConfiguracaoDeProducao(),
            servicos: servicos => servicos.AddScoped<IAuditWriter, AuditoriaQueFalha>());

        // Escrever: o erro sobe (500) em vez de responder sucesso.
        using var criar = await api.EnviarAsync(tokenValido, HttpMethod.Post, "/organizations", new { name = "Sem auditoria" });
        Assert.Equal(HttpStatusCode.InternalServerError, criar.StatusCode);

        // Login bem-sucedido também depende da trilha: sem ela, nenhum token é devolvido.
        using var login = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha);
        Assert.Equal(HttpStatusCode.InternalServerError, login.StatusCode);
        Assert.DoesNotContain("ibs_", await login.Content.ReadAsStringAsync());

        // Ler não grava auditoria e continua funcionando.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(tokenValido, HttpMethod.Get, "/organizations"));
    }

    [Fact]
    public async Task Com_a_auditoria_fora_do_ar_a_contagem_de_falhas_de_login_continua_bloqueando()
    {
        var c = await MontarAsync("protecao");
        await using var api = await ApiServer.StartAsync(
            "Production",
            ApiServer.ConfiguracaoDeProducao(),
            servicos: servicos => servicos.AddScoped<IAuditWriter, AuditoriaQueFalha>());

        for (var i = 0; i < 5; i++)
        {
            using var resposta = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, "senha-errada-123");
            Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        }

        using var bloqueado = await LoginAsync(api, c.Admin.TenantSlug, c.Admin.Email, c.Admin.Senha);
        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);
    }

    [Fact]
    public async Task Negacao_com_a_auditoria_fora_do_ar_responde_500_e_nao_um_403_silencioso()
    {
        var c = await MontarAsync("negacao");
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, (await banco.CriarOrganizacaoAsync(c.Tenant, "Org")).Id, IaBimeta.Identity.Domain.Role.Manager);
        string token;
        await using (var saudavel = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao()))
        {
            token = await saudavel.LoginAsync(gestor);
        }

        await using var api = await ApiServer.StartAsync(
            "Production",
            ApiServer.ConfiguracaoDeProducao(),
            servicos: servicos => servicos.AddScoped<IAuditWriter, AuditoriaQueFalha>());

        // Um gestor não cria organização: sem auditoria o 403 não pode ser entregue como se a negação estivesse registrada.
        using var resposta = await api.EnviarAsync(token, HttpMethod.Post, "/organizations", new { name = "Nao pode" });

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
    }
}
