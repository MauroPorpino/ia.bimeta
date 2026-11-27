using System.Net;
using System.Text;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.Adapters.Ai.OpenAI;

namespace IaBimeta.Adapters.Ai.OpenAI.Tests;

/// <summary>A verificação de conectividade do adaptador (Etapa 05) contra um servidor HTTP SIMULADO: só GET do modelo, sem geração, sem vazar a chave nem o endereço.</summary>
public sealed class OpenAiConnectivityTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Behavior { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<(HttpMethod Method, string Url, string? Auth, string? Version)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("anthropic-version", out var v) ? v.First() : null));
            return await Behavior(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (IAiProviderDiagnostics Provider, Handler Handler) Build()
    {
        var handler = new Handler();
        return (new OpenAiProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new OpenAiOptions()), handler);
    }

    private static AiConnectivityRequest Pedido(string chave = "sk-canario-123", string modelo = "modelo-x") => new(modelo, new AiCredential(chave), TimeSpan.FromSeconds(2));

    private const string Ok = "{\"id\":\"modelo-x\",\"object\":\"model\",\"type\":\"model\",\"lifecycle\":\"active\"}";

    [Fact]
    public async Task O_modelo_existente_e_verificado_por_um_GET_com_a_credencial_so_no_cabecalho()
    {
        var (p, h) = Build();
        h.Behavior = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Ok));
        var r = await p.CheckConnectivityAsync(Pedido(), default);

        Assert.Equal(AiConnectivityState.Verified, r.State);
        Assert.True(r.Reachable == true && r.Authenticated == true && r.ModelAvailable == true);
        Assert.Null(r.ConversationCapable); // o provedor não declara capacidade de conversa: nada é inventado
        var req = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.EndsWith("/models/modelo-x", req.Url);
        Assert.Equal("Bearer sk-canario-123", req.Auth);
        Assert.DoesNotContain("sk-canario-123", req.Url);
    }

    [Theory]
    [InlineData(401, AiConnectivityState.AuthenticationFailed, "unauthorized")]
    [InlineData(404, AiConnectivityState.ModelUnavailable, "model-not-found")]
    [InlineData(403, AiConnectivityState.Inconclusive, "forbidden")]
    [InlineData(429, AiConnectivityState.Inconclusive, "rate-limited")]
    [InlineData(503, AiConnectivityState.Inconclusive, "server-error")]
    public async Task Os_estados_http_viram_resultados_conservadores(int status, AiConnectivityState estado, string codigo)
    {
        var (p, h) = Build();
        h.Behavior = (_, _) => Task.FromResult(Json((HttpStatusCode)status, "{\"error\":{\"message\":\"texto do servidor com sk-canario-123\"}}"));
        var r = await p.CheckConnectivityAsync(Pedido(), default);
        Assert.Equal((estado, codigo), (r.State, r.Code));
        Assert.NotEqual(AiConnectivityState.Verified, r.State);
    }

    [Fact]
    public async Task Resposta_de_outro_modelo_ou_invalida_e_inconclusiva()
    {
        var (p, h) = Build();
        h.Behavior = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":\"outro\"}"));
        Assert.Equal(AiConnectivityState.Inconclusive, (await p.CheckConnectivityAsync(Pedido(), default)).State);
        h.Behavior = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "isto nao e json"));
        Assert.Equal(AiConnectivityState.Inconclusive, (await p.CheckConnectivityAsync(Pedido(), default)).State);
    }

    [Fact]
    public async Task Rede_e_tempo_esgotado_viram_inalcancavel_sem_vazar_o_endereco_e_o_cancelamento_do_chamador_sai_como_excecao()
    {
        var (p, h) = Build();
        h.Behavior = (_, _) => throw new HttpRequestException("falha em http://10.1.2.3/models");
        var rede = await p.CheckConnectivityAsync(Pedido(), default);
        Assert.Equal((AiConnectivityState.Unreachable, "network-error"), (rede.State, rede.Code));
        Assert.DoesNotContain("10.1.2.3", rede.Code);

        h.Behavior = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, Ok);
        };
        var tempo = await p.CheckConnectivityAsync(Pedido() with { Timeout = TimeSpan.FromMilliseconds(80) }, default);
        Assert.Equal((AiConnectivityState.Unreachable, "timeout"), (tempo.State, tempo.Code));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.CheckConnectivityAsync(Pedido() with { Timeout = TimeSpan.FromSeconds(30) }, cts.Token));
    }

    [Fact]
    public async Task Credencial_fora_do_formato_nunca_vai_a_um_cabecalho()
    {
        var (p, h) = Build();
        var r = await p.CheckConnectivityAsync(Pedido(chave: "chave com espaco\r\nX: y"), default);
        Assert.Equal(AiConnectivityState.AuthenticationFailed, r.State);
        Assert.Empty(h.Requests);
    }
}

