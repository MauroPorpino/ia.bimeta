using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Tenancy.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A chave <c>Platform:Enabled</c> (ADR 0043, seção 7), pela API REAL: padrão ligada só em Development; desligada, tudo sob <c>/platform</c> (inclusive o login) responde 404 no backend; o guarda do ingresso
/// público continua valendo com a plataforma ligada; as empresas contratantes não são afetadas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlataformaDisponibilidadeTests(PostgresFixture banco)
{
    private static readonly string[] RotasDaPlataforma =
    [
        "/platform/auth/login",
        "/platform/auth/anything",
        "/platform/companies",
        "/platform/overview",
        "/platform/operators",
        "/platform/audit",
        "/platform",
    ];

    private static Dictionary<string, string?> Producao(string? plataforma = null)
    {
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        if (plataforma is not null)
        {
            configuracao["Platform:Enabled"] = plataforma;
        }

        return configuracao;
    }

    private static Dictionary<string, string?> Desenvolvimento(string? plataforma = null)
    {
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false);
        if (plataforma is not null)
        {
            configuracao["Platform:Enabled"] = plataforma;
        }

        return configuracao;
    }

    /// <summary>A rota existe e recusou a credencial (401), ou o limite de tentativas de login a barrou (429): em ambos os casos NÃO é 404.</summary>
    private static void ExisteERecusa(HttpStatusCode status) =>
        Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests, $"status {(int)status}");

    private static async Task<HttpStatusCode> ChamarAsync(ApiServer api, HttpMethod metodo, string caminho, string? token = null, Action<HttpRequestMessage>? ajusta = null)
    {
        using var requisicao = api.RequisicaoDe(token, metodo, caminho, metodo == HttpMethod.Post ? JsonContent.Create(new { email = "x@plataforma.local", password = "senha-qualquer" }) : null);
        ajusta?.Invoke(requisicao);
        using var resposta = await api.Client.SendAsync(requisicao);
        return resposta.StatusCode;
    }

    [Fact]
    public async Task Fora_do_Development_a_plataforma_vem_desligada_e_toda_rota_responde_404_inclusive_o_login()
    {
        await using var api = await ApiServer.StartAsync("Production", Producao());

        foreach (var rota in RotasDaPlataforma)
        {
            Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Get, rota));
            Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Post, rota));
            Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Get, rota.ToUpperInvariant()));
        }
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Ambientes_que_nao_sao_Development_nao_ligam_a_plataforma_sozinhos(string ambiente)
    {
        await using var api = await ApiServer.StartAsync(ambiente, Producao());

        Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Post, "/platform/auth/login"));
    }

    [Fact]
    public async Task Em_Development_a_plataforma_vem_ligada_por_padrao()
    {
        await using var api = await ApiServer.StartAsync("Development", Desenvolvimento());

        // Credencial inválida: a rota existe e recusa o login (nunca 404).
        ExisteERecusa(await ChamarAsync(api, HttpMethod.Post, "/platform/auth/login"));
        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(api, HttpMethod.Get, "/platform/companies"));
    }

    [Fact]
    public async Task Development_com_a_chave_desligada_responde_404()
    {
        await using var api = await ApiServer.StartAsync("Development", Desenvolvimento("false"));

        foreach (var rota in RotasDaPlataforma)
        {
            Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Post, rota));
        }
    }

    [Fact]
    public async Task Fora_do_Development_a_chave_explicita_liga_a_plataforma()
    {
        await using var api = await ApiServer.StartAsync("Production", Producao("true"));

        ExisteERecusa(await ChamarAsync(api, HttpMethod.Post, "/platform/auth/login"));
    }

    [Fact]
    public async Task Ligar_a_plataforma_nao_afrouxa_o_ingresso_publico()
    {
        // Com a porta do ingresso público configurada (como no túnel), o guarda age; o teste escuta em outra porta local, então só o rastro do túnel dispara a defesa.
        var configuracao = Producao("true");
        configuracao["Channels:Webhook:PublicListenerPort"] = "5156";
        await using var api = await ApiServer.StartAsync("Production", configuracao);

        // O rastro de um túnel (o ingresso público) só alcança o webhook: a plataforma continua 404 para ele, ligada ou não.
        foreach (var cabecalho in new[] { "X-Forwarded-For", "Cf-Ray", "Cf-Connecting-Ip" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Post, "/platform/auth/login", ajusta: r => r.Headers.Add(cabecalho, "203.0.113.9")));
            Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(api, HttpMethod.Get, "/platform/companies", ajusta: r => r.Headers.Add(cabecalho, "203.0.113.9")));
        }

        // Sem o rastro, a mesma rota existe (a chave está ligada): o 404 de cima vem do guarda do ingresso, não da ausência da rota.
        ExisteERecusa(await ChamarAsync(api, HttpMethod.Post, "/platform/auth/login"));
    }

    [Fact]
    public async Task Empresas_contratantes_continuam_funcionando_com_a_plataforma_desligada()
    {
        await using var api = await ApiServer.StartAsync("Production", Producao());

        Assert.Equal(HttpStatusCode.OK, await ChamarAsync(api, HttpMethod.Get, "/health"));

        // O login das empresas existe e responde pelas próprias regras (empresa desconhecida: recusa, nunca 404 de rota).
        using var login = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = "empresa-inexistente", email = "a@b.c", password = "x" });
        Assert.NotEqual(HttpStatusCode.NotFound, login.StatusCode);
        Assert.True((int)login.StatusCode < 500, $"status {(int)login.StatusCode}");
    }

    [Fact]
    public async Task Sessao_de_plataforma_que_sobrou_nao_alcanca_nada_quando_a_plataforma_e_desligada()
    {
        var plataforma = await PlataformaAsync();
        var email = $"op{Guid.NewGuid().ToString("N")[..8]}@plataforma.local";
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(plataforma)))
        {
            var sp = escopo.ServiceProvider;
            var agora = DateTimeOffset.UtcNow;
            var usuario = User.Create(plataforma.TenantId, email, "Operador de teste", sp.GetRequiredService<PasswordHasher>().Hash(PostgresFixture.PasswordPadrao), agora);
            await sp.GetRequiredService<IUserRepository>().AddAsync(usuario, UserOrganizationGrant.Create(plataforma.TenantId, usuario.Id, null, Role.PlatformViewer, agora, null), default);
        }

        string token;
        await using (var ligada = await ApiServer.StartAsync("Development", Desenvolvimento()))
        {
            using var resposta = await ligada.Client.PostAsJsonAsync("/platform/auth/login", new { email, password = PostgresFixture.PasswordPadrao });
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            token = (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.OK, await ChamarAsync(ligada, HttpMethod.Get, "/platform/companies", token));
        }

        await using var desligada = await ApiServer.StartAsync("Development", Desenvolvimento("false"));
        Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(desligada, HttpMethod.Get, "/platform/companies", token));
        Assert.Equal(HttpStatusCode.NotFound, await ChamarAsync(desligada, HttpMethod.Get, "/auth/me", token));
    }

    private async Task<IaBimeta.Tenancy.Contracts.TenantInfo> PlataformaAsync()
    {
        await using var escopo = banco.NovoEscopo();
        return await escopo.ServiceProvider.GetRequiredService<TenantAdministrationService>().EnsurePlatformTenantAsync("Plataforma BImeta (teste)");
    }
}
