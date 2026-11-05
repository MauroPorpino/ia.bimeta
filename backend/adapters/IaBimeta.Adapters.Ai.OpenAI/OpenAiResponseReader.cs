using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// Traduz a resposta HTTP da Responses API para o resultado normalizado do contrato (ADR 0017, seção C e Anexo A.1). Duas regras valem acima do resto:
/// <list type="bullet">
/// <item>O CONSUMO nunca é presumido. Resposta com <c>usage</c> válido vale o que ele informa (<see cref="AiUsageCertainty.Reported"/>). Falha em que o provedor pode ter processado e cobrado (tempo esgotado, queda,
/// 429, 5xx, corpo inválido, resposta sem <c>usage</c>) é <see cref="AiUsageCertainty.Unknown"/>: o AiGateway cobra a reserva e pode reconciliar depois. <see cref="AiUsageCertainty.NoneIncurred"/> só com
/// evidência: o provedor REJEITOU o pedido antes de gerar (4xx de validação, de credencial ou de tamanho) e devolveu o corpo de erro completo.</item>
/// <item>Nada do conteúdo do provedor é repetido: o código estável vem só de <c>error.code</c> e <c>error.type</c> (saneados), nunca da mensagem, que pode ecoar parte da credencial ou do pedido.</item>
/// </list>
/// </summary>
internal static class OpenAiResponseReader
{
    private static readonly Regex CodePattern = new("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Os códigos de <c>error.code</c> que dizem "cobrança, gasto ou crédito esgotados": NÃO se tenta de novo, mesmo que o provedor sugira uma espera (Anexo A.1, item 4).</summary>
    private static readonly HashSet<string> QuotaCodes = new(StringComparer.Ordinal)
    {
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
        "insufficient_quota",
    };

    /// <summary>Os estados HTTP de REJEIÇÃO antes da geração: com o corpo de erro completo, é evidência de que nada foi cobrado.</summary>
    private static readonly HashSet<int> AdmissionRejections = new() { 400, 401, 402, 403, 404, 413, 422 };

    public static AiCompletionResult Read(int status, byte[] body, string? providerRequestId, TimeSpan latency, TimeSpan? retryAfter)
    {
        try
        {
            return status is >= 200 and < 300
                ? ReadSuccess(body, providerRequestId, latency)
                : ReadHttpError(status, body, providerRequestId, latency, retryAfter);
        }
        catch (InvalidOperationException)
        {
            // Um escape de substituto UTF-16 sem par no texto do provedor faz o leitor de JSON lançar: a resposta é inválida (e o consumo, desconhecido), nunca uma exceção para o AiGateway.
            return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency, providerRequestId);
        }
    }

    // Sucesso (HTTP 2xx)

