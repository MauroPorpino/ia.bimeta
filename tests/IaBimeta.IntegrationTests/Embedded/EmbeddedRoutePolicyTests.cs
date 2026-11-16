using System.Net;
using System.Text.RegularExpressions;
using IaBimeta.Api.Embedded;
using IaBimeta.IntegrationTests.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// O teste de ARQUITETURA da lista fechada de rotas da sessão embutida (ADR 0039, seção 9). Quatro travas: (1) a lista é exatamente a aprovada (ampliar exige editar este teste, visível na revisão e no ADR);
/// (2) nunca é vazia nem contém rota administrativa; (3) toda rota da lista existe de fato na API (nada de padrão morto); (4) TODA rota da API que não está na lista responde 403 para a sessão embutida, existindo ou não,
/// sem nunca chegar ao endpoint.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedRoutePolicyTests(PostgresFixture banco)
{
    /// <summary>A lista aprovada (E1). Para ampliar: ADR, atualização deste texto e revisão da segurança.</summary>
    private static readonly string[] ListaAprovada =
    {
        "GET /auth/me",
        "POST /auth/logout",
        "GET /contact-center/me/queues",
        "GET /contact-center/me/presence",
        "PUT /contact-center/me/presence",
        "POST /contact-center/queues/{guid}/claim-next",
        "GET /contact-center/items",
        "GET /contact-center/items/{guid}",
        "GET /contact-center/items/{guid}/history",
        "GET /contact-center/items/{guid}/conversation",
        "GET /contact-center/items/{guid}/messages",
        "GET /contact-center/items/{guid}/messages/{guid}/attachment",
        "GET /contact-center/items/{guid}/reply-options",
        "POST /contact-center/items/{guid}/claim",
        "POST /contact-center/items/{guid}/release",
        "POST /contact-center/items/{guid}/transfer",
        "POST /contact-center/items/{guid}/return-to-ai",
        "POST /contact-center/items/{guid}/reply",
        "POST /contact-center/items/{guid}/reply-template",
        "POST /contact-center/items/{guid}/reply-media",
        "POST /contact-center/items/{guid}/close",
        "GET /embed/v1/session",
        "POST /embed/v1/context",
    };

    /// <summary>O que NUNCA pode estar na lista: administração, estrutura, usuários, canais, integrações, fluxos, conhecimento, auditoria, senha, simulador e o que é anônimo.</summary>
    private static readonly string[] PrefixosProibidos =
    {
        "/organizations", "/tenants", "/audit", "/channels", "/capabilities", "/workflows", "/agents", "/knowledge", "/conversations", "/embedded",
        "/auth/me/password", "/auth/password-reset", "/auth/login", "/webhooks", "/dev", "/health",
        "/contact-center/queues/{guid}/members", "/contact-center/routing-rules", "/contact-center/handoffs", "/contact-center/routing/preview",
    };

    // Rotas que o host trata SEM contexto de usuário: a sessão embutida não passa por elas (o guarda só age com contexto).
    private static readonly string[] RotasSemContexto =
    {
        "/health", "/auth/login", "/auth/password-reset", "/webhooks", "/embed/v1/launch-tickets", "/embed/v1/context-tickets", "/embed/v1/sessions/revoke", "/embed/v1/exchange",
    };

    // O padrão do grupo termina em "/" quando a rota é a raiz do grupo (/contact-center/items/): a lista usa a forma sem a barra final.
    private static string Normalizar(string padrao)
    {
        var sem = Regex.Replace(padrao, @"\{\w+:guid\}", "{guid}");
        return sem.Length > 1 ? sem.TrimEnd('/') : sem;
    }

    [Fact]
    public void A_lista_de_rotas_e_exatamente_a_aprovada()
    {
        var atual = EmbeddedRoutePolicy.Allowed.Select(r => r.ToString()).ToArray();

        Assert.Equal(ListaAprovada, atual);
    }

    [Fact]
    public void A_lista_nao_e_vazia_nao_tem_duplicata_e_nenhuma_rota_e_administrativa()
    {
        Assert.NotEmpty(EmbeddedRoutePolicy.Allowed);
        Assert.Equal(EmbeddedRoutePolicy.Allowed.Count, EmbeddedRoutePolicy.Allowed.Select(r => r.ToString()).Distinct().Count());

        foreach (var rota in EmbeddedRoutePolicy.Allowed)
        {
            Assert.All(PrefixosProibidos, proibido => Assert.False(
                rota.Pattern.StartsWith(proibido, StringComparison.OrdinalIgnoreCase), $"A rota '{rota}' começa por um prefixo proibido: {proibido}"));
            Assert.Contains(rota.Method, new[] { "GET", "POST", "PUT" });
            Assert.DoesNotContain("*", rota.Pattern);
        }
    }

    [Theory]
    [InlineData("GET", "/auth/me", true)]
    [InlineData("get", "/AUTH/me", true)]
    [InlineData("GET", "/auth/me/", true)]
    [InlineData("DELETE", "/auth/me", false)]
    [InlineData("PUT", "/auth/me", false)]
    [InlineData("PUT", "/auth/me/password", false)]
    [InlineData("PUT", "/auth/me/locale", false)]
    [InlineData("GET", "/auth/me/../organizations", false)]
    [InlineData("GET", "//auth/me", false)]
    [InlineData("GET", "/contact-center/items/not-a-guid", false)]
    [InlineData("GET", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301", true)]
    [InlineData("GET", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/extra", false)]
    [InlineData("POST", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/assign", false)]
    [InlineData("POST", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/priority", false)]
    [InlineData("POST", "/contact-center/queues/3f2504e0-4f89-11d3-9a0c-0305e82c3301/claim-next", true)]
    [InlineData("POST", "/contact-center/queues/3f2504e0-4f89-11d3-9a0c-0305e82c3301/pause", false)]
    [InlineData("GET", "/organizations", false)]
    [InlineData("GET", "/embedded/connections", false)]
    [InlineData("GET", "", false)]
    [InlineData("", "/auth/me", false)]
    public void IsAllowed_aplica_a_lista_com_metodo_caminho_e_sem_atalhos(string metodo, string caminho, bool esperado)
    {
        Assert.Equal(esperado, EmbeddedRoutePolicy.IsAllowed(metodo, caminho));
    }

    /// <summary>E1-R: o subconjunto que o modo CustomerOnly alcança é EXATAMENTE a própria sessão e o resgate do contexto; ampliá-lo exige editar este teste (e a E2 trará a autorização por cliente).</summary>
    [Fact]
    public void O_subconjunto_da_lista_para_CustomerOnly_e_exatamente_a_propria_sessao_e_o_resgate_do_contexto()
    {
        var alcancadas = EmbeddedRoutePolicy.Allowed
            .Where(r => EmbeddedRoutePolicy.IsAllowed(r.Method, r.Pattern.Replace("{guid}", Guid.NewGuid().ToString()), customerOnly: true))
            .Select(r => r.ToString())
            .ToArray();

        Assert.Equal(new[] { "GET /auth/me", "POST /auth/logout", "GET /embed/v1/session", "POST /embed/v1/context" }, alcancadas);
        Assert.All(EmbeddedRoutePolicy.Allowed, r => Assert.True(EmbeddedRoutePolicy.IsAllowed(r.Method, r.Pattern.Replace("{guid}", Guid.NewGuid().ToString()), customerOnly: false)));
        Assert.False(EmbeddedRoutePolicy.IsAllowed("GET", "/contact-center/items", customerOnly: true));
        Assert.False(EmbeddedRoutePolicy.IsAllowed(null, "/auth/me", customerOnly: true));
    }

    [Fact]
    public async Task Toda_rota_da_lista_existe_na_api_e_toda_rota_fora_dela_responde_403_para_a_sessao_embutida()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "rotas");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-rotas");

        var endpoints = c.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" }).Select(m => (Metodo: m, Padrao: e.RoutePattern)))
            .Where(x => x.Metodo != "OPTIONS" && x.Metodo != "HEAD")
            .ToList();
        Assert.True(endpoints.Count > 100, "A API deveria ter mais de 100 rotas: a varredura estaria vazia.");

        // (3) toda rota da lista existe.
        var existentes = endpoints.Select(e => $"{e.Metodo} {Normalizar(e.Padrao.RawText!)}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rota in EmbeddedRoutePolicy.Allowed)
        {
            Assert.True(existentes.Contains(rota.ToString()), $"A rota da lista não existe na API: {rota}");
        }

        // (4) toda rota fora da lista é barrada ANTES do endpoint.
        var varridas = 0;
        foreach (var (metodo, padrao) in endpoints)
        {
            var texto = padrao.RawText!;
            if (RotasSemContexto.Any(p => texto.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || EmbeddedRoutePolicy.Allowed.Any(r => string.Equals(r.ToString(), $"{metodo} {Normalizar(texto)}", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var caminho = Regex.Replace(texto, @"\{[^}]*\}", m => m.Value.Contains(":guid", StringComparison.Ordinal) ? Guid.NewGuid().ToString() : "x");
            using var resposta = await c.Api.EnviarAsync(troca.Token, new HttpMethod(metodo), caminho);
            Assert.True(resposta.StatusCode == HttpStatusCode.Forbidden, $"{metodo} {texto} deveria responder 403 à sessão embutida, respondeu {(int)resposta.StatusCode}");
            Assert.Equal("embedded-route-forbidden", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
            varridas++;
        }

        Assert.True(varridas > 80, $"A varredura cobriu poucas rotas ({varridas}).");
    }

    [Theory]
    [InlineData("/rota-que-nao-existe")]
    [InlineData("/organizations")]
    [InlineData("/embedded/connections")]
    [InlineData("/capabilities/connections")]
    [InlineData("/workflows/definitions")]
    [InlineData("/knowledge")]
    [InlineData("/audit/events")]
    [InlineData("/auth/me/password")]
    public async Task Rota_inexistente_ou_administrativa_responde_o_mesmo_403_a_sessao_embutida_e_nao_revela_a_superficie(string caminho)
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "superficie");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-superficie");

        foreach (var metodo in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var resposta = await c.Api.EnviarAsync(troca.Token, metodo, caminho);
            Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
            Assert.Equal("embedded-route-forbidden", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
        }
    }

    [Fact]
    public async Task A_sessao_interativa_continua_igual_e_o_administrador_alcanca_a_administracao()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "interativa");
        await using var servidor = c.Api;

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Get, "/embedded/connections"));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.NotFound, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Get, "/rota-que-nao-existe"));
    }

    [Fact]
    public async Task O_guarda_nao_atua_sem_contexto_e_o_token_embutido_nao_abre_as_rotas_anonimas_de_outra_forma()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "anonimas");
        await using var servidor = c.Api;

        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(null, HttpMethod.Get, "/health"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(null, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync("ibs_" + new string('a', 43), HttpMethod.Get, "/embed/v1/session"));
    }
}
