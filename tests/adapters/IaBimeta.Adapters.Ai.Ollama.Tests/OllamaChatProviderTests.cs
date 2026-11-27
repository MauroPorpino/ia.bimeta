using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Ollama.Tests;

/// <summary>Testes do adaptador CONVERSACIONAL do Ollama (Etapa 04) contra um servidor HTTP SIMULADO. NÃO provam compatibilidade com o Ollama real (formato lido na documentação oficial).</summary>
public sealed class OllamaChatProviderTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"}},\"required\":[\"action\"],\"additionalProperties\":false}";

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> Behavior { get; set; } = (_, _, _) => Task.FromResult(Ok("Olá"));

        private readonly object _gate = new();
        private readonly List<(Uri Uri, string Body, string? Auth)> _requests = [];
        private int _emVoo;

        public List<(Uri Uri, string Body, string? Auth)> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToList();
                }
            }
        }

        /// <summary>O maior número de requisições que estiveram ao mesmo tempo dentro do servidor simulado.</summary>
        public int PicoSimultaneo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var corpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                _requests.Add((request.RequestUri!, corpo, request.Headers.Authorization?.ToString()));
                _emVoo++;
                PicoSimultaneo = Math.Max(PicoSimultaneo, _emVoo);
            }

            try
            {
                return await Behavior(request, corpo, cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _emVoo--;
                }
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (n, v) in headers)
        {
            r.Headers.TryAddWithoutValidation(n, v);
        }

        return r;
    }

    private static HttpResponseMessage Ok(string text, int prompt = 120, int eval = 30, string reason = "stop", string? thinking = "raciocinio interno secreto") =>
        Json(HttpStatusCode.OK, OkBody(text, prompt, eval, reason, thinking));

    private static string OkBody(string text, int? prompt = 120, int? eval = 30, string? reason = "stop", string? thinking = null, bool done = true)
    {
        var mensagem = new JsonObject { ["role"] = "assistant", ["content"] = text };
        if (thinking is not null)
        {
            mensagem["thinking"] = thinking;
        }

        var corpo = new JsonObject { ["model"] = "qwen3:4b", ["message"] = mensagem, ["done"] = done };
        if (reason is not null)
        {
            corpo["done_reason"] = reason;
        }

        if (prompt is not null)
        {
            corpo["prompt_eval_count"] = prompt;
        }

        if (eval is not null)
        {
            corpo["eval_count"] = eval;
        }

        return corpo.ToJsonString();
    }

    private static AiCompletionRequest Request(
        int maxOutput = 300, AiOutputMode mode = AiOutputMode.Text, string profile = "atendimento-local", string system = "Você é a atendente.", IReadOnlyList<AiMessage>? messages = null,
        string model = "qwen3:4b", TimeSpan? timeout = null) =>
        new(Guid.NewGuid(), "turno-1", model, profile, system, messages ?? [new AiMessage(AiMessageRole.User, "Preciso de jejum?")], maxOutput, timeout ?? TimeSpan.FromSeconds(5), mode,
            mode == AiOutputMode.StructuredJson ? new AiStructuredOutput("agent_decision", Schema) : null, AiCredential.None);

    private static (OllamaChatProvider Provider, Handler Handler) Build(Action<OllamaChatOptions>? ajusta = null)
    {
        var opcoes = new OllamaChatOptions();
        ajusta?.Invoke(opcoes);
        var handler = new Handler();
        return (new OllamaChatProvider(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, opcoes), handler);
    }

    // Identidade e credencial

    [Fact]
    public void O_provedor_se_chama_ollama_e_nao_exige_credencial()
    {
        var (provedor, _) = Build();
        Assert.Equal("ollama", provedor.ProviderId);
        Assert.False(provedor.RequiresCredential);
        Assert.Equal(string.Empty, AiCredential.None.Secret);
    }

    // Construção do pedido

    [Fact]
    public async Task O_pedido_vai_a_api_chat_sem_streaming_com_sistema_historico_modelo_e_limites()
    {
        var (p, h) = Build();
        var mensagens = new[] { new AiMessage(AiMessageRole.User, "Oi"), new AiMessage(AiMessageRole.Assistant, "Olá!"), new AiMessage(AiMessageRole.User, "Tem jejum?") };
        var r = await p.CompleteAsync(Request(maxOutput: 250, messages: mensagens), default);

        Assert.True(r.IsSuccess);
        var (uri, corpo, auth) = Assert.Single(h.Requests);
        Assert.Equal("/api/chat", uri.AbsolutePath);
        Assert.Null(auth);
        var json = JsonNode.Parse(corpo)!;
        Assert.Equal("qwen3:4b", (string?)json["model"]);
        Assert.False((bool)json["stream"]!);
        Assert.Equal(250, (int)json["options"]!["num_predict"]!);
        Assert.Equal(8192, (int)json["options"]!["num_ctx"]!);
        Assert.Equal(0, (double)json["options"]!["temperature"]!);
        var ms = json["messages"]!.AsArray();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, ms.Select(m => (string?)m!["role"]).ToArray());
        Assert.Equal("Você é a atendente.", (string?)ms[0]!["content"]);
        Assert.Equal("Tem jejum?", (string?)ms[3]!["content"]);
        Assert.Null(json["format"]);
        Assert.Null(json["think"]);
        Assert.Null(json["tools"]);
        Assert.Null(json["keep_alive"]);
    }

    [Fact]
    public async Task A_saida_estruturada_vai_em_format_e_o_esquema_tambem_no_texto_do_sistema()
    {
        var (p, h) = Build();
        await p.CompleteAsync(Request(mode: AiOutputMode.StructuredJson), default);
        var json = JsonNode.Parse(h.Requests[0].Body)!;
        Assert.Equal("object", (string?)json["format"]!["type"]);
        Assert.Contains("\"action\"", (string?)json["messages"]![0]!["content"]);
    }

    [Fact]
    public async Task O_perfil_controla_contexto_temperatura_think_e_keep_alive()
    {
        var (p, h) = Build(o =>
        {
            o.Profiles["atendimento-local"] = new OllamaChatProfile { NumCtx = 4096, Temperature = 0.2, Think = false };
            o.KeepAlive = "10m";
        });
        await p.CompleteAsync(Request(), default);
        var json = JsonNode.Parse(h.Requests[0].Body)!;
        Assert.Equal(4096, (int)json["options"]!["num_ctx"]!);
        Assert.Equal(0.2, (double)json["options"]!["temperature"]!, 3);
        Assert.False((bool)json["think"]!);
        Assert.Equal("10m", (string?)json["keep_alive"]);
    }

    [Fact]
    public async Task Perfil_desconhecido_esquema_ausente_ou_invalido_e_teto_baixo_nao_enviam_nada()
    {
        var (p, h) = Build();
        var semPerfil = await p.CompleteAsync(Request(profile: "outro"), default);
        var semEsquema = await p.CompleteAsync(Request() with { OutputMode = AiOutputMode.StructuredJson, StructuredOutput = null }, default);
        var esquemaRuim = await p.CompleteAsync(Request() with { OutputMode = AiOutputMode.StructuredJson, StructuredOutput = new AiStructuredOutput("x", "{nao-json") }, default);
        var tetoZero = await p.CompleteAsync(Request(maxOutput: 0), default);

        Assert.All(new[] { semPerfil, semEsquema, esquemaRuim, tetoZero }, r =>
        {
            Assert.Equal(AiErrorKind.InvalidRequest, r.Error!.Kind);
            Assert.Equal(AiUsageCertainty.NoneIncurred, r.UsageCertainty);
        });
        Assert.Equal("profile-not-configured", semPerfil.Error!.Code);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Texto_com_substituto_utf16_sem_par_nao_derruba_a_chamada()
    {
        var (p, h) = Build();
        var r = await p.CompleteAsync(Request(messages: [new AiMessage(AiMessageRole.User, "a\uD800b")]), default);
        Assert.True(r.IsSuccess);
        Assert.Contains("a�b", (string?)JsonNode.Parse(h.Requests[0].Body)!["messages"]![1]!["content"]);
    }

    // Resposta

    [Fact]
    public async Task Resposta_textual_traz_os_tokens_reportados_e_nunca_o_raciocinio()
    {
        var (p, _) = Build();
        var r = await p.CompleteAsync(Request(), default);
        Assert.True(r.IsSuccess);
        Assert.Equal("Olá", r.Completion!.Text);
        Assert.Equal(AiStopReason.Completed, r.Completion.StopReason);
        Assert.Equal(new AiUsage(120, 0, 0, 30), r.Usage);
        Assert.Equal(AiUsageCertainty.Reported, r.UsageCertainty);
        Assert.DoesNotContain("secreto", r.Completion.Text);
    }

    [Fact]
    public async Task Blocos_think_no_texto_sao_removidos()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Ok("<think>pensando alto</think>\nResposta final", thinking: null));
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal("Resposta final", r.Completion!.Text);
    }

    [Fact]
    public async Task Saida_estruturada_valida_volta_como_texto_json()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Ok("{\"action\":\"reply\"}"));
        var r = await p.CompleteAsync(Request(mode: AiOutputMode.StructuredJson), default);
        Assert.Equal("{\"action\":\"reply\"}", r.Completion!.Text);
    }

    [Fact]
    public async Task Done_reason_length_e_truncamento_e_load_ou_desconhecido_nao_sao_resposta()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Ok("parcial", reason: "length"));
        var truncada = await p.CompleteAsync(Request(), default);
        Assert.Equal(AiStopReason.Truncated, truncada.Completion!.StopReason);

        h.Behavior = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, OkBody("", prompt: null, eval: null, reason: "load")));
        var carga = await p.CompleteAsync(Request(), default);
        Assert.Equal("model-load-only", carga.Error!.Code);
        Assert.Equal(AiUsageCertainty.Unknown, carga.UsageCertainty);

        h.Behavior = (_, _, _) => Task.FromResult(Ok("x", reason: "novo-motivo"));
        var novo = await p.CompleteAsync(Request(), default);
        Assert.Equal("unexpected-done-reason", novo.Error!.Code);
        Assert.Equal(AiErrorKind.InvalidRequest, novo.Error.Kind);
        Assert.Equal(AiUsageCertainty.Reported, novo.UsageCertainty);

        h.Behavior = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, OkBody("x", reason: null)));
        var semMotivo = await p.CompleteAsync(Request(), default);
        Assert.Equal("unexpected-done-reason", semMotivo.Error!.Code);
    }

    [Fact]
    public async Task Resposta_vazia_com_stop_e_falha_com_consumo_conhecido()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Ok("   "));
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal("empty-output", r.Error!.Code);
        Assert.Equal(AiErrorKind.Transient, r.Error.Kind);
        Assert.Equal(AiUsageCertainty.Reported, r.UsageCertainty);
        Assert.Equal(120, r.Usage.InputTokens);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(120, null)]
    public async Task Consumo_nao_informado_e_desconhecido_nunca_zero_e_a_resposta_e_descartada(int? prompt, int? eval)
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, OkBody("resposta", prompt, eval)));
        var r = await p.CompleteAsync(Request(), default);
        Assert.False(r.IsSuccess);
        Assert.Equal("missing-usage", r.Error!.Code);
        Assert.Equal(AiUsageCertainty.Unknown, r.UsageCertainty);
        Assert.Equal(AiUsage.None, r.Usage);
    }

    [Fact]
    public async Task Entrada_que_ocupa_toda_a_janela_indica_truncamento_silencioso_e_nao_e_usada()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Ok("resposta sobre prompt cortado", prompt: 8192));
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal(AiErrorKind.ContextTooLong, r.Error!.Kind);
        Assert.Equal(AiUsageCertainty.Reported, r.UsageCertainty);
    }

    [Theory]
    [InlineData("{\"model\":\"x\",\"message\":{\"role\":\"assistant\",\"content\":\"a\"},\"done\":false}")]
    [InlineData("{\"error\":\"falhou no meio\"}")]
    [InlineData("isto nao e json")]
    [InlineData("[1,2]")]
    public async Task Respostas_incompletas_ou_inesperadas_sao_falha_transitoria_de_consumo_desconhecido(string corpo)
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, corpo));
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal(AiErrorKind.Transient, r.Error!.Kind);
        Assert.Equal(AiUsageCertainty.Unknown, r.UsageCertainty);
    }

    [Fact]
    public async Task Corpo_acima_do_limite_e_resposta_invalida()
    {
        var (p, h) = Build(o => o.MaxResponseBytes = 1024);
        h.Behavior = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, OkBody(new string('a', 5000))));
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal("invalid-response", r.Error!.Code);
        Assert.Equal(AiUsageCertainty.Unknown, r.UsageCertainty);
    }

    // Erros HTTP

    [Theory]
    [InlineData(404, "{\"error\":\"model 'x' not found\"}", AiErrorKind.InvalidRequest, "model-not-installed", AiUsageCertainty.NoneIncurred)]
    [InlineData(400, "{\"error\":\"registry.ollama.ai/library/x does not support thinking\"}", AiErrorKind.InvalidRequest, "model-incompatible", AiUsageCertainty.NoneIncurred)]
    [InlineData(400, "{\"error\":\"invalid options\"}", AiErrorKind.InvalidRequest, "http-400", AiUsageCertainty.NoneIncurred)]
    [InlineData(401, "{\"error\":\"unauthorized\"}", AiErrorKind.InvalidCredentials, "unauthorized", AiUsageCertainty.NoneIncurred)]
    [InlineData(503, "{\"error\":\"server busy, please try again\"}", AiErrorKind.Transient, "server-busy", AiUsageCertainty.Unknown)]
    [InlineData(429, "{\"error\":\"rate limit\"}", AiErrorKind.Transient, "server-busy", AiUsageCertainty.Unknown)]
    [InlineData(500, "{\"error\":\"model requires more system memory (9.0 GiB) than is available (2.1 GiB)\"}", AiErrorKind.Transient, "insufficient-memory", AiUsageCertainty.Unknown)]
    [InlineData(500, "{\"error\":\"model failed to load\"}", AiErrorKind.Transient, "http-500", AiUsageCertainty.Unknown)]
    [InlineData(502, "bad gateway", AiErrorKind.Transient, "http-502", AiUsageCertainty.Unknown)]
    [InlineData(404, "nao e json", AiErrorKind.InvalidRequest, "model-not-installed", AiUsageCertainty.Unknown)]
    public async Task Erros_http_sao_normalizados_sem_repetir_o_texto_do_servidor(int status, string corpo, AiErrorKind tipo, string codigo, AiUsageCertainty certeza)
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Json((HttpStatusCode)status, corpo));
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal(tipo, r.Error!.Kind);
        Assert.Equal(codigo, r.Error.Code);
        Assert.Equal(certeza, r.UsageCertainty);
        Assert.DoesNotContain("GiB", r.Error.Code);
    }

    [Fact]
    public async Task Retry_after_so_vale_para_falha_transitoria()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Json((HttpStatusCode)503, "{\"error\":\"busy\"}", ("Retry-After", "7")));
        var ocupado = await p.CompleteAsync(Request(), default);
        Assert.Equal(TimeSpan.FromSeconds(7), ocupado.Error!.RetryAfter);

        h.Behavior = (_, _, _) => Task.FromResult(Json((HttpStatusCode)404, "{\"error\":\"nf\"}", ("Retry-After", "7")));
        var ausente = await p.CompleteAsync(Request(), default);
        Assert.Null(ausente.Error!.RetryAfter);
    }

    // Servidor, tempo e cancelamento

    [Fact]
    public async Task Servidor_indisponivel_e_falha_transitoria_sem_vazar_o_endereco()
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => throw new HttpRequestException("conexão recusada em http://10.9.8.7:11434/api/chat");
        var r = await p.CompleteAsync(Request(), default);
        Assert.Equal(AiErrorKind.Transient, r.Error!.Kind);
        Assert.Equal("server-unavailable", r.Error.Code);
        Assert.Equal(AiUsageCertainty.Unknown, r.UsageCertainty);
        Assert.DoesNotContain("10.9.8.7", r.Error.Code);
    }

    [Fact]
    public async Task Tempo_esgotado_e_falha_transitoria_de_consumo_desconhecido()
    {
        var (p, h) = Build();
        h.Behavior = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("nunca");
        };
        var r = await p.CompleteAsync(Request(timeout: TimeSpan.FromMilliseconds(80)), default);
        Assert.Equal("timeout", r.Error!.Code);
        Assert.Equal(AiUsageCertainty.Unknown, r.UsageCertainty);
    }

    [Fact]
    public async Task Cancelamento_do_chamador_sai_como_excecao_para_o_AiGateway_decidir()
    {
        var (p, h) = Build();
        h.Behavior = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("nunca");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.CompleteAsync(Request(timeout: TimeSpan.FromSeconds(30)), cts.Token));
    }

    [Fact]
    public async Task O_token_de_proxy_vai_so_no_cabecalho_da_requisicao_e_a_credencial_do_tenant_nunca_e_usada()
    {
        var (p, h) = Build(o => o.BearerToken = "token-de-proxy-123");
        var pedido = Request() with { Credential = new AiCredential("sk-ant-NAO-DEVERIA-IR") };
        await p.CompleteAsync(pedido, default);
        var (_, corpo, auth) = Assert.Single(h.Requests);
        Assert.Equal("Bearer token-de-proxy-123", auth);
        Assert.DoesNotContain("NAO-DEVERIA-IR", corpo);
    }

    [Fact]
    public async Task Chamadas_simultaneas_de_tenants_diferentes_nao_misturam_sistema_historico_nem_resposta()
    {
        var (p, h) = Build();
        // Barreira determinística: NENHUMA resposta sai antes de as 40 requisições estarem dentro do servidor simulado ao mesmo tempo; se não houver sobreposição real, a barreira estoura o tempo e o teste falha.
        var barreira = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chegadas = 0;
        h.Behavior = async (_, corpo, ct) =>
        {
            if (Interlocked.Increment(ref chegadas) == 40)
            {
                barreira.TrySetResult();
            }

            await barreira.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var json = JsonNode.Parse(corpo)!;
            var eco = (string?)json["messages"]![0]!["content"] + "|" + (string?)json["messages"]![1]!["content"];
            return Ok("eco:" + eco);
        };
        var tarefas = Enumerable.Range(0, 40).Select(i => p.CompleteAsync(Request(system: $"sistema-{i}", messages: [new AiMessage(AiMessageRole.User, $"pergunta-{i}")], timeout: TimeSpan.FromSeconds(20)), default)).ToArray();
        var respostas = await Task.WhenAll(tarefas);

        Assert.Equal(40, h.PicoSimultaneo);
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal($"eco:sistema-{i}|pergunta-{i}", respostas[i].Completion!.Text);
        }

        Assert.Equal(40, h.Requests.Count);
    }

    // Configuração do endereço (infraestrutura)

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434/")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://192.168.1.50:11434")]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://172.20.1.1:11434")]
    [InlineData("http://ollama:11434")]
    [InlineData("https://ia.exemplo.com.br")]
    public void Enderecos_confiaveis_sao_aceitos(string url) => new OllamaChatOptions { BaseAddress = url }.Validate();

    [Theory]
    [InlineData("http://ollama.exemplo.com:11434")]
    [InlineData("http://8.8.8.8:11434")]
    [InlineData("http://172.32.0.1:11434")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://127.0.0.1.evil.com")]
    [InlineData("ftp://localhost")]
    [InlineData("nao e url")]
    [InlineData("https://usuario:senha@host.exemplo.com")]
    [InlineData("https://host.exemplo.com/?x=1")]
    public void Enderecos_inseguros_ou_invalidos_sao_recusados_na_partida(string url) =>
        Assert.Throws<InvalidOperationException>(() => new OllamaChatOptions { BaseAddress = url }.Validate());

    [Fact]
    public void Configuracao_invalida_falha_na_partida()
    {
        Assert.Throws<InvalidOperationException>(() => new OllamaChatOptions { KeepAlive = "para-sempre" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OllamaChatOptions { BearerToken = "com espaco" }.Validate());
        var perfil = new OllamaChatOptions();
        perfil.Profiles["x"] = new OllamaChatProfile { NumCtx = 10 };
        Assert.Throws<InvalidOperationException>(() => perfil.Validate());
    }

    [Fact]
    public void Os_perfis_padrao_tem_contexto_de_8192_e_o_de_modelo_com_raciocinio_pede_think_falso()
    {
        var o = new OllamaChatOptions();
        o.Validate();
        Assert.Equal(8192, o.Profiles["atendimento-local"].NumCtx);
        Assert.Null(o.Profiles["atendimento-local"].Think);
        Assert.Equal(8192, o.Profiles["atendimento-local-sem-raciocinio"].NumCtx);
        Assert.False(o.Profiles["atendimento-local-sem-raciocinio"].Think);
    }

    [Fact]
    public void O_cliente_proprio_nao_segue_redirecionamentos()
    {
        using var handler = OllamaChatProvider.CreateHandler(new OllamaChatOptions());
        Assert.False(handler.AllowAutoRedirect);
    }

    // Sondagem

    [Fact]
    public async Task A_sondagem_distingue_servidor_modelo_instalado_e_compativel_sem_baixar_nada()
    {
        var h = new Handler
        {
            Behavior = (req, corpo, _) => Task.FromResult(req.RequestUri!.AbsolutePath switch
            {
                "/api/version" => Json(HttpStatusCode.OK, "{\"version\":\"0.13.0\"}"),
                "/api/tags" => Json(HttpStatusCode.OK, "{\"models\":[{\"name\":\"qwen3:4b\"},{\"name\":\"embed:latest\"}]}"),
                "/api/show" when corpo.Contains("qwen3:4b") => Json(HttpStatusCode.OK, "{\"capabilities\":[\"completion\",\"thinking\"],\"model_info\":{\"qwen3.context_length\":40960}}"),
                "/api/show" => Json(HttpStatusCode.OK, "{\"capabilities\":[\"embedding\"],\"model_info\":{}}"),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            }),
        };
        var sonda = new OllamaServerProbe(new HttpClient(h), new OllamaChatOptions());
        var s = await sonda.ProbeAsync(["qwen3:4b", "embed", "ausente:1b"], TimeSpan.FromSeconds(2));

        Assert.True(s.Reachable);
        Assert.Equal("0.13.0", s.Version);
        var instalado = s.Models.Single(m => m.ModelId == "qwen3:4b");
        Assert.True(instalado.Installed && instalado.Compatible && instalado.SupportsThinking);
        Assert.Equal(40960, instalado.ContextLength);
        var embedding = s.Models.Single(m => m.ModelId == "embed");
        Assert.True(embedding.Installed);
        Assert.False(embedding.Compatible);
        Assert.False(s.Models.Single(m => m.ModelId == "ausente:1b").Installed);
        Assert.All(h.Requests, r => Assert.NotEqual("/api/pull", r.Uri.AbsolutePath));
        Assert.DoesNotContain(h.Requests, r => r.Uri.AbsolutePath.Contains("pull") || r.Uri.AbsolutePath.Contains("create"));
    }

    [Fact]
    public async Task A_sondagem_com_servidor_fora_do_ar_informa_sem_lancar()
    {
        var h = new Handler { Behavior = (_, _, _) => throw new HttpRequestException("recusada") };
        var s = await new OllamaServerProbe(new HttpClient(h), new OllamaChatOptions()).ProbeAsync(["qwen3:4b"], TimeSpan.FromSeconds(2));
        Assert.False(s.Reachable);
        Assert.Equal("server-unavailable", s.ErrorCode);
    }

    // P2-01: proteção do contexto (heurística; não prova a preservação do histórico)

    [Fact]
    public async Task Requisicao_claramente_acima_do_orcamento_de_contexto_e_recusada_antes_de_enviar_nada()
    {
        var (p, h) = Build(o => o.Profiles["atendimento-local"] = new OllamaChatProfile { NumCtx = 1024 });
        var longo = new string('a', 3000); // ~1300 tokens estimados com a margem: não cabe em 1024
        var r = await p.CompleteAsync(Request(maxOutput: 100, messages: [new AiMessage(AiMessageRole.User, longo)]), default);

        Assert.Equal(AiErrorKind.ContextTooLong, r.Error!.Kind);
        Assert.Equal("context-budget-exceeded", r.Error.Code);
        Assert.Equal(AiUsageCertainty.NoneIncurred, r.UsageCertainty);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task A_reserva_da_resposta_conta_no_orcamento()
    {
        var (p, h) = Build(o => o.Profiles["atendimento-local"] = new OllamaChatProfile { NumCtx = 1024 });
        var medio = new string('a', 1500); // ~650 tokens estimados: cabe sozinho, mas não com 600 de saída
        var cabe = await p.CompleteAsync(Request(maxOutput: 100, messages: [new AiMessage(AiMessageRole.User, medio)]), default);
        var naoCabe = await p.CompleteAsync(Request(maxOutput: 600, messages: [new AiMessage(AiMessageRole.User, medio)]), default);

        Assert.True(cabe.IsSuccess);
        Assert.Equal("context-budget-exceeded", naoCabe.Error!.Code);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task O_esquema_acrescentado_ao_sistema_conta_no_orcamento_e_o_de_format_conta_quando_nao_esta_no_sistema()
    {
        // Sem o esquema o pedido cabe; com o esquema acrescentado ao sistema (que o adaptador introduz), passa a não caber.
        var textoLongo = new string('a', 1100);
        var semEsquema = Build(o => o.Profiles["atendimento-local"] = new OllamaChatProfile { NumCtx = 700 });
        Assert.True((await semEsquema.Provider.CompleteAsync(Request(maxOutput: 100, messages: [new AiMessage(AiMessageRole.User, textoLongo)]), default)).IsSuccess);

        var comEsquema = Build(o => o.Profiles["atendimento-local"] = new OllamaChatProfile { NumCtx = 700 });
        var r = await comEsquema.Provider.CompleteAsync(Request(maxOutput: 100, mode: AiOutputMode.StructuredJson, messages: [new AiMessage(AiMessageRole.User, textoLongo)]), default);
        Assert.Equal("context-budget-exceeded", r.Error!.Code);

        // Com o esquema FORA do sistema (grounding desligado), o esquema de format ainda é contado, por prudência.
        var semGrounding = Build(o => { o.Profiles["atendimento-local"] = new OllamaChatProfile { NumCtx = 640 }; o.GroundStructuredOutputInPrompt = false; });
        var semFormat = await semGrounding.Provider.CompleteAsync(Request(maxOutput: 100, messages: [new AiMessage(AiMessageRole.User, textoLongo)]), default);
        var comFormat = await semGrounding.Provider.CompleteAsync(Request(maxOutput: 100, mode: AiOutputMode.StructuredJson, messages: [new AiMessage(AiMessageRole.User, textoLongo)]), default);
        Assert.True(semFormat.IsSuccess);
        Assert.Equal("context-budget-exceeded", comFormat.Error!.Code);
    }

    [Fact]
    public void Catalogo_e_perfil_precisam_ser_coerentes_para_modelos_ativos_do_ollama()
    {
        var o = new OllamaChatOptions();
        OllamaCatalogEntry E(string perfil = "atendimento-local", int janela = 8192, int saida = 1024, bool ativo = true, string provedor = "ollama") => new(provedor, "m", perfil, janela, saida, ativo);

        Assert.Empty(OllamaCatalogCoherence.Find(o, [E()]));
        Assert.Single(OllamaCatalogCoherence.Find(o, [E(janela: 4096)]));            // janela diferente do NumCtx
        Assert.Single(OllamaCatalogCoherence.Find(o, [E(perfil: "inexistente")]));    // perfil que não existe
        Assert.Single(OllamaCatalogCoherence.Find(o, [E(saida: 8192)]));              // saída não cabe na janela
        Assert.Empty(OllamaCatalogCoherence.Find(o, [E(janela: 4096, ativo: false)])); // desativado: só modelo de referência
        Assert.Empty(OllamaCatalogCoherence.Find(o, [E(janela: 4096, provedor: "outro")])); // outros provedores não são tocados
        Assert.Throws<InvalidOperationException>(() => OllamaCatalogCoherence.EnsureCoherent(o, [E(janela: 4096)]));
    }

    [Fact]
    public void O_catalogo_de_referencia_dos_hosts_e_coerente_com_os_perfis_padrao()
    {
        foreach (var host in new[] { "IaBimeta.Api", "IaBimeta.Worker" })
        {
            var raiz = AppContext.BaseDirectory;
            while (raiz is not null && !Directory.Exists(Path.Combine(raiz, "backend")))
            {
                raiz = Path.GetDirectoryName(raiz);
            }

            Assert.NotNull(raiz);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(raiz!, "backend", "host", host, "appsettings.json")))!;
            var entradas = json["AiGateway"]!["Catalog"]!["Models"]!.AsArray().Where(m => (string?)m!["ProviderId"] == "ollama")
                .Select(m => new OllamaCatalogEntry("ollama", (string)m!["ModelId"]!, (string)m["Profile"]!, (int)m["ContextWindowTokens"]!, (int)m["MaxOutputTokens"]!, true)).ToList();
            Assert.NotEmpty(entradas);
            Assert.Empty(OllamaCatalogCoherence.Find(new OllamaChatOptions(), entradas)); // se fosse ativado, seria coerente
        }
    }

    [Fact]
    public void Estimativa_invalida_falha_na_partida()
    {
        Assert.Throws<InvalidOperationException>(() => new OllamaChatOptions { InputSafetyMargin = 0.5 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OllamaChatOptions { CharactersPerToken = 0 }.Validate());
    }

    // P2-02: raciocínio deixado no texto

    private static async Task<AiCompletionResult> ComConteudo(string conteudo, string reason = "stop", string? thinking = null)
    {
        var (p, h) = Build();
        h.Behavior = (_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, OkBody(conteudo, 210, 77, reason, thinking)));
        return await p.CompleteAsync(Request(), default);
    }

    private static void ExigeInutilizavelComConsumo(AiCompletionResult r, string codigo)
    {
        Assert.False(r.IsSuccess);
        Assert.Null(r.Completion);
        Assert.Equal(codigo, r.Error!.Code);
        Assert.Equal(AiErrorKind.InvalidRequest, r.Error.Kind); // permanente: o Agents transfere ao atendente, sem repetir
        Assert.Equal(AiUsageCertainty.Reported, r.UsageCertainty);
        Assert.Equal(new AiUsage(210, 0, 0, 77), r.Usage); // os tokens informados são preservados
    }

    [Fact]
    public async Task Campo_message_thinking_separado_nunca_e_lido()
    {
        var r = await ComConteudo("Resposta limpa.", thinking: "raciocinio secreto");
        Assert.True(r.IsSuccess);
        Assert.Equal("Resposta limpa.", r.Completion!.Text);
    }

    [Fact]
    public async Task Bloco_completo_antes_da_resposta_e_descartado()
    {
        var r = await ComConteudo("<think>vou pensar\nem varias linhas</think>\n\nResposta final.");
        Assert.True(r.IsSuccess);
        Assert.Equal("Resposta final.", r.Completion!.Text);
    }

    [Fact]
    public async Task Multiplos_blocos_no_inicio_sao_descartados()
    {
        var r = await ComConteudo("<think>a</think> <THINK>b</THINK>\nResposta.");
        Assert.Equal("Resposta.", r.Completion!.Text);
    }

    [Theory]
    [InlineData("<think>pensando sem fechar a ideia")]
    [InlineData("Resposta</think> depois")]
    [InlineData("Resposta <think>meio</think> do texto")]
    [InlineData("<think>a</think> Resposta <think>b sem fim")]
    [InlineData("Resposta ok.\n<think>raciocinio no fim</think>")]
    public async Task Marcador_incompleto_isolado_ou_fora_de_lugar_torna_a_saida_inutilizavel_com_o_consumo_preservado(string conteudo)
    {
        ExigeInutilizavelComConsumo(await ComConteudo(conteudo), "reasoning-leak");
    }

    [Fact]
    public async Task Marcador_de_abertura_sem_fechamento_por_truncamento_tambem_e_inutilizavel()
    {
        ExigeInutilizavelComConsumo(await ComConteudo("<think>raciocinio cortado pelo limite de saida", reason: "length"), "reasoning-leak");
    }

    [Theory]
    [InlineData("<think>so raciocinio</think>")]
    [InlineData("<think>a</think>\n<think>b</think>  ")]
    public async Task Saida_composta_so_de_raciocinio_e_inutilizavel(string conteudo)
    {
        ExigeInutilizavelComConsumo(await ComConteudo(conteudo), "reasoning-only");
    }

    [Fact]
    public async Task Json_valido_com_conteudo_literal_legitimo_passa_sem_nenhuma_alteracao()
    {
        var json = "{\"action\":\"reply\",\"reply_text\":\"Use <b>negrito</b>; 2 < 3; o termo think e comum e a tag <think> citada no meio de um texto e literal.\",\"handoff_reason\":\"none\"}";
        var r = await ComConteudo(json);
        Assert.True(r.IsSuccess);
        Assert.Equal(json, r.Completion!.Text);
    }

    [Fact]
    public async Task Bloco_completo_antes_de_json_valido_e_descartado_e_o_json_nao_e_reescrito()
    {
        var json = "{\"action\":\"reply\",\"reply_text\":\"Olá\",\"handoff_reason\":\"none\"}";
        var r = await ComConteudo("<think>decidindo</think>\n" + json);
        Assert.Equal(json, r.Completion!.Text);
    }

    [Theory]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"<think>raciocinio que vazou sem fechar\",\"handoff_reason\":\"none\"}")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"meu raciocinio</think>A resposta\",\"handoff_reason\":\"none\"}")]
    [InlineData("{\"action\":\"reply\",\"reply_text\":\"<think>a</think>Resposta\",\"handoff_reason\":\"none\"}")]
    [InlineData("{\"itens\":[\"ok\",{\"x\":\"<think>escondido\"}]}")]
    public async Task Json_com_raciocinio_no_campo_de_resposta_e_inutilizavel(string json)
    {
        ExigeInutilizavelComConsumo(await ComConteudo(json), "reasoning-leak");
    }

    [Fact]
    public async Task Bloco_sem_fechamento_antes_de_json_e_inutilizavel()
    {
        ExigeInutilizavelComConsumo(await ComConteudo("<think>nao fechou {\"action\":\"reply\"}"), "reasoning-leak");
    }
}
