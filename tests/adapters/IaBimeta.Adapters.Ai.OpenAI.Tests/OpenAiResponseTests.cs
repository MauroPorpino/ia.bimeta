using System.Net;
using IaBimeta.Adapters.Ai.OpenAI.Testing;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// A RESPOSTA da Responses API e os ERROS (ADR 0017, seção C e Anexo A.1) com HTTP simulado: o consumo normalizado para o ledger (e a certeza dele), o motivo de parada, a tabela de erros (400, 401, 403,
/// 404, 413, 422, 429, 5xx), o <c>Retry-After</c>, o tempo limite, o cancelamento, as respostas inválidas e o sigilo da credencial. Regra central: consumo desconhecido é <c>Unknown</c>, nunca presumido como zero.
/// </summary>
public sealed class OpenAiResponseTests
{
    private static async Task<AiCompletionResult> Chamar(HttpStatusCode status, string corpo, params (string, string)[] cabecalhos)
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Returns(status, corpo, cabecalhos);
        return await provider.CompleteAsync(Requests.Request(), default);
    }

    // Sucesso e consumo

    [Fact]
    public async Task Sucesso_normaliza_o_texto_o_consumo_real_e_o_identificador_da_requisicao()
    {
        var resultado = await Chamar(HttpStatusCode.OK, OpenAiBodies.Ok("Em geral não precisa de jejum.", input: 120, cached: 40, output: 35, reasoning: 5), ("x-request-id", "req_abc123"));

        Assert.True(resultado.IsSuccess);
        Assert.Equal(("Em geral não precisa de jejum.", AiStopReason.Completed), (resultado.Completion!.Text, resultado.Completion.StopReason));

        // A entrada é o total (inclui o cache), a saída inclui o raciocínio, e a OpenAI não cobra escrita de cache à parte.
        Assert.Equal(new AiUsage(120, 40, 0, 35, 5), resultado.Usage);
        Assert.Equal(AiUsageCertainty.Reported, resultado.UsageCertainty);
        Assert.Equal("req_abc123", resultado.ProviderRequestId);
        Assert.True(resultado.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task O_texto_so_vem_das_partes_output_text_o_raciocinio_e_outros_itens_nunca_entram()
    {
        var corpo = """
            {"status":"completed","output":[
              {"type":"reasoning","summary":[{"type":"summary_text","text":"RACIOCINIO SECRETO"}]},
              {"type":"function_call","name":"x","arguments":"{}"},
              {"type":"message","content":[{"type":"output_text","text":"Parte 1. "},{"type":"output_text","text":"Parte 2."}]}],
             "usage":{"input_tokens":10,"output_tokens":5}}
            """;

        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.Equal("Parte 1. Parte 2.", resultado.Completion!.Text);
        Assert.DoesNotContain("RACIOCINIO", resultado.Completion.Text);
        Assert.Equal(new AiUsage(10, 0, 0, 5, null), resultado.Usage); // sem detalhes informados: nada é inventado
    }

    [Fact]
    public async Task Cache_e_raciocinio_maiores_que_o_total_sao_limitados_ao_total()
    {
        var resultado = await Chamar(HttpStatusCode.OK, OpenAiBodies.Ok("ok", input: 10, cached: 500, output: 3, reasoning: 99));

        Assert.Equal(new AiUsage(10, 10, 0, 3, 3), resultado.Usage);
    }

    [Fact]
    public async Task Resposta_incompleta_pelo_teto_de_saida_e_Truncated_e_o_consumo_e_o_informado()
    {
        var corpo = """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"message","content":[{"type":"output_text","text":"{\"action\":\"re"}]}],"usage":{"input_tokens":50,"output_tokens":300}}""";

        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.Equal(AiStopReason.Truncated, resultado.Completion!.StopReason);
        Assert.Equal(300, resultado.Usage.OutputTokens);
        Assert.Equal(AiUsageCertainty.Reported, resultado.UsageCertainty);
    }

    [Theory]
    [InlineData("""{"status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[],"usage":{"input_tokens":5,"output_tokens":0}}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"não posso ajudar com isso"}]}],"usage":{"input_tokens":5,"output_tokens":9}}""")]
    public async Task Recusa_do_modelo_e_Refused_com_o_consumo_cobrado_e_sem_repassar_o_texto_da_recusa(string corpo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.Equal((string.Empty, AiStopReason.Refused), (resultado.Completion!.Text, resultado.Completion.StopReason));
        Assert.Equal(AiUsageCertainty.Reported, resultado.UsageCertainty);
        Assert.True(resultado.Usage.InputTokens > 0);
    }

    [Fact]
    public async Task Concluida_sem_nenhum_texto_e_falha_mas_o_consumo_informado_vale()
    {
        var resultado = await Chamar(HttpStatusCode.OK, """{"status":"completed","output":[{"type":"reasoning","summary":[]}],"usage":{"input_tokens":40,"output_tokens":12}}""");

        Assert.Equal((AiErrorKind.Transient, "empty-output", AiUsageCertainty.Reported), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.Equal(40, resultado.Usage.InputTokens);
    }

    // Consumo desconhecido é Unknown

    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}]}""")] // sem usage
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}],"usage":{}}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}],"usage":{"input_tokens":10}}""")] // falta a saída
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}],"usage":{"input_tokens":-1,"output_tokens":2}}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}],"usage":{"input_tokens":"10","output_tokens":2}}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}],"usage":{"input_tokens":1.5,"output_tokens":2}}""")]
    public async Task Resposta_sem_consumo_valido_nunca_vira_sucesso_e_o_consumo_fica_Unknown(string corpo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        // A resposta é descartada: a contabilidade do tenant não aceita "gastou não sei quanto" como sucesso. O AiGateway cobra a reserva e pode reconciliar.
        Assert.Null(resultado.Completion);
        Assert.Equal((AiErrorKind.Transient, "missing-usage", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.Equal(AiUsage.None, resultado.Usage);
    }

    [Theory]
    [InlineData("isto não é JSON")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"status":"in_progress"}""")]
    [InlineData("""{"status":"queued","output":[]}""")]
    public async Task Resposta_invalida_do_provedor_e_falha_transitoria_de_consumo_desconhecido(string corpo)
    {
        var resultado = await Chamar(HttpStatusCode.OK, corpo);

        Assert.Null(resultado.Completion);
        Assert.Equal(AiErrorKind.Transient, resultado.Error!.Kind);
        Assert.Contains(resultado.Error.Code, new[] { "invalid-response", "unexpected-status" });
        Assert.Equal(AiUsageCertainty.Unknown, resultado.UsageCertainty);
    }

    [Fact]
    public async Task Escape_de_substituto_solto_no_texto_do_provedor_e_resposta_invalida_e_nao_excecao()
    {
        var resultado = await Chamar(HttpStatusCode.OK, """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi \ud83d"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""");

        Assert.Equal(("invalid-response", AiUsageCertainty.Unknown), (resultado.Error!.Code, resultado.UsageCertainty));
    }

    [Fact]
    public async Task Corpo_maior_que_o_limite_e_resposta_invalida_e_nunca_e_lido_inteiro()
    {
        var (provider, handler, _) = Requests.Provider(o => o.MaxResponseBytes = 2048);
        handler.Returns(HttpStatusCode.OK, new string('x', 5000));

        var resultado = await provider.CompleteAsync(Requests.Request(), default);

        Assert.Equal(("invalid-response", AiUsageCertainty.Unknown), (resultado.Error!.Code, resultado.UsageCertainty));
    }

    [Fact]
    public async Task Status_failed_no_corpo_mapeia_pelo_codigo_e_so_cobra_se_informou_o_consumo()
    {
        var transitoria = await Chamar(HttpStatusCode.OK, """{"status":"failed","error":{"code":"server_error","message":"x"}}""");
        var invalida = await Chamar(HttpStatusCode.OK, """{"status":"failed","error":{"code":"invalid_prompt","message":"x"},"usage":{"input_tokens":7,"output_tokens":0}}""");

        Assert.Equal((AiErrorKind.Transient, "server_error", AiUsageCertainty.Unknown), (transitoria.Error!.Kind, transitoria.Error.Code, transitoria.UsageCertainty));
        Assert.Equal((AiErrorKind.InvalidRequest, "invalid_prompt", AiUsageCertainty.Reported), (invalida.Error!.Kind, invalida.Error.Code, invalida.UsageCertainty));
        Assert.Equal(7, invalida.Usage.InputTokens);
    }

    // Erros HTTP: só a rejeição ANTES de gerar, com o corpo de erro completo, é NoneIncurred

    [Theory]
    [InlineData(400, "invalid_request_error", null, AiErrorKind.InvalidRequest, AiUsageCertainty.NoneIncurred)]
    [InlineData(400, null, "context_length_exceeded", AiErrorKind.ContextTooLong, AiUsageCertainty.NoneIncurred)]
    [InlineData(401, "invalid_request_error", "invalid_api_key", AiErrorKind.InvalidCredentials, AiUsageCertainty.NoneIncurred)]
    [InlineData(403, "invalid_request_error", "unsupported_country_region_territory", AiErrorKind.InvalidCredentials, AiUsageCertainty.NoneIncurred)]
    [InlineData(404, "invalid_request_error", "model_not_found", AiErrorKind.InvalidRequest, AiUsageCertainty.NoneIncurred)]
    [InlineData(413, "invalid_request_error", null, AiErrorKind.ContextTooLong, AiUsageCertainty.NoneIncurred)]
    [InlineData(422, "invalid_request_error", null, AiErrorKind.InvalidRequest, AiUsageCertainty.NoneIncurred)]
    [InlineData(402, "billing_error", null, AiErrorKind.QuotaOrBilling, AiUsageCertainty.NoneIncurred)]
    public async Task Rejeicao_de_admissao_com_o_corpo_de_erro_completo_nao_cobra_nada(int status, string? tipo, string? codigo, AiErrorKind esperado, AiUsageCertainty certeza)
    {
        var resultado = await Chamar((HttpStatusCode)status, OpenAiBodies.Error(codigo, tipo));

        Assert.Null(resultado.Completion);
        Assert.Equal((esperado, certeza), (resultado.Error!.Kind, resultado.UsageCertainty));
        Assert.Equal(AiUsage.None, resultado.Usage);
        Assert.Equal(codigo ?? tipo, resultado.Error.Code);
    }

    [Theory]
    [InlineData(429, "rate_limit_exceeded", AiErrorKind.Transient)]
    [InlineData(429, "tokens", AiErrorKind.Transient)]
    [InlineData(429, "credit_balance_exhausted", AiErrorKind.QuotaOrBilling)]
    [InlineData(429, "organization_spend_limit_exceeded", AiErrorKind.QuotaOrBilling)]
    [InlineData(429, "project_spend_limit_exceeded", AiErrorKind.QuotaOrBilling)]
    [InlineData(429, "organization_usage_limit_exceeded", AiErrorKind.QuotaOrBilling)]
    [InlineData(429, "insufficient_quota", AiErrorKind.QuotaOrBilling)]
    [InlineData(500, "server_error", AiErrorKind.Transient)]
    [InlineData(502, "bad_gateway", AiErrorKind.Transient)]
    [InlineData(503, "service_unavailable", AiErrorKind.Transient)]
    [InlineData(504, "gateway_timeout", AiErrorKind.Transient)]
    [InlineData(408, "request_timeout", AiErrorKind.Transient)]
    public async Task Limite_de_taxa_e_5xx_nunca_presumem_que_nada_foi_cobrado_o_consumo_fica_Unknown(int status, string codigo, AiErrorKind esperado)
    {
        // Mesmo com um corpo de erro perfeitamente formado: 429 e 5xx não são evidência de que o provedor não processou nem cobrou.
        var resultado = await Chamar((HttpStatusCode)status, OpenAiBodies.Error(codigo));

        Assert.Equal((esperado, AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.UsageCertainty));
        Assert.Equal(codigo, resultado.Error.Code);
    }

    [Fact]
    public async Task O_tipo_insufficient_quota_tambem_indica_cota_esgotada_no_429()
    {
        var resultado = await Chamar(HttpStatusCode.TooManyRequests, OpenAiBodies.Error(null, "insufficient_quota"));

        Assert.Equal((AiErrorKind.QuotaOrBilling, "insufficient_quota"), (resultado.Error!.Kind, resultado.Error.Code));
    }

    [Theory]
    [InlineData(400, "texto puro, não é JSON")]
    [InlineData(401, "{}")]
    [InlineData(401, """{"detalhe":"sem o objeto error"}""")]
    [InlineData(404, "")]
    public async Task Sem_o_corpo_de_erro_completo_nao_ha_evidencia_e_o_consumo_fica_Unknown(int status, string corpo)
    {
        var resultado = await Chamar((HttpStatusCode)status, corpo);

        Assert.Equal(AiUsageCertainty.Unknown, resultado.UsageCertainty);
        Assert.Equal($"http-{status}", resultado.Error!.Code);
    }

    [Fact]
    public async Task A_espera_sugerida_do_429_em_segundos_vira_RetryAfter_e_cota_esgotada_a_ignora()
    {
        var limite = await Chamar(HttpStatusCode.TooManyRequests, OpenAiBodies.Error("rate_limit_exceeded"), ("Retry-After", "7"));
        var cota = await Chamar(HttpStatusCode.TooManyRequests, OpenAiBodies.Error("insufficient_quota"), ("Retry-After", "7"));
        var semCabecalho = await Chamar(HttpStatusCode.TooManyRequests, OpenAiBodies.Error("rate_limit_exceeded"));

        Assert.Equal(TimeSpan.FromSeconds(7), limite.Error!.RetryAfter);
        Assert.Null(cota.Error!.RetryAfter); // cota e cobrança não passam esperando
        Assert.Null(semCabecalho.Error!.RetryAfter);
    }

    [Fact]
    public async Task O_Retry_After_em_data_vira_o_tempo_que_falta_e_nunca_negativo()
    {
        var agora = DateTimeOffset.UtcNow;
        var futuro = await Chamar(HttpStatusCode.ServiceUnavailable, OpenAiBodies.Error("overloaded"), ("Retry-After", agora.AddSeconds(30).ToString("R")));
        var passado = await Chamar(HttpStatusCode.ServiceUnavailable, OpenAiBodies.Error("overloaded"), ("Retry-After", agora.AddMinutes(-5).ToString("R")));

        Assert.InRange(futuro.Error!.RetryAfter!.Value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(31));
        Assert.Equal(TimeSpan.Zero, passado.Error!.RetryAfter);
    }

    // Tempo limite, cancelamento e rede

    [Fact]
    public async Task Tempo_limite_esgotado_e_falha_transitoria_de_consumo_desconhecido_e_nao_excecao()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeHandler.Json(HttpStatusCode.OK, OpenAiBodies.Ok("nunca chega"));
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
            return FakeHandler.Json(HttpStatusCode.OK, OpenAiBodies.Ok("nunca chega"));
        };
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Requests.Request(timeout: TimeSpan.FromSeconds(20)), cancelamento.Token));
    }

    [Fact]
    public async Task Falha_de_rede_e_transitoria_de_consumo_desconhecido_e_a_mensagem_da_excecao_nunca_vaza()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = (captura, _) => throw new HttpRequestException($"falha ao conectar com {captura.Authorization} em api.openai.com");

        var resultado = await provider.CompleteAsync(Requests.Request(), default);

        Assert.Equal((AiErrorKind.Transient, "network-error", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.DoesNotContain(Requests.Canary, resultado.ToString());
    }

    // Sigilo

    [Theory]
    [InlineData(401, "invalid_api_key")]
    [InlineData(400, "invalid_request_error")]
    [InlineData(500, "server_error")]
    public async Task A_mensagem_do_provedor_que_ecoa_a_credencial_nunca_chega_ao_resultado(int status, string codigo)
    {
        var mensagem = $"Incorrect API key provided: {Requests.Canary}. You can find your API key at ...";

        var resultado = await Chamar((HttpStatusCode)status, OpenAiBodies.Error(codigo, null, mensagem));

        Assert.DoesNotContain(Requests.Canary, resultado.ToString());
        Assert.DoesNotContain(Requests.Canary, resultado.Error!.Code ?? string.Empty);
        Assert.DoesNotContain("Incorrect API key", resultado.ToString());
    }

    [Fact]
    public async Task Um_codigo_de_erro_fora_do_formato_estavel_e_descartado_em_vez_de_repetido()
    {
        var resultado = await Chamar(HttpStatusCode.BadRequest, OpenAiBodies.Error($"código com {Requests.Canary} e espaços!", "Tipo Inválido"));

        Assert.Equal("http-400", resultado.Error!.Code);
        Assert.DoesNotContain(Requests.Canary, resultado.ToString());
    }

    // O contrato: uma resposta OU uma falha, nunca as duas nem nenhuma

    [Theory]
    [InlineData(200, """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"oi"}]}],"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData(200, "lixo")]
    [InlineData(200, """{"status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData(200, """{"status":"incomplete","output":[],"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData(400, """{"error":{"code":"x","message":"y"}}""")]
    [InlineData(401, "")]
    [InlineData(429, """{"error":{"code":"insufficient_quota"}}""")]
    [InlineData(503, "<html>erro</html>")]
    [InlineData(302, "")]
    [InlineData(418, "{}")]
    public async Task Todo_resultado_tem_exatamente_uma_resposta_ou_uma_falha_e_sempre_uma_certeza_valida(int status, string corpo)
    {
        var resultado = await Chamar((HttpStatusCode)status, corpo);

        Assert.True((resultado.Completion is null) != (resultado.Error is null));
        Assert.True(Enum.IsDefined(resultado.UsageCertainty));
        if (resultado.Completion is not null)
        {
            Assert.Equal(AiUsageCertainty.Reported, resultado.UsageCertainty);
        }
    }
}
