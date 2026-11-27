using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.Ai.Anthropic.Testing;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Anthropic;

/// <summary>
/// A RESPOSTA da Messages API e os ERROS com HTTP simulado: o consumo normalizado para o ledger (e a certeza dele), os motivos de encerramento, a tabela de erros, o <c>retry-after</c>, o tempo limite,
/// o cancelamento, as respostas inesperadas e o sigilo da credencial. Regra central: consumo desconhecido é <c>Unknown</c>, nunca presumido como zero.
/// </summary>
public sealed class AnthropicResponseTests
{
    private static async Task<AiCompletionResult> Chamar(HttpStatusCode status, string corpo, params (string, string)[] cabecalhos)
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Returns(status, corpo, cabecalhos);
        return await provider.CompleteAsync(Requests.Request(), default);
    }

    // Sucesso e consumo

    [Fact]
    public async Task Sucesso_normaliza_o_texto_o_consumo_real_com_cache_e_o_identificador_da_requisicao()
    {
        var resultado = await Chamar(HttpStatusCode.OK, AnthropicBodies.Ok("Em geral não é necessário jejum.", input: 80, cacheRead: 15, cacheWrite: 5, output: 30, thinking: 12), ("request-id", "req_018EeWyXxfu5pfWkrYcMdjWG"));

        Assert.True(resultado.IsSuccess);
        Assert.Equal(("Em geral não é necessário jejum.", AiStopReason.Completed), (resultado.Completion!.Text, resultado.Completion.StopReason));

        // A entrada TOTAL é a soma das três parcelas que a API reporta separadas (100); leitura e escrita do cache ficam discriminadas; o raciocínio já está contado na saída.
        Assert.Equal(new AiUsage(100, 15, 5, 30, 12), resultado.Usage);
        Assert.Equal(AiUsageCertainty.Reported, resultado.UsageCertainty);
        Assert.Equal("req_018EeWyXxfu5pfWkrYcMdjWG", resultado.ProviderRequestId);
    }

    [Fact]
    public async Task Campos_de_cache_ausentes_ou_nulos_valem_zero()
    {
        var corpo = JsonNode.Parse(AnthropicBodies.Ok("oi"))!;
        corpo["usage"]!.AsObject().Remove("cache_creation_input_tokens");
        corpo["usage"]!["cache_read_input_tokens"] = null;
        corpo["usage"]!.AsObject().Remove("output_tokens_details");

        var resultado = await Chamar(HttpStatusCode.OK, corpo.ToJsonString());

        Assert.Equal(new AiUsage(80, 0, 0, 30, null), resultado.Usage);
    }

    [Fact]
    public async Task O_texto_so_vem_dos_blocos_text_o_raciocinio_e_outros_blocos_nunca_entram()
    {
        var corpo = JsonNode.Parse(AnthropicBodies.Ok("resposta final"))!;
        var conteudo = corpo["content"]!.AsArray();
        conteudo.Insert(0, new JsonObject { ["type"] = "redacted_thinking", ["data"] = "opaco" });
        conteudo.Add(new JsonObject { ["type"] = "tool_use", ["id"] = "t1", ["name"] = "x", ["input"] = new JsonObject() });
        conteudo.Add(new JsonObject { ["type"] = "text", ["text"] = " (fim)" });

        var resultado = await Chamar(HttpStatusCode.OK, corpo.ToJsonString());

        Assert.Equal("resposta final (fim)", resultado.Completion!.Text);
        Assert.DoesNotContain("raciocinio interno", resultado.ToString());
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop_sequence")]
    public async Task Motivos_de_encerramento_normais_sao_Completed(string motivo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, AnthropicBodies.Ok("pronto", stopReason: motivo));

        Assert.Equal(AiStopReason.Completed, resultado.Completion!.StopReason);
    }

    [Fact]
    public async Task Resposta_interrompida_pelo_teto_de_saida_e_Truncated_e_o_consumo_e_o_informado()
    {
        var resultado = await Chamar(HttpStatusCode.OK, AnthropicBodies.Ok("metade da respos", output: 300, stopReason: "max_tokens"));

        Assert.True(resultado.IsSuccess);
        Assert.Equal(("metade da respos", AiStopReason.Truncated), (resultado.Completion!.Text, resultado.Completion.StopReason));
        Assert.Equal((300, AiUsageCertainty.Reported), (resultado.Usage.OutputTokens, resultado.UsageCertainty));
    }

    [Fact]
    public async Task Truncada_durante_o_raciocinio_sem_bloco_de_texto_continua_Truncated_com_texto_vazio()
    {
        var corpo = JsonNode.Parse(AnthropicBodies.Ok("x", stopReason: "max_tokens"))!;
        corpo["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "...", ["signature"] = "s" });

        var resultado = await Chamar(HttpStatusCode.OK, corpo.ToJsonString());

        Assert.Equal((string.Empty, AiStopReason.Truncated), (resultado.Completion!.Text, resultado.Completion.StopReason));
    }

    [Fact]
    public async Task Recusa_do_modelo_e_Refused_com_o_consumo_cobrado_e_sem_repassar_o_texto()
    {
        var resultado = await Chamar(HttpStatusCode.OK, AnthropicBodies.Ok("texto que não deve ser repassado", stopReason: "refusal"));

        Assert.Equal((string.Empty, AiStopReason.Refused), (resultado.Completion!.Text, resultado.Completion.StopReason));
        Assert.Equal(AiUsageCertainty.Reported, resultado.UsageCertainty);
        Assert.True(resultado.Usage.OutputTokens > 0);
    }

    [Fact]
    public async Task Concluida_sem_nenhum_texto_e_falha_mas_o_consumo_informado_vale()
    {
        var corpo = JsonNode.Parse(AnthropicBodies.Ok("x"))!;
        corpo["content"] = new JsonArray();

        var resultado = await Chamar(HttpStatusCode.OK, corpo.ToJsonString());

        Assert.Equal((AiErrorKind.Transient, "empty-output", AiUsageCertainty.Reported), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.Equal(30, resultado.Usage.OutputTokens);
    }

    [Fact]
    public async Task Janela_de_contexto_excedida_e_contexto_longo_demais_e_nao_se_tenta_de_novo()
    {
        var resultado = await Chamar(HttpStatusCode.OK, AnthropicBodies.Ok("x", stopReason: "model_context_window_exceeded"));

        Assert.Equal((AiErrorKind.ContextTooLong, AiUsageCertainty.Reported), (resultado.Error!.Kind, resultado.UsageCertainty));
    }

    [Theory]
    [InlineData("tool_use")]
    [InlineData("pause_turn")]
    [InlineData("motivo_novo_do_provedor")]
    public async Task Motivo_de_encerramento_inesperado_nao_vira_sucesso_e_nunca_e_retentavel(string motivo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, AnthropicBodies.Ok("nada utilizável", stopReason: motivo));

        Assert.Null(resultado.Completion);
        Assert.Equal((AiErrorKind.InvalidRequest, "unexpected-stop-reason", AiUsageCertainty.Reported), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
    }

    [Fact]
    public async Task Sem_stop_reason_a_resposta_tambem_e_inesperada()
    {
        var corpo = JsonNode.Parse(AnthropicBodies.Ok("x"))!;
        corpo.AsObject().Remove("stop_reason");

        var resultado = await Chamar(HttpStatusCode.OK, corpo.ToJsonString());

        Assert.Equal("unexpected-stop-reason", resultado.Error!.Code);
    }

    [Theory]
    [InlineData("""{"type":"message","content":[{"type":"text","text":"oi"}],"stop_reason":"end_turn"}""")]
    [InlineData("""{"type":"message","content":[{"type":"text","text":"oi"}],"stop_reason":"end_turn","usage":{"input_tokens":10}}""")]
    [InlineData("""{"type":"message","content":[{"type":"text","text":"oi"}],"stop_reason":"end_turn","usage":{"input_tokens":-1,"output_tokens":5}}""")]
    [InlineData("""{"type":"message","content":[{"type":"text","text":"oi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":5,"cache_read_input_tokens":"x"}}""")]
    [InlineData("""{"type":"message","content":[{"type":"text","text":"oi"}],"stop_reason":"end_turn","usage":{"input_tokens":2147483647,"output_tokens":5,"cache_read_input_tokens":10}}""")]
    public async Task Resposta_sem_consumo_valido_nunca_vira_sucesso_e_o_consumo_fica_Unknown(string corpo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.Null(resultado.Completion);
        Assert.Equal((AiErrorKind.Transient, "missing-usage", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.Equal(AiUsage.None, resultado.Usage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é JSON")]
    [InlineData("[]")]
    [InlineData("""{"type":"error","error":{"type":"api_error"}}""")]
    [InlineData("""{"type":"outra_coisa"}""")]
    public async Task Resposta_inesperada_do_provedor_e_falha_transitoria_de_consumo_desconhecido(string corpo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.Null(resultado.Completion);
        Assert.Equal((AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
    }

    [Fact]
    public async Task Escape_de_substituto_solto_no_texto_do_provedor_e_resposta_invalida_e_nao_excecao()
    {
        var corpo = AnthropicBodies.Ok("x").Replace("\"x\"", "\"\\ud83d solto\"");

        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.True(resultado.IsSuccess || resultado.Error!.Code == "invalid-response");
    }

    [Fact]
    public async Task Corpo_maior_que_o_limite_e_resposta_invalida_e_nunca_e_lido_inteiro()
    {
        var (provider, handler, _) = Requests.Provider(o => o.MaxResponseBytes = 2048);
        handler.Returns(HttpStatusCode.OK, new string('x', 5000));

        var resultado = await provider.CompleteAsync(Requests.Request(), default);

        Assert.Equal(("invalid-response", AiUsageCertainty.Unknown), (resultado.Error!.Code, resultado.UsageCertainty));
    }

    // Erros HTTP

    [Theory]
    [InlineData(400, "invalid_request_error", AiErrorKind.InvalidRequest)]
    [InlineData(401, "authentication_error", AiErrorKind.InvalidCredentials)]
    [InlineData(402, "billing_error", AiErrorKind.QuotaOrBilling)]
    [InlineData(403, "permission_error", AiErrorKind.InvalidCredentials)]
    [InlineData(404, "not_found_error", AiErrorKind.InvalidRequest)]
    [InlineData(413, "request_too_large", AiErrorKind.ContextTooLong)]
    [InlineData(422, "invalid_request_error", AiErrorKind.InvalidRequest)]
    public async Task Rejeicao_de_admissao_com_o_corpo_de_erro_completo_nao_cobra_nada(int status, string tipo, AiErrorKind esperado)
    {
        var resultado = await Chamar((HttpStatusCode)status, AnthropicBodies.Error(tipo), ("request-id", "req_abc"));

        Assert.Equal((esperado, tipo, AiUsageCertainty.NoneIncurred), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.Equal(AiUsage.None, resultado.Usage);
        Assert.Equal("req_abc", resultado.ProviderRequestId);
    }

    [Theory]
    [InlineData(409, "conflict_error", AiErrorKind.Transient)]
    [InlineData(429, "rate_limit_error", AiErrorKind.Transient)]
    [InlineData(500, "api_error", AiErrorKind.Transient)]
    [InlineData(503, "overloaded_error", AiErrorKind.Transient)]
    [InlineData(504, "timeout_error", AiErrorKind.Transient)]
    [InlineData(529, "overloaded_error", AiErrorKind.Transient)]
    [InlineData(408, "timeout_error", AiErrorKind.Transient)]
    public async Task Limite_de_taxa_sobrecarga_e_5xx_nunca_presumem_que_nada_foi_cobrado_o_consumo_fica_Unknown(int status, string tipo, AiErrorKind esperado)
    {
        var resultado = await Chamar((HttpStatusCode)status, AnthropicBodies.Error(tipo));

        Assert.Equal((esperado, tipo, AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
    }

    [Fact]
    public async Task O_teto_de_gasto_do_nivel_no_429_e_cota_esgotada_sem_espera_e_o_codigo_vem_de_details()
    {
        var resultado = await Chamar(HttpStatusCode.TooManyRequests, AnthropicBodies.Error("rate_limit_error", errorCode: "enforced_spend_limit_reached"), ("retry-after", "30"));

        Assert.Equal((AiErrorKind.QuotaOrBilling, "enforced_spend_limit_reached", (TimeSpan?)null), (resultado.Error!.Kind, resultado.Error.Code, resultado.Error.RetryAfter));
        Assert.Equal(AiUsageCertainty.Unknown, resultado.UsageCertainty);
    }

    [Fact]
    public async Task O_limite_de_gasto_definido_pelo_cliente_no_400_e_cota_esgotada()
    {
        var resultado = await Chamar(HttpStatusCode.BadRequest, AnthropicBodies.Error("invalid_request_error", "You have reached your specified API usage limits. You will regain access on 2026-12-01."));

        Assert.Equal((AiErrorKind.QuotaOrBilling, "invalid_request_error"), (resultado.Error!.Kind, resultado.Error.Code));
        Assert.DoesNotContain("regain", resultado.ToString());
    }

    [Theory]
    [InlineData(400, "texto puro, não é JSON")]
    [InlineData(401, "{}")]
    [InlineData(401, """{"detalhe":"sem o objeto error"}""")]
    [InlineData(404, "")]
    [InlineData(403, """{"type":"error","error":"não é objeto"}""")]
    public async Task Sem_o_corpo_de_erro_completo_nao_ha_evidencia_e_o_consumo_fica_Unknown(int status, string corpo)
    {
        var resultado = await Chamar((HttpStatusCode)status, corpo);

        Assert.Equal(AiUsageCertainty.Unknown, resultado.UsageCertainty);
        Assert.Equal($"http-{status}", resultado.Error!.Code);
    }

    [Fact]
    public async Task A_espera_sugerida_do_429_vira_RetryAfter_e_nunca_negativa()
    {
        var comEspera = await Chamar(HttpStatusCode.TooManyRequests, AnthropicBodies.Error("rate_limit_error"), ("retry-after", "7"));
        var semEspera = await Chamar(HttpStatusCode.TooManyRequests, AnthropicBodies.Error("rate_limit_error"));

        Assert.Equal(TimeSpan.FromSeconds(7), comEspera.Error!.RetryAfter);
        Assert.Null(semEspera.Error!.RetryAfter);
    }

    [Fact]
    public async Task O_Retry_After_em_data_vira_o_tempo_que_falta_e_nunca_negativo()
    {
        var agora = DateTimeOffset.UtcNow;
        var futuro = await Chamar((HttpStatusCode)529, AnthropicBodies.Error("overloaded_error"), ("retry-after", agora.AddSeconds(30).ToString("R")));
        var passado = await Chamar((HttpStatusCode)529, AnthropicBodies.Error("overloaded_error"), ("retry-after", agora.AddMinutes(-5).ToString("R")));

        Assert.InRange(futuro.Error!.RetryAfter!.Value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(31));
        Assert.Equal(TimeSpan.Zero, passado.Error!.RetryAfter);
    }

    [Fact]
    public async Task O_identificador_da_requisicao_vem_do_cabecalho_ou_do_corpo_e_so_se_for_seguro()
    {
        var doCorpo = await Chamar(HttpStatusCode.InternalServerError, AnthropicBodies.Error("api_error", requestId: "req_do_corpo"));
        var suspeito = await Chamar(HttpStatusCode.InternalServerError, AnthropicBodies.Error("api_error", requestId: "req com espaço e <script>"));

        Assert.Equal("req_do_corpo", doCorpo.ProviderRequestId);
        Assert.Null(suspeito.ProviderRequestId);
    }

    // Tempo limite, cancelamento e rede

    [Fact]
    public async Task Tempo_limite_esgotado_e_falha_transitoria_de_consumo_desconhecido_e_nao_excecao()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeHandler.Json(HttpStatusCode.OK, AnthropicBodies.Ok("nunca chega"));
        };

        var resultado = await provider.CompleteAsync(Requests.Request(timeout: TimeSpan.FromMilliseconds(80)), default);

        Assert.Null(resultado.Completion);
        Assert.Equal((AiErrorKind.Transient, "timeout", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.InRange(resultado.Latency, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancelamento_do_chamador_propaga_como_OperationCanceledException_para_o_AiGateway_decidir()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeHandler.Json(HttpStatusCode.OK, AnthropicBodies.Ok("nunca chega"));
        };
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Requests.Request(timeout: TimeSpan.FromSeconds(20)), cancelamento.Token));
    }

    [Fact]
    public async Task Falha_de_rede_e_transitoria_de_consumo_desconhecido_e_a_mensagem_da_excecao_nunca_vaza()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = (captura, _) => throw new HttpRequestException($"falha ao conectar com {captura.Authorization} em api.anthropic.com");

        var resultado = await provider.CompleteAsync(Requests.Request(), default);

        Assert.Equal((AiErrorKind.Transient, "network-error", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.DoesNotContain(Requests.Canary, resultado.ToString());
    }

    // Sigilo

    [Theory]
    [InlineData(401, "authentication_error")]
    [InlineData(400, "invalid_request_error")]
    [InlineData(500, "api_error")]
    public async Task A_mensagem_do_provedor_que_ecoa_a_credencial_nunca_chega_ao_resultado(int status, string tipo)
    {
        var resultado = await Chamar((HttpStatusCode)status, AnthropicBodies.Error(tipo, $"invalid x-api-key {Requests.Canary} e o prompt do cliente"));

        Assert.DoesNotContain(Requests.Canary, resultado.ToString());
        Assert.DoesNotContain("prompt do cliente", resultado.ToString());
    }

    [Fact]
    public async Task Um_codigo_de_erro_fora_do_formato_estavel_e_descartado_em_vez_de_repetido()
    {
        var resultado = await Chamar(HttpStatusCode.BadRequest, AnthropicBodies.Error("Tipo Com Espaço!", errorCode: "<script>"));

        Assert.Equal("http-400", resultado.Error!.Code);
    }

    [Theory]
    [InlineData(200, """{"type":"message","content":[{"type":"text","text":"oi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData(200, "lixo")]
    [InlineData(429, "{}")]
    [InlineData(500, "")]
    [InlineData(401, """{"type":"error","error":{"type":"authentication_error","message":"m"}}""")]
    public async Task Todo_resultado_tem_exatamente_uma_resposta_ou_uma_falha_e_sempre_uma_certeza_valida(int status, string corpo)
    {
        var resultado = await Chamar((HttpStatusCode)status, corpo);

        Assert.True((resultado.Completion is null) != (resultado.Error is null));
        Assert.True(Enum.IsDefined(resultado.UsageCertainty));
    }
}
