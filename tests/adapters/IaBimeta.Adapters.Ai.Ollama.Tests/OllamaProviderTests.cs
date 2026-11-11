using System.Net;
using System.Text;
using System.Text.Json;
using IaBimeta.Knowledge.Contracts;

namespace IaBimeta.Adapters.Ai.Ollama.Tests;

/// <summary>Testes do adaptador contra um serviço HTTP SIMULADO. NÃO equivalem a homologar o Ollama real (qualidade do Qwen em pt-BR, latência e memória são validados na VM).</summary>
public sealed class OllamaProviderTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

        public List<(string Path, string Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var corpo = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, corpo, request.Headers.Authorization?.ToString()));
            return Respond(request, corpo);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static OllamaProvider Build(HttpMessageHandler handler,Action<OllamaOptions>? ajusta = null, string? token = null)
    {
        var opcoes = new OllamaOptions { BaseUrl = "http://ollama.test:11434", EmbeddingDimension = 4, GenerationModelId = "qwen3:1.7b", BearerToken = token };
        ajusta?.Invoke(opcoes);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test:11434/") };
        if (token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return new OllamaProvider(http, opcoes);
    }

    [Fact]
    public async Task Embed_envia_o_modelo_e_os_textos_e_normaliza_o_vetor()
    {
        var h = new Handler { Respond = (_, _) => Json("{\"embeddings\":[[3,4,0,0],[0,0,0,2]]}") };
        var vetores = await Build(h).EmbedAsync(["a", "b"], EmbeddingPurpose.Document);

        Assert.Equal(2, vetores.Count);
        Assert.Equal(0.6f, vetores[0][0], 4);
        Assert.Equal(0.8f, vetores[0][1], 4);
        Assert.Equal(1f, vetores[1][3], 4);
        var (caminho, corpo, _) = Assert.Single(h.Requests);
        Assert.Equal("/api/embed", caminho);
        using var doc = JsonDocument.Parse(corpo);
        Assert.Equal("qwen3-embedding:0.6b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("a", doc.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task Consulta_leva_a_instrucao_e_documento_nao()
    {
        var h = new Handler { Respond = (_, _) => Json("{\"embeddings\":[[1,0,0,0]]}") };
        var p = Build(h);
        await p.EmbedAsync(["jejum?"], EmbeddingPurpose.Query);
        await p.EmbedAsync(["jejum?"], EmbeddingPurpose.Document);
        Assert.Contains("Instruct:", h.Requests[0].Body);
        Assert.Contains("Query: jejum?", h.Requests[0].Body);
        Assert.DoesNotContain("Instruct:", h.Requests[1].Body);
    }

    [Fact]
    public async Task Envia_o_token_quando_configurado()
    {
        var h = new Handler { Respond = (_, _) => Json("{\"embeddings\":[[1,0,0,0]]}") };
        await Build(h, token: "segredo-de-teste").EmbedAsync(["a"], EmbeddingPurpose.Document);
        Assert.Equal("Bearer segredo-de-teste", h.Requests[0].Auth);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "unauthorized")]
    [InlineData(HttpStatusCode.InternalServerError, "http-500")]
    [InlineData(HttpStatusCode.NotFound, "http-404")]
    public async Task Resposta_de_erro_vira_codigo_curto_sem_corpo(HttpStatusCode status, string codigo)
    {
        var h = new Handler { Respond = (_, _) => Json("{\"error\":\"texto interno com a pergunta do cliente\"}", status) };
        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => Build(h).EmbedAsync(["a"], EmbeddingPurpose.Document));
        Assert.Equal(codigo, ex.Code);
        Assert.DoesNotContain("cliente", ex.Message);
    }

    [Theory]
    [InlineData("{}", "invalid-response")]
    [InlineData("{\"embeddings\":[[1,0,0,0]]}", "invalid-response")] // um vetor para dois textos
    [InlineData("{\"embeddings\":[[1,0],[1,0]]}", "dimension-mismatch")]
    [InlineData("{\"embeddings\":[[0,0,0,0],[1,0,0,0]]}", "invalid-response")] // vetor nulo
    [InlineData("não é json", "invalid-response")]
    public async Task Resposta_invalida_vira_codigo_curto(string corpo, string codigo)
    {
        var h = new Handler { Respond = (_, _) => Json(corpo) };
        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => Build(h).EmbedAsync(["a", "b"], EmbeddingPurpose.Document));
        Assert.Equal(codigo, ex.Code);
    }

    [Fact]
    public async Task Servico_inacessivel_vira_unreachable()
    {
        var h = new Handler { Respond = (_, _) => throw new HttpRequestException("recusado") };
        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => Build(h).EmbedAsync(["a"], EmbeddingPurpose.Document));
        Assert.Equal("unreachable", ex.Code);
    }

    [Fact]
    public async Task Tempo_esgotado_vira_timeout_mas_cancelamento_do_chamador_propaga()
    {
        var p =Build(new SlowHandler(), o => o.EmbeddingTimeout = TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => p.EmbedAsync(["a"], EmbeddingPurpose.Document));
        Assert.Equal("timeout", ex.Code);

        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(new SlowHandler()).EmbedAsync(["a"], EmbeddingPurpose.Document, cancelado.Token));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return Json("{\"embeddings\":[[1,0,0,0]]}");
        }
    }

    [Fact]
    public async Task Lista_vazia_nao_chama_o_servico()
    {
        var h = new Handler();
        Assert.Empty(await Build(h).EmbedAsync([], EmbeddingPurpose.Document));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Geracao_pede_json_estruturado_sem_stream_e_devolve_o_conteudo()
    {
        var h = new Handler { Respond = (_, _) => Json("{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"grupo\\\":1}\"}}") };
        var r = await Build(h).GenerateJsonAsync(new LocalGenerationRequest("sistema", "usuario", "{\"type\":\"object\"}"));
        Assert.Equal("{\"grupo\":1}", r.Json);
        using var doc = JsonDocument.Parse(h.Requests[0].Body);
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("qwen3:1.7b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("object", doc.RootElement.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("system", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("options").GetProperty("temperature").GetInt32());
    }

    [Theory]
    [InlineData("{\"message\":{\"content\":\"isto não é json\"}}", "invalid-response")]
    [InlineData("{\"message\":{}}", "invalid-response")]
    [InlineData("{}", "invalid-response")]
    public async Task Geracao_com_saida_invalida_vira_codigo_curto(string corpo, string codigo)
    {
        var h = new Handler { Respond = (_, _) => Json(corpo) };
        var ex = await Assert.ThrowsAsync<LocalGenerationUnavailableException>(() => Build(h).GenerateJsonAsync(new LocalGenerationRequest("s", "u", "{}")));
        Assert.Equal(codigo, ex.Code);
    }

    [Fact]
    public async Task Geracao_sem_modelo_configurado_e_nao_configurada_e_nao_chama_o_servico()
    {
        var h = new Handler();
        var ex = await Assert.ThrowsAsync<LocalGenerationUnavailableException>(() => Build(h, o => o.GenerationModelId = null).GenerateJsonAsync(new LocalGenerationRequest("s", "u", "{}")));
        Assert.Equal("not-configured", ex.Code);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Geracao_com_esquema_invalido_nao_chama_o_servico()
    {
        var h = new Handler();
        var ex = await Assert.ThrowsAsync<LocalGenerationUnavailableException>(() => Build(h).GenerateJsonAsync(new LocalGenerationRequest("s", "u", "{nao")));
        Assert.Equal("invalid-schema", ex.Code);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Opcoes_invalidas_sao_recusadas()
    {
        Assert.Throws<InvalidOperationException>(() => new OllamaOptions { BaseUrl = "ftp://x" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OllamaOptions { EmbeddingModelId = " " }.Validate());
        new OllamaOptions().Validate();
    }
}
