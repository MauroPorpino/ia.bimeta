using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Anthropic;

/// <summary>
/// Traduz a resposta HTTP da Messages API para o resultado normalizado do contrato. Duas regras valem acima do resto:
/// <list type="bullet">
/// <item>O CONSUMO nunca é presumido. Resposta com <c>usage</c> válido vale o que ele informa (<see cref="AiUsageCertainty.Reported"/>), com a entrada TOTAL somando <c>input_tokens</c>, <c>cache_creation_input_tokens</c> e
/// <c>cache_read_input_tokens</c> (a API os reporta separados). Falha em que o provedor pode ter processado e cobrado (tempo esgotado, queda, 429, 5xx, corpo inválido, resposta sem <c>usage</c>) é
/// <see cref="AiUsageCertainty.Unknown"/>. <see cref="AiUsageCertainty.NoneIncurred"/> só com evidência: o provedor REJEITOU o pedido antes de gerar (4xx de validação, credencial ou tamanho) com o corpo de erro completo.</item>
/// <item>Nada do conteúdo do provedor é repetido: o código estável vem só de <c>error.type</c> e <c>error.details.error_code</c> (saneados), nunca da mensagem, que pode ecoar parte da credencial ou do pedido.</item>
/// </list>
/// </summary>
internal static class AnthropicResponseReader
{
    private static readonly Regex CodePattern = new("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>O <c>error.details.error_code</c> do teto de gasto mensal do nível: 429 SEM <c>retry-after</c>, que só passa na virada do mês. NÃO se tenta de novo.</summary>
    private const string SpendLimitCode = "enforced_spend_limit_reached";

    /// <summary>O início do texto do 400 que a documentação descreve para o limite de gasto definido pelo próprio cliente. Só se compara: nunca se repete.</summary>
    private const string CustomerSpendLimitPrefix = "You have reached your specified API usage limits";

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
            if (raiz.ValueKind != JsonValueKind.Object || String(raiz, "type") != "message")
            {
                return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency, requestId);
            }

            // Sem o consumo não há resposta: a contabilidade do tenant não aceita "gastou não sei quanto" como sucesso. A resposta é descartada e a chamada vira falha de consumo desconhecido
            // (o AiGateway cobra a reserva).
            if (!TryReadUsage(raiz, out var uso))
            {
                return Fail(AiErrorKind.Transient, "missing-usage", AiUsageCertainty.Unknown, latency, requestId);
            }

            var motivo = String(raiz, "stop_reason");
            var texto = ReadText(raiz);