    private static AiCompletionResult ReadSuccess(byte[] body, string? requestId, TimeSpan latency)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency, requestId);
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency, requestId);
            }

            var estado = String(raiz, "status");

            // O processamento falhou do lado do provedor, apesar do HTTP 200: o código de erro do corpo decide; o consumo vale só se ele o informou.
            if (estado == "failed")
            {
                var consumoInformado = TryReadUsage(raiz, out var usoDaFalha);
                var codigo = ErrorCode(raiz);
                var tipo = codigo is "rate_limit_exceeded" or "server_error" or null ? AiErrorKind.Transient : AiErrorKind.InvalidRequest;
                return consumoInformado
                    ? AiCompletionResult.Failure(new AiError(tipo, null, codigo ?? "response-failed"), latency, usoDaFalha, requestId, AiUsageCertainty.Reported)
                    : Fail(tipo, codigo ?? "response-failed", AiUsageCertainty.Unknown, latency, requestId);
            }

            if (estado is not ("completed" or "incomplete"))
            {
                return Fail(AiErrorKind.Transient, "unexpected-status", AiUsageCertainty.Unknown, latency, requestId);
            }

            // Sem o consumo não há resposta: a contabilidade do tenant não aceita "gastou não sei quanto" como sucesso. A resposta é descartada e a chamada vira falha de consumo desconhecido
            // (o AiGateway cobra a reserva), que o Agents trata como falha transitória da tentativa.
            if (!TryReadUsage(raiz, out var uso))
            {
                return Fail(AiErrorKind.Transient, "missing-usage", AiUsageCertainty.Unknown, latency, requestId);
            }

            var motivo = estado == "incomplete" ? IncompleteReason(raiz) : null;
            var (texto, recusou) = ReadOutput(raiz);

            if (recusou || motivo == "content_filter")
            {
                // O modelo recusou (ou o filtro cortou): os tokens foram cobrados. Nenhum texto do modelo é repassado.
                return AiCompletionResult.Success(new AiCompletion(string.Empty, AiStopReason.Refused), uso, latency, requestId);
            }

            if (estado == "incomplete")
            {
                return AiCompletionResult.Success(new AiCompletion(texto, AiStopReason.Truncated), uso, latency, requestId);
            }

            // Concluída sem nenhum texto de saída: os tokens foram gastos (consumo conhecido), mas não há o que responder.
            return string.IsNullOrEmpty(texto)
                ? AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, "empty-output"), latency, uso, requestId, AiUsageCertainty.Reported)
                : AiCompletionResult.Success(new AiCompletion(texto, AiStopReason.Completed), uso, latency, requestId);
        }
    }

    /// <summary>Só as partes <c>output_text</c> dos itens <c>message</c>, e se alguma é uma recusa. O raciocínio e qualquer outro item são ignorados: nunca entram na resposta.</summary>
    private static (string Text, bool Refused) ReadOutput(JsonElement raiz)
    {
        var texto = new StringBuilder();
        var recusou = false;
        if (!raiz.TryGetProperty("output", out var saida) || saida.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, false);
        }

        foreach (var item in saida.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || String(item, "type") != "message" || !item.TryGetProperty("content", out var partes) || partes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var parte in partes.EnumerateArray())
            {
                switch (parte.ValueKind == JsonValueKind.Object ? String(parte, "type") : null)
                {
                    case "output_text":
                        texto.Append(String(parte, "text"));
                        break;
                    case "refusal":
                        recusou = true;
                        break;
                }
            }
        }

        return (texto.ToString(), recusou);
    }

    private static string? IncompleteReason(JsonElement raiz) =>
        raiz.TryGetProperty("incomplete_details", out var detalhes) && detalhes.ValueKind == JsonValueKind.Object ? String(detalhes, "reason") : null;

    /// <summary>
    /// O consumo normalizado: entrada total (inclui o que veio do cache), tokens em cache, escrita de cache (a OpenAI não cobra à parte: zero), saída (<b>inclui</b> o raciocínio) e o raciocínio quando informado.
    /// Falso se <c>usage</c> falta, ou se algum número obrigatório falta ou é negativo.
    /// </summary>
    internal static bool TryReadUsage(JsonElement raiz, out AiUsage uso)
    {
        uso = AiUsage.None;
        if (!raiz.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object || !TryInt(u, "input_tokens", out var entrada) || !TryInt(u, "output_tokens", out var saida))
        {
            return false;
        }

        var emCache = 0;
        if (u.TryGetProperty("input_tokens_details", out var detalhesDaEntrada) && detalhesDaEntrada.ValueKind == JsonValueKind.Object && TryInt(detalhesDaEntrada, "cached_tokens", out var cache))
        {
            emCache = Math.Min(cache, entrada);
        }

        int? raciocinio = null;
        if (u.TryGetProperty("output_tokens_details", out var detalhesDaSaida) && detalhesDaSaida.ValueKind == JsonValueKind.Object && TryInt(detalhesDaSaida, "reasoning_tokens", out var pensou))
        {
            raciocinio = Math.Min(pensou, saida);
        }

        uso = new AiUsage(entrada, emCache, 0, saida, raciocinio);
        return true;
    }

    // Erro HTTP

    private static AiCompletionResult ReadHttpError(int status, byte[] body, string? requestId, TimeSpan latency, TimeSpan? retryAfter)
    {
        var (codigoDoProvedor, tipoDoProvedor, completo) = ReadErrorBody(body);
        var codigo = codigoDoProvedor ?? tipoDoProvedor ?? $"http-{status}";
        var quota = (codigoDoProvedor is not null && QuotaCodes.Contains(codigoDoProvedor)) || tipoDoProvedor == "insufficient_quota";

        var tipo = status switch
        {
            401 or 403 => AiErrorKind.InvalidCredentials,
            402 => AiErrorKind.QuotaOrBilling,
            413 => AiErrorKind.ContextTooLong,
            400 when codigoDoProvedor == "context_length_exceeded" => AiErrorKind.ContextTooLong,
            429 => quota ? AiErrorKind.QuotaOrBilling : AiErrorKind.Transient,
            408 or 409 => AiErrorKind.Transient,
            >= 500 => AiErrorKind.Transient,
            _ => AiErrorKind.InvalidRequest,
        };

        // Cota e cobrança não passam esperando: a espera sugerida é descartada.
        var espera = tipo == AiErrorKind.Transient ? retryAfter : null;

        // NoneIncurred só com evidência: rejeição ANTES de gerar, com o corpo de erro completo do provedor. 429, 5xx, 408, 409 e qualquer corpo ilegível ficam desconhecidos.
        var certeza = AdmissionRejections.Contains(status) && completo ? AiUsageCertainty.NoneIncurred : AiUsageCertainty.Unknown;
        return AiCompletionResult.Failure(new AiError(tipo, espera, codigo), latency, null, requestId, certeza);
    }

    /// <summary>
    /// O código e o tipo do erro do provedor, saneados, e se o corpo é um erro COMPLETO (objeto <c>error</c> com algum campo de texto). A mensagem nunca é lida para fora: ela pode ecoar parte da credencial
    /// ou do pedido.
    /// </summary>
    private static (string? Code, string? Type, bool Complete) ReadErrorBody(byte[] body)
    {
        try
        {
            using var documento = JsonDocument.Parse(body);
            if (documento.RootElement.ValueKind != JsonValueKind.Object
                || !documento.RootElement.TryGetProperty("error", out var erro)
                || erro.ValueKind != JsonValueKind.Object)
            {
                return (null, null, false);
            }

            var codigo = Sanitize(String(erro, "code"));
            var tipo = Sanitize(String(erro, "type"));
            var completo = codigo is not null || tipo is not null || !string.IsNullOrEmpty(String(erro, "message"));
            return (codigo, tipo, completo);
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
    }

    private static string? ErrorCode(JsonElement raiz) =>
        raiz.TryGetProperty("error", out var erro) && erro.ValueKind == JsonValueKind.Object ? Sanitize(String(erro, "code")) : null;

    // Auxiliares

    internal static AiCompletionResult Fail(AiErrorKind tipo, string codigo, AiUsageCertainty certeza, TimeSpan latency, string? requestId, TimeSpan? retryAfter = null) =>
        AiCompletionResult.Failure(new AiError(tipo, retryAfter, codigo), latency, null, requestId, certeza);

    /// <summary>Um código estável e seguro: minúsculas, números e <c>._-</c>, até 100 caracteres. Qualquer outra coisa é descartada.</summary>
    internal static string? Sanitize(string? valor)
    {
        if (string.IsNullOrEmpty(valor))
        {
            return null;
        }

        var minusculo = valor.ToLowerInvariant();
        return CodePattern.IsMatch(minusculo) ? minusculo : null;
    }

    private static string? String(JsonElement objeto, string nome) =>
        objeto.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static bool TryInt(JsonElement objeto, string nome, out int valor)
    {
        valor = 0;
        return objeto.TryGetProperty(nome, out var numero) && numero.ValueKind == JsonValueKind.Number && numero.TryGetInt32(out valor) && valor >= 0;
    }
}
