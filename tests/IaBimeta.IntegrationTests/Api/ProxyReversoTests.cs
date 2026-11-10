using System.Net;
using System.Net.Http.Json;
using IaBimeta.Api.Proxy;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O Api atrás de um proxy reverso (ADR 0033), por HTTP de verdade: com a confiança nos cabeçalhos de encaminhamento ligada, o limite de login por origem vale por CLIENTE (o endereço do <c>X-Forwarded-For</c>) e não pelo
/// endereço do proxy; o cabeçalho de uma origem que NÃO é um proxy confiável é ignorado (o cliente não escolhe o próprio endereço); desligado (o padrão) nada muda; e a configuração contraditória impede a partida.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProxyReversoTests
{
    private const string ClienteA = "203.0.113.10";
    private const string ClienteB = "203.0.113.11";

    private static Dictionary<string, string?> Configuracao(string[] proxies, bool ligado = true, string[]? redes = null)
    {
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:LoginRateLimit:PermitLimit"] = "3";
        configuracao["Identity:LoginRateLimit:WindowSeconds"] = "60";
        configuracao["ForwardedHeaders:Enabled"] = ligado ? "true" : "false";
        for (var i = 0; i < proxies.Length; i++)
        {
            configuracao[$"ForwardedHeaders:KnownProxies:{i}"] = proxies[i];
        }

        for (var i = 0; i < (redes?.Length ?? 0); i++)
        {
            configuracao[$"ForwardedHeaders:KnownNetworks:{i}"] = redes![i];
        }

        return configuracao;
    }

    /// <summary>Uma tentativa de login (de uma empresa que não existe: só o limite por origem age, antes de qualquer consulta ao banco), vinda do cliente informado pelo cabeçalho do proxy.</summary>
    private static async Task<HttpStatusCode> TentarAsync(ApiServer api, string? clienteDoCabecalho)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new { tenant = "empresa-que-nao-existe", email = $"{Guid.NewGuid():N}@teste.local", password = "senha-qualquer-123" }),
        };
        if (clienteDoCabecalho is not null)
        {
            pedido.Headers.TryAddWithoutValidation("X-Forwarded-For", clienteDoCabecalho);
            pedido.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        }

        using var resposta = await api.Client.SendAsync(pedido);
        return resposta.StatusCode;
    }

    private static async Task EsgotarAsync(ApiServer api, string cliente)
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await TentarAsync(api, cliente));
        }
    }

    [Fact]
    public async Task Atras_de_um_proxy_confiavel_o_limite_de_login_vale_por_cliente_e_nao_pelo_endereco_do_proxy()
    {
        await using var api = await ApiServer.StartAsync("Production", Configuracao(["127.0.0.1"]));

        await EsgotarAsync(api, ClienteA);

        Assert.Equal(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteA)); // o cliente A passou do limite
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteB)); // o cliente B, no mesmo proxy, tem o limite dele
    }

    [Fact]
    public async Task A_rede_confiavel_em_CIDR_vale_como_o_endereco_do_proxy()
    {
        await using var api = await ApiServer.StartAsync("Production", Configuracao([], redes: ["127.0.0.0/8"]));

        await EsgotarAsync(api, ClienteA);

        Assert.Equal(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteB));
    }

    [Fact]
    public async Task Origem_que_nao_e_proxy_confiavel_nao_escolhe_o_proprio_endereco_o_cabecalho_e_ignorado()
    {
        // O proxy confiável é OUTRO endereço: a conexão (127.0.0.1) não é confiável, então o X-Forwarded-For dela vale nada.
        await using var api = await ApiServer.StartAsync("Production", Configuracao(["10.255.255.1"]));

        await EsgotarAsync(api, ClienteA);

        Assert.Equal(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteA));
        Assert.Equal(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteB)); // mudar o cabeçalho não escapa do limite
    }

    [Fact]
    public async Task Desligado_o_padrao_o_cabecalho_nao_muda_nada_e_todos_dividem_o_endereco_da_conexao()
    {
        await using var api = await ApiServer.StartAsync("Production", Configuracao([], ligado: false));

        await EsgotarAsync(api, ClienteA);

        Assert.Equal(HttpStatusCode.TooManyRequests, await TentarAsync(api, ClienteB));
    }

    [Fact]
    public void A_configuracao_contraditoria_ou_invalida_impede_a_partida_sem_repetir_o_valor()
    {
        // Ligado sem nenhum endereço confiável: nenhum cabeçalho seria confiável.
        Assert.Throws<InvalidOperationException>(() => new ForwardedHeadersSettings { Enabled = true }.Validate());

        var proxyInvalido = Assert.Throws<InvalidOperationException>(() => new ForwardedHeadersSettings { Enabled = true, KnownProxies = ["isto-nao-e-ip"] }.Validate());
        Assert.DoesNotContain("isto-nao-e-ip", proxyInvalido.Message, StringComparison.Ordinal);
        var redeInvalida = Assert.Throws<InvalidOperationException>(() => new ForwardedHeadersSettings { Enabled = true, KnownNetworks = ["172.29.89.0/99"] }.Validate());
        Assert.DoesNotContain("172.29.89.0/99", redeInvalida.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new ForwardedHeadersSettings { Enabled = true, KnownProxies = ["127.0.0.1"], ForwardLimit = 0 }.Validate());

        // Desligado, nada é validado; ligado e correto, passa.
        new ForwardedHeadersSettings { Enabled = false, KnownProxies = ["lixo"] }.Validate();
        new ForwardedHeadersSettings { Enabled = true, KnownProxies = ["172.29.89.2"], KnownNetworks = ["172.29.89.0/24"] }.Validate();
    }

    [Fact]
    public async Task O_Api_nao_sobe_com_o_encaminhamento_ligado_e_sem_nenhum_endereco_confiavel()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiServer.StartAsync("Production", Configuracao([])));
    }
}
