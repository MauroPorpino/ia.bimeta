using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Anthropic;

/// <summary>
/// Monta o pedido HTTP à Messages API (<c>POST /v1/messages</c>). O teto de tokens de saída que o AiGateway já limitou vai SEMPRE em <c>max_tokens</c>; o esforço vem do PERFIL do modelo; o adaptador nunca
/// envia os parâmetros de amostragem (removidos na versão 2026-10-21 da API: HTTP 400), o bloco de raciocínio, ferramentas, streaming nem prefill (a última mensagem é sempre do usuário, regra do contrato).
/// A credencial do tenant vai só no cabeçalho <c>Authorization</c> DESTE pedido: nunca num cabeçalho padrão do <see cref="HttpClient"/> compartilhado, então duas chamadas de tenants diferentes nunca se misturam.
/// </summary>
internal static class AnthropicRequestFactory
{
    /// <summary>O pedido pronto, ou nulo com o problema (credencial sem formato de chave, ou esquema da decisão que não é JSON): nada é enviado.</summary>
    public static HttpRequestMessage? Create(AiCompletionRequest request, AnthropicOptions options, out string? problem)
    {
        problem = null;

        // A chave de API é ASCII visível, sem espaço nem controle: qualquer outra coisa nunca vai a um cabeçalho (injeção de cabeçalho) e nunca é repetida em mensagem.
        if (!IsHeaderSafeToken(request.Credential.Secret))
        {
            problem = "credential-invalid-format";
            return null;
        }

        options.Profiles.TryGetValue(request.Profile ?? string.Empty, out var perfil);

        var corpo = new JsonObject
        {
            ["model"] = Safe(request.ModelId),
            ["max_tokens"] = request.MaxOutputTokens,
            ["system"] = SystemBlock(request.SystemInstruction, perfil?.CacheSystemPrompt == true),
            ["messages"] = Messages(request.Messages),
        };

        JsonObject? saida = null;
        if (perfil?.Effort is { } esforco)
        {
            saida = new JsonObject { ["effort"] = esforco };
        }

        if (request.OutputMode == AiOutputMode.StructuredJson)
        {
            if (request.StructuredOutput is null)
            {
                problem = "structured-output-missing";
                return null;
            }

            JsonNode? esquema;
            try
            {
                esquema = JsonNode.Parse(request.StructuredOutput.JsonSchema);
            }
            catch (JsonException)
            {
                esquema = null;
            }

            if (esquema is not JsonObject)
            {
                problem = "structured-output-schema-invalid";
                return null;
            }

            // A saída estruturada nativa: o JSON volta como bloco de texto. O nome do esquema não existe nesta API.
            saida ??= new JsonObject();
            saida["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = esquema };
        }

        if (saida is not null)
        {
            corpo["output_config"] = saida;
        }

        var mensagem = new HttpRequestMessage(HttpMethod.Post, options.MessagesUri)
        {
            Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential.Secret);
        mensagem.Headers.TryAddWithoutValidation("anthropic-version", options.ApiVersion);
        return mensagem;
    }

    private static JsonNode SystemBlock(string instrucao, bool cache)
    {
        var texto = Safe(instrucao);
        if (!cache)
        {
            return JsonValue.Create(texto)!;
        }

        return new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = texto,
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        });
    }

    private static JsonArray Messages(IReadOnlyList<AiMessage> mensagens)
    {
        var lista = new JsonArray();
        foreach (var m in mensagens)
        {
            lista.Add(new JsonObject
            {
                ["role"] = m.Role == AiMessageRole.User ? "user" : "assistant",
                ["content"] = Safe(m.Text),
            });
        }

        return lista;
    }

    internal static bool IsHeaderSafeToken(string? valor)
    {
        if (string.IsNullOrEmpty(valor) || valor.Length > 4096)
        {
            return false;
        }

        foreach (var c in valor)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// O texto como o JSON o aceita: um substituto UTF-16 sem par faria o serializador lançar. Vira U+FFFD, como o resto da plataforma trata texto inválido, e a chamada segue.
    /// </summary>
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