            switch (motivo)
            {
                case "refusal":
                    // O modelo recusou: os tokens foram cobrados (HTTP 200). Nenhum texto do modelo é repassado.
                    return AiCompletionResult.Success(new AiCompletion(string.Empty, AiStopReason.Refused), uso, latency, requestId);

                case "max_tokens":
                    return AiCompletionResult.Success(new AiCompletion(texto, AiStopReason.Truncated), uso, latency, requestId);

                case "model_context_window_exceeded":
                    return AiCompletionResult.Failure(new AiError(AiErrorKind.ContextTooLong, null, "model_context_window_exceeded"), latency, uso, requestId, AiUsageCertainty.Reported);

                case "end_turn" or "stop_sequence":
                    // Concluída sem nenhum texto: os tokens foram gastos (consumo conhecido), mas não há o que responder.
                    return string.IsNullOrEmpty(texto)
                        ? AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, "empty-output"), latency, uso, requestId, AiUsageCertainty.Reported)
                        : AiCompletionResult.Success(new AiCompletion(texto, AiStopReason.Completed), uso, latency, requestId);

                default:
                    // tool_use, pause_turn, ausente ou um valor novo: o adaptador não envia ferramentas nem sabe continuar um turno. Tokens cobrados, nada utilizável; NÃO se tenta de novo (outra chamada seria outra cobrança).
                    return AiCompletionResult.Failure(new AiError(AiErrorKind.InvalidRequest, null, "unexpected-stop-reason"), latency, uso, requestId, AiUsageCertainty.Reported);
            }
        }
    }

    /// <summary>Só os blocos <c>text</c>. O raciocínio (<c>thinking</c>, <c>redacted_thinking</c>) e qualquer outro bloco são ignorados: nunca entram na resposta.</summary>
    private static string ReadText(JsonElement raiz)
    {
        var texto = new StringBuilder();
        if (!raiz.TryGetProperty("content", out var conteudo) || conteudo.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var bloco in conteudo.EnumerateArray())
        {
            if (bloco.ValueKind == JsonValueKind.Object && String(bloco, "type") == "text")
            {
                texto.Append(String(bloco, "text"));
            }
        }

        return texto.ToString();
    }

    /// <summary>
    /// O consumo normalizado: entrada total (<c>input_tokens</c> + escrita no cache + leitura do cache, que a API reporta separados), leitura do cache, escrita no cache, saída (inclui o raciocínio) e o raciocínio
    /// quando informado. Falso se <c>usage</c> falta, ou se um número obrigatório falta ou é negativo.
    /// </summary>
    internal static bool TryReadUsage(JsonElement raiz, out AiUsage uso)
    {
        uso = AiUsage.None;
        if (!raiz.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object || !TryInt(u, "input_tokens", out var entrada) || !TryInt(u, "output_tokens", out var saida))
        {
            return false;
        }

        // Os campos de cache são opcionais (ausentes ou nulos valem zero); presentes e inválidos tornam o consumo inválido.
        if (!TryOptionalInt(u, "cache_creation_input_tokens", out var escritaNoCache) || !TryOptionalInt(u, "cache_read_input_tokens", out var leituraDoCache))
        {
            return false;
        }

        var total = (long)entrada + escritaNoCache + leituraDoCache;
        if (total > int.MaxValue)
        {
            return false;
        }

        int? raciocinio = null;
        if (u.TryGetProperty("output_tokens_details", out var detalhes) && detalhes.ValueKind == JsonValueKind.Object && TryInt(detalhes, "thinking_tokens", out var pensou))
        {
            raciocinio = Math.Min(pensou, saida);
        }

        uso = new AiUsage((int)total, leituraDoCache, escritaNoCache, saida, raciocinio);
        return true;
    }

    // Erro HTTP

    private static AiCompletionResult ReadHttpError(int status, byte[] body, string? requestId, TimeSpan latency, TimeSpan? retryAfter)
    {
        var erro = ReadErrorBody(body);
        var codigo = erro.ErrorCode ?? erro.Type ?? $"http-{status}";
        var tetoDeGasto = erro.ErrorCode == SpendLimitCode;

        var tipo = status switch
        {
            401 or 403 => AiErrorKind.InvalidCredentials,
            402 => AiErrorKind.QuotaOrBilling,
            413 => AiErrorKind.ContextTooLong,
            400 when erro.CustomerSpendLimit => AiErrorKind.QuotaOrBilling,
            429 => tetoDeGasto ? AiErrorKind.QuotaOrBilling : AiErrorKind.Transient,
            408 or 409 => AiErrorKind.Transient,
            >= 500 => AiErrorKind.Transient, // 500, 503, 504 (timeout_error) e 529 (overloaded_error)
            _ => AiErrorKind.InvalidRequest, // 400, 404 (modelo inexistente), 422 e demais 4xx
        };

        // Cota e cobrança não passam esperando: a espera sugerida é descartada.
        var espera = tipo == AiErrorKind.Transient ? retryAfter : null;

        // NoneIncurred só com evidência: rejeição ANTES de gerar, com o corpo de erro completo. 429, 5xx, 408, 409 e qualquer corpo ilegível ficam desconhecidos.
        var certeza = AdmissionRejections.Contains(status) && erro.Complete ? AiUsageCertainty.NoneIncurred : AiUsageCertainty.Unknown;
        return AiCompletionResult.Failure(new AiError(tipo, espera, codigo), latency, null, requestId ?? erro.RequestId, certeza);
    }

    private readonly record struct ErrorBody(string? Type, string? ErrorCode, bool Complete, bool CustomerSpendLimit, string? RequestId);

    /// <summary>
    /// O corpo de erro (<c>type: error</c>, <c>error.type</c>, <c>error.message</c>, <c>request_id</c>): o tipo e o <c>details.error_code</c> saneados, se é um erro COMPLETO e se a mensagem começa como a do
    /// limite de gasto do cliente. A mensagem nunca sai daqui.
    /// </summary>
    private static ErrorBody ReadErrorBody(byte[] body)
    {
        try
        {
            using var documento = JsonDocument.Parse(body);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || String(raiz, "type") != "error" || !raiz.TryGetProperty("error", out var erro) || erro.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var tipo = Sanitize(String(erro, "type"));
            string? codigo = null;
            if (erro.TryGetProperty("details", out var detalhes) && detalhes.ValueKind == JsonValueKind.Object)
            {
                codigo = Sanitize(String(detalhes, "error_code"));
            }

            var texto = String(erro, "message");
            var completo = tipo is not null || !string.IsNullOrEmpty(texto);
            var gasto = texto is not null && texto.StartsWith(CustomerSpendLimitPrefix, StringComparison.Ordinal);
            return new ErrorBody(tipo, codigo, completo, gasto, SanitizeId(String(raiz, "request_id")));
        }
        catch (JsonException)
        {
            return default;
        }
    }

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

    /// <summary>O identificador da requisição no provedor (<c>req_...</c>): ASCII seguro, até 100 caracteres, ou nulo.</summary>
    internal static string? SanitizeId(string? valor)
    {
        if (string.IsNullOrEmpty(valor) || valor.Length > 100)
        {
            return null;
        }

        foreach (var c in valor)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return null;
            }
        }

        return valor;
    }

    private static string? String(JsonElement objeto, string nome) =>
        objeto.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static bool TryInt(JsonElement objeto, string nome, out int valor)
    {
        valor = 0;
        return objeto.TryGetProperty(nome, out var numero) && numero.ValueKind == JsonValueKind.Number && numero.TryGetInt32(out valor) && valor >= 0;
    }

    private static bool TryOptionalInt(JsonElement objeto, string nome, out int valor)
    {
        valor = 0;
        if (!objeto.TryGetProperty(nome, out var numero) || numero.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return numero.ValueKind == JsonValueKind.Number && numero.TryGetInt32(out valor) && valor >= 0;
    }
}
