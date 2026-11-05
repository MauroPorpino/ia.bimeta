using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.Ai.OpenAI;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.Adapters.Ai.OpenAI.Testing;

/// <summary>O que o servidor simulado recebeu de UMA requisição (o conteúdo é lido na hora: o adaptador o descarta depois).</summary>
internal sealed record Captured(HttpMethod Method, Uri Uri, string? Authorization, string? ClientRequestId, string Accept, string Body)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>O "servidor da OpenAI": responde o que o teste manda, registra cada requisição e honra o cancelamento. Nenhuma rede, nenhuma chave real.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<Captured> _requests = new();

    public Func<Captured, CancellationToken, Task<HttpResponseMessage>> Behavior { get; set; } = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, OpenAiBodies.Ok("ok")));

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
            request.Headers.TryGetValues("X-Client-Request-Id", out var ids) ? ids.FirstOrDefault() : null,
            string.Join(",", request.Headers.Accept.Select(a => a.MediaType)),
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

/// <summary>Os corpos que a Responses API devolve (a forma documentada em ADR 0017, Anexo A.1), montados por objeto: sempre JSON válido.</summary>
internal static class OpenAiBodies
{
    public static string Ok(string text, int input = 100, int cached = 20, int output = 30, int reasoning = 5) =>
        new JsonObject
        {
            ["id"] = "resp_1",
            ["status"] = "completed",
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() },
                new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
                }),
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = cached },
                ["output_tokens"] = output,
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning },
                ["total_tokens"] = input + output,
            },
        }.ToJsonString();

    public static string Error(string? code, string? type = null, string message = "mensagem do provedor") =>
        new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["param"] = null, ["code"] = code },
        }.ToJsonString();
}

internal static class Requests
{
    public const string Canary = "sk-CANARIO-DO-TENANT-A-9f3a1c";

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
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            "turno-1",
            "gpt-6-luna",
            profile,
            system,
            messages ?? new[] { new AiMessage(AiMessageRole.User, "Preciso de jejum?") },
            maxOutput,
            timeout ?? TimeSpan.FromSeconds(5),
            mode,
            mode == AiOutputMode.StructuredJson ? new AiStructuredOutput("agent_decision", DecisionSchema) : null,
            new AiCredential(secret));

    public static (OpenAiProvider Provider, FakeHandler Handler, HttpClient Http) Provider(Action<OpenAiOptions>? configure = null)
    {
        var opcoes = new OpenAiOptions();
        configure?.Invoke(opcoes);
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new OpenAiProvider(http, opcoes), handler, http);
    }
}
