using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// Monta o pedido a <c>POST /api/chat</c> do Ollama, sempre SEM streaming, sem ferramentas e sem imagens. O teto de saída que o AiGateway já limitou vai em <c>options.num_predict</c>; a janela de
/// contexto, a temperatura e o raciocínio vêm do PERFIL do modelo (infraestrutura). O Ollama local não tem chave: a credencial do tenant (vazia) nunca é lida aqui, e o token de proxy, se houver, vai só
/// no cabeçalho DESTE pedido.
/// </summary>
internal static class OllamaChatRequestFactory
{
    /// <summary>O pedido pronto, ou nulo com o problema (perfil não configurado, esquema ausente ou inválido): nada é enviado.</summary>
    public static HttpRequestMessage? Create(AiCompletionRequest request, OllamaChatOptions options, out string? problem, out int numCtx)
    {
        problem = null;
        numCtx = 0;

        if (!options.Profiles.TryGetValue(request.Profile ?? string.Empty, out var perfil))
        {
            // O contexto de geração é o que impede o truncamento silencioso: sem perfil conhecido, nenhum valor é presumido.
            problem = "profile-not-configured";
            return null;
        }

        numCtx = perfil.NumCtx;
        var sistema = Safe(request.SystemInstruction);
        JsonNode? formato = null;

        if (request.OutputMode == AiOutputMode.StructuredJson)
        {
            if (request.StructuredOutput is null)
            {
                problem = "structured-output-missing";
                return null;
            }

            try
            {
                formato = JsonNode.Parse(request.StructuredOutput.JsonSchema);
            }
            catch (JsonException)
            {
                formato = null;
            }

            if (formato is not JsonObject)
            {
                problem = "structured-output-schema-invalid";
                return null;
            }

            if (options.GroundStructuredOutputInPrompt)
            {
                sistema += "\n\nResponda SOMENTE com um objeto JSON válido que obedeça a este esquema (sem texto antes ou depois):\n" + formato.ToJsonString();
            }
        }

        var mensagens = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = sistema } };
        foreach (var m in request.Messages)
        {
            mensagens.Add(new JsonObject { ["role"] = m.Role == AiMessageRole.User ? "user" : "assistant", ["content"] = Safe(m.Text) });
        }

        // Orçamento de contexto (HEURÍSTICO, sem tokenizador): o texto que o ADAPTADOR leva ao servidor (sistema, já com o esquema acrescentado, mais o histórico; o esquema de `format` também conta quando não está
        // no sistema, porque não se sabe se o servidor o põe no prompt) mais a reserva da resposta precisa caber na janela do perfil. Recusa só o que é CLARAMENTE grande demais; um pedido aceito não está provado dentro da janela.
        var caracteres = (long)sistema.Length + request.Messages.Sum(m => (long)(m.Text?.Length ?? 0));
        if (formato is not null && !options.GroundStructuredOutputInPrompt)
        {
            caracteres += formato.ToJsonString().Length;
        }

        var estimada = (long)Math.Ceiling(caracteres / options.CharactersPerToken * options.InputSafetyMargin) + ((long)request.Messages.Count + 1) * options.PerMessageOverheadTokens + options.RequestOverheadTokens;
        if (estimada + request.MaxOutputTokens > perfil.NumCtx)
        {
            problem = "context-budget-exceeded";
            return null;
        }

        var corpo = new JsonObject
        {
            ["model"] = Safe(request.ModelId),
            ["stream"] = false,
            ["messages"] = mensagens,
            ["options"] = new JsonObject
            {
                ["num_predict"] = request.MaxOutputTokens,
                ["num_ctx"] = perfil.NumCtx,
                ["temperature"] = perfil.Temperature,
            },
        };

        if (formato is not null)
        {
            corpo["format"] = formato;
        }

        if (perfil.Think is { } pensar)
        {
            corpo["think"] = pensar;
        }

        if (options.KeepAlive is { } manter)
        {
            corpo["keep_alive"] = manter is "-1" or "0" ? JsonValue.Create(int.Parse(manter, System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(manter);
        }

        var mensagemHttp = new HttpRequestMessage(HttpMethod.Post, options.ChatUri)
        {
            Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        mensagemHttp.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(options.BearerToken))
        {
            mensagemHttp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
        }

        return mensagemHttp;
    }

    /// <summary>O texto como o JSON o aceita: um substituto UTF-16 sem par vira U+FFFD, como no resto da plataforma.</summary>
    internal static string Safe(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        StringBuilder? corrigido = null;
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (char.IsHighSurrogate(c) && i + 1 < texto.Length && char.IsLowSurrogate(texto[i + 1]))
            {
                corrigido?.Append(c).Append(texto[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                corrigido ??= new StringBuilder(texto, 0, i, texto.Length);
                corrigido.Append('�');
                continue;
            }

            corrigido?.Append(c);
        }

        return corrigido?.ToString() ?? texto;
    }
}
