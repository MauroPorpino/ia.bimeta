using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// Traduz a resposta HTTP do <c>/api/chat</c> (sem streaming) para o resultado normalizado do contrato. Regras acima do resto:
/// <list type="bullet">
/// <item>O CONSUMO nunca é presumido: <c>prompt_eval_count</c> e <c>eval_count</c> informados valem como entrada e saída (<see cref="AiUsageCertainty.Reported"/>). Se faltam, a resposta é descartada e a chamada vira
/// falha de consumo DESCONHECIDO (nunca zero, nunca um número inventado). O Ollama não cobra por token: este consumo é contagem de tokens, não dinheiro, e o custo estimado sai zero pelo preço zero do catálogo.</item>
/// <item>O raciocínio (<c>message.thinking</c> e blocos <c>&lt;think&gt;</c> no texto) NUNCA entra na resposta ao cliente.</item>
/// <item>Nada do conteúdo do servidor é repetido: o código estável vem do estado HTTP e de uma classificação por palavras-chave da mensagem de erro, que nunca sai daqui.</item>
/// </list>
/// </summary>
internal static class OllamaChatResponseReader
{

    /// <summary>Os estados HTTP de REJEIÇÃO antes da geração: com o corpo de erro, é evidência de que nada foi processado.</summary>
    private static readonly HashSet<int> AdmissionRejections = new() { 400, 401, 403, 404, 413, 422 };

