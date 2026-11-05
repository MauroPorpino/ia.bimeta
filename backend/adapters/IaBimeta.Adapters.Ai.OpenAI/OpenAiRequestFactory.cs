using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// Monta o pedido HTTP à Responses API (<c>POST /v1/responses</c>, ADR 0017 Anexo A.1). O teto de tokens de saída que o AiGateway já limitou vai SEMPRE em <c>max_output_tokens</c> (é ele que limita a saída
/// de fato); <c>store</c> vai sempre desligado; o contrato não tem <c>temperature</c> nem <c>top_p</c>, e o esforço de raciocínio vem do PERFIL do modelo. A credencial do tenant vai só no cabeçalho
/// <c>Authorization</c> DESTE pedido: nunca num cabeçalho padrão do <see cref="HttpClient"/> compartilhado, então duas chamadas de tenants diferentes nunca se misturam.
/// </summary>
internal static class OpenAiRequestFactory
{
    /// <summary>O pedido pronto, ou nulo com o problema (a credencial não tem formato de chave ou o esquema da decisão não é JSON): nada é enviado.</summary>
    public static HttpRequestMessage? Create(AiCompletionRequest request, OpenAiOptions options, out string? problem)
    {
        problem = null;

        // A chave de API é ASCII visível, sem espaço nem controle: qualquer outra coisa nunca vai a um cabeçalho (injeção de cabeçalho) e nunca é repetida em mensagem.
        if (!IsHeaderSafeToken(request.Credential.Secret))
        {
            problem = "credential-invalid-format";
            return null;
        }

        var corpo = new JsonObject
        {
            ["model"] = Safe(request.ModelId),
            ["instructions"] = Safe(request.SystemInstruction),
            ["input"] = Input(request.Messages),
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["store"] = false,
        };

        if (options.Profiles.TryGetValue(request.Profile ?? string.Empty, out var perfil) && perfil.ReasoningEffort is { } esforco)
        {
            corpo["reasoning"] = new JsonObject { ["effort"] = esforco };
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

            corpo["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = request.StructuredOutput.Name,
                    ["schema"] = esquema,
                    ["strict"] = true,
                },
            };
        }

        var mensagem = new HttpRequestMessage(HttpMethod.Post, options.ResponsesUri)
        {
            Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Credential.Secret);

        // O identificador da chamada acompanha a requisição (ASCII, até 512 caracteres): serve à correlação com o suporte do provedor. NÃO é chave de idempotência (a OpenAI não tem uma na geração).
        mensagem.Headers.TryAddWithoutValidation("X-Client-Request-Id", request.AiRequestId.ToString("N"));
        return mensagem;
    }

    private static JsonArray Input(IReadOnlyList<AiMessage> mensagens)
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
    /// O texto como o JSON o aceita: um substituto UTF-16 sem par (que o cliente pode ter mandado) faria o serializador lançar. Vira U+FFFD, como o resto da plataforma trata texto inválido, e a chamada
    /// segue (o modelo não ganha nada com o byte inválido, e a conversa não deve cair por causa dele).
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
