using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.Ai.Anthropic;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.Anthropic.Testing;

/// <summary>O que o servidor simulado recebeu de UMA requisição (o conteúdo é lido na hora: o adaptador o descarta depois).</summary>
internal sealed record Captured(HttpMethod Method, Uri Uri, string? Authorization, string? ApiKeyHeader, string? Version, string Accept, string ContentType, string Body)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>O "servidor da Anthropic": responde o que o teste manda, registra cada requisição e honra o cancelamento. Nenhuma rede, nenhuma chave real.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<Captured> _requests = new();

    public Func<Captured, CancellationToken, Task<HttpResponseMessage>> Behavior { get; set; } = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, AnthropicBodies.Ok("ok")));

    public IReadOnlyList<Captured> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var capturada = new Captured(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("x-api-key", out var chaves) ? chaves.FirstOrDefault() : null,
            request.Headers.TryGetValues("anthropic-version", out var versoes) ? versoes.FirstOrDefault() : null,
            string.Join(",", request.Headers.Accept.Select(a => a.MediaType)),
            request.Content?.Headers.ContentType?.MediaType ?? string.Empty,
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        lock (_gate)
        {
            _requests.Add(capturada);
        }

        return await Behavior(capturada, cancellationToken);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var resposta = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (nome, valor) in headers)
        {
            resposta.Headers.TryAddWithoutValidation(nome, valor);
        }

        return resposta;
    }

    public FakeHandler Returns(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        Behavior = (_, _) => Task.FromResult(Json(status, body, headers));
        return this;
    }
}

/// <summary>Os corpos que a Messages API devolve (a forma documentada em 27/11/2026), montados por objeto: sempre JSON válido.</summary>
internal static class AnthropicBodies
{
    public static string Ok(string text, int input = 80, int cacheRead = 15, int cacheWrite = 5, int output = 30, int thinking = 5, string stopReason = "end_turn", bool withThinkingBlock = true)
    {
        var conteudo = new JsonArray();
        if (withThinkingBlock)
        {
            conteudo.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = "raciocinio interno que nunca vai ao cliente", ["signature"] = "sig" });
        }

        conteudo.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return new JsonObject
        {
            ["id"] = "msg_1",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-haiku-5-5",
            ["content"] = conteudo,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["cache_creation_input_tokens"] = cacheWrite,
                ["cache_read_input_tokens"] = cacheRead,
                ["output_tokens"] = output,
                ["output_tokens_details"] = new JsonObject { ["thinking_tokens"] = thinking },
                ["service_tier"] = "standard",
            },
        }.ToJsonString();
    }

    public static string Error(string? type, string message = "mensagem do provedor", string? errorCode = null, string? requestId = "req_011CSHoEeqs5C35K2UUqR7Fy")
    {
        var erro = new JsonObject { ["type"] = type, ["message"] = message };
        if (errorCode is not null)
        {
            erro["details"] = new JsonObject { ["error_code"] = errorCode };
        }

        return new JsonObject { ["type"] = "error", ["error"] = erro, ["request_id"] = requestId }.ToJsonString();
    }
}

internal static class Requests
{
    public const string Canary = "sk-ant-CANARIO-DO-TENANT-A-9f3a1c";

    public const string DecisionSchema =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"reply\",\"handoff\"]}},\"required\":[\"action\"],\"additionalProperties\":false}";

    public static AiCompletionRequest Request(
        string secret = Canary,
        int maxOutput = 300,
        TimeSpan? timeout = null,
        AiOutputMode mode = AiOutputMode.Text,
        string profile = "atendimento-rapido",
        string system = "Você é a atendente.",
        IReadOnlyList<AiMessage>? messages = null,
        Guid? id = null,
        string model = "claude-haiku-5-5") =>
        new(
            id ?? Guid.NewGuid(),
            "turno-1",
            model,
            profile,
            system,
            messages ?? new[] { new AiMessage(AiMessageRole.User, "Preciso de jejum?") },
            maxOutput,
            timeout ?? TimeSpan.FromSeconds(5),
            mode,
            mode == AiOutputMode.StructuredJson ? new AiStructuredOutput("agent_decision", DecisionSchema) : null,
            new AiCredential(secret));

    public static (AnthropicProvider Provider, FakeHandler Handler, HttpClient Http) Provider(Action<AnthropicOptions>? configure = null)
    {
        var opcoes = new AnthropicOptions();
        configure?.Invoke(opcoes);
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new AnthropicProvider(http, opcoes), handler, http);
    }
}