    public static AiCompletionResult Read(int status, byte[] body, TimeSpan latency, TimeSpan? retryAfter, int numCtx)
    {
        try
        {
            return status is >= 200 and < 300
                ? ReadSuccess(body, latency, numCtx)
                : ReadHttpError(status, body, latency, retryAfter);
        }
        catch (InvalidOperationException)
        {
            // Um escape de substituto UTF-16 sem par no texto do servidor faz o leitor de JSON lançar: resposta inválida, consumo desconhecido.
            return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency);
        }
    }

    private static AiCompletionResult ReadSuccess(byte[] body, TimeSpan latency, int numCtx)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency);
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                return Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, latency);
            }

            // Um 200 com o corpo de erro (o servidor falhou no meio): o consumo é desconhecido.
            if (raiz.TryGetProperty("error", out _))
            {
                return Fail(AiErrorKind.Transient, "provider-error", AiUsageCertainty.Unknown, latency);
            }

            // Sem streaming, a resposta completa vem com done = true. Qualquer outra coisa é resposta incompleta.
            if (!raiz.TryGetProperty("done", out var feito) || feito.ValueKind != JsonValueKind.True)
            {
                return Fail(AiErrorKind.Transient, "incomplete-response", AiUsageCertainty.Unknown, latency);
            }

            var motivo = String(raiz, "done_reason");
            if (motivo == "load")
            {
                // O servidor só carregou o modelo, sem gerar: nada utilizável e nenhuma saída contada.
                return Fail(AiErrorKind.Transient, "model-load-only", AiUsageCertainty.Unknown, latency);
            }

            if (!TryReadUsage(raiz, out var uso))
            {
                return Fail(AiErrorKind.Transient, "missing-usage", AiUsageCertainty.Unknown, latency);
            }

            // O servidor trunca em silêncio um prompt acima do contexto (não documentado): se a entrada ocupou toda a janela, a resposta foi gerada sobre um prompt cortado e NÃO é usada.
            if (uso.InputTokens >= numCtx)
            {
                return AiCompletionResult.Failure(new AiError(AiErrorKind.ContextTooLong, null, "context-window-exceeded"), latency, uso, null, AiUsageCertainty.Reported);
            }

            // Raciocínio deixado no texto (P2-02): marcador incompleto, isolado ou no campo de resposta, ou saída só de raciocínio, é INUTILIZÁVEL (nada vai ao cliente). Os tokens informados são preservados.
            var limpeza = OllamaReasoningFilter.Clean(ReadContent(raiz), out var texto);
            if (limpeza != ReasoningOutcome.Clean)
            {
                return AiCompletionResult.Failure(
                    new AiError(AiErrorKind.InvalidRequest, null, limpeza == ReasoningOutcome.OnlyReasoning ? "reasoning-only" : "reasoning-leak"), latency, uso, null, AiUsageCertainty.Reported);
            }

            switch (motivo)
            {
                case "stop":
                    return string.IsNullOrEmpty(texto)
                        ? AiCompletionResult.Failure(new AiError(AiErrorKind.Transient, null, "empty-output"), latency, uso, null, AiUsageCertainty.Reported)
                        : AiCompletionResult.Success(new AiCompletion(texto, AiStopReason.Completed), uso, latency);

                case "length":
                    return AiCompletionResult.Success(new AiCompletion(texto, AiStopReason.Truncated), uso, latency);

                default:
                    // Ausente ou um valor novo: tokens contados, nada seguro de usar, e NÃO se tenta de novo (outra chamada seria outro processamento).
                    return AiCompletionResult.Failure(new AiError(AiErrorKind.InvalidRequest, null, "unexpected-done-reason"), latency, uso, null, AiUsageCertainty.Reported);
            }
        }
    }

    /// <summary>Só <c>message.content</c> (bruto). O campo <c>message.thinking</c> nunca é lido; os marcadores <c>think</c> no texto são tratados por <see cref="OllamaReasoningFilter"/>.</summary>
    private static string ReadContent(JsonElement raiz)
    {
        if (!raiz.TryGetProperty("message", out var mensagem) || mensagem.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return String(mensagem, "content") ?? string.Empty;
    }

    /// <summary>O consumo: entrada = <c>prompt_eval_count</c>, saída = <c>eval_count</c>. Falso se algum falta, não é número inteiro ou é negativo. Sem cache a declarar (o contrato recebe zero, que aqui é fato, não ausência).</summary>
    internal static bool TryReadUsage(JsonElement raiz, out AiUsage uso)
    {
        uso = AiUsage.None;
        if (!TryInt(raiz, "prompt_eval_count", out var entrada) || !TryInt(raiz, "eval_count", out var saida))
        {
            return false;
        }

        uso = new AiUsage(entrada, 0, 0, saida);
        return true;
    }

    // Erro HTTP

    private static AiCompletionResult ReadHttpError(int status, byte[] body, TimeSpan latency, TimeSpan? retryAfter)
    {
        var texto = ReadErrorText(body, out var completo);
        var memoria = texto is not null && (texto.Contains("memory", StringComparison.OrdinalIgnoreCase) || texto.Contains("out of resources", StringComparison.OrdinalIgnoreCase));
        var incompativel = texto is not null && texto.Contains("does not support", StringComparison.OrdinalIgnoreCase);

        AiErrorKind tipo;
        string codigo;
        switch (status)
        {
            case 401 or 403:
                tipo = AiErrorKind.InvalidCredentials;
                codigo = "unauthorized";
                break;
            case 404:
                tipo = AiErrorKind.InvalidRequest;
                codigo = "model-not-installed";
                break;
            case 400 when incompativel:
                tipo = AiErrorKind.InvalidRequest;
                codigo = "model-incompatible";
                break;
            case 413:
                tipo = AiErrorKind.ContextTooLong;
                codigo = "request-too-large";
                break;
            case 429 or 503:
                tipo = AiErrorKind.Transient;
                codigo = "server-busy";
                break;
            case 408 or 409:
                tipo = AiErrorKind.Transient;
                codigo = $"http-{status}";
                break;
            case >= 500 when memoria:
                tipo = AiErrorKind.Transient;
                codigo = "insufficient-memory";
                break;
            case >= 500:
                tipo = AiErrorKind.Transient;
                codigo = $"http-{status}";
                break;
            default:
                tipo = AiErrorKind.InvalidRequest;
                codigo = $"http-{status}";
                break;
        }

        var espera = tipo == AiErrorKind.Transient ? retryAfter : null;
        var certeza = AdmissionRejections.Contains(status) && completo ? AiUsageCertainty.NoneIncurred : AiUsageCertainty.Unknown;
        return AiCompletionResult.Failure(new AiError(tipo, espera, codigo), latency, null, null, certeza);
    }

    /// <summary>O texto do <c>{"error": "..."}</c> só para CLASSIFICAR; nunca é repetido. <paramref name="complete"/> diz se o corpo tinha a forma de erro do Ollama.</summary>
    private static string? ReadErrorText(byte[] body, out bool complete)
    {
        complete = false;
        try
        {
            using var documento = JsonDocument.Parse(body);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("error", out var erro) || erro.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            complete = true;
            return erro.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static AiCompletionResult Fail(AiErrorKind tipo, string codigo, AiUsageCertainty certeza, TimeSpan latency, TimeSpan? retryAfter = null) =>
        AiCompletionResult.Failure(new AiError(tipo, retryAfter, codigo), latency, null, null, certeza);

    private static string? String(JsonElement objeto, string nome) =>
        objeto.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static bool TryInt(JsonElement objeto, string nome, out int valor)
    {
        valor = 0;
        return objeto.TryGetProperty(nome, out var numero) && numero.ValueKind == JsonValueKind.Number && numero.TryGetInt32(out valor) && valor >= 0;
    }
}
