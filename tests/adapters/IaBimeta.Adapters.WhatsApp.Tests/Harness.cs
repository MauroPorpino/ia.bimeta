using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp.Testing;

/// <summary>O que o servidor simulado recebeu de UMA requisição (o conteúdo é lido na hora: o adaptador o descarta depois).</summary>
internal sealed record Captured(HttpMethod Method, Uri Uri, string? Authorization, string Accept, string? ContentType, string Body, byte[]? Raw = null)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>O "servidor da Graph API": responde o que o teste manda, registra cada requisição e honra o cancelamento. Nenhuma rede, nenhum token real.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<Captured> _requests = new();

    public Func<Captured, CancellationToken, Task<HttpResponseMessage>> Behavior { get; set; } = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, SendBodies.Ok()));

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
            string.Join(",", request.Headers.Accept.Select(a => a.MediaType)),
            request.Content?.Headers.ContentType?.ToString(),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken));
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

/// <summary>Um relógio de teste com instante fixo (a espera do <c>Retry-After</c> em data).</summary>
internal sealed class FixedClock(DateTimeOffset agora) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => agora;
}

/// <summary>Um fluxo sem posição nem tamanho: o <c>Content-Length</c> da resposta fica desconhecido (leitura limitada em blocos).</summary>
internal sealed class ForwardOnlyStream(byte[] bytes) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, bytes.Length - _position);
        Array.Copy(bytes, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Os corpos de resposta do envio (a forma documentada), montados por objeto: sempre JSON válido.</summary>
internal static class SendBodies
{
    public const string MessageId = "wamid.HBgLMTY0NjcwNDM1OTUVAgARGBI1RjQyNUE3NEYxMzAzMzQ5MkEA";

    public static string Ok(string id = MessageId) =>
        new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["contacts"] = new JsonArray(new JsonObject { ["input"] = "5511999990001", ["wa_id"] = "5511999990001" }),
            ["messages"] = new JsonArray(new JsonObject { ["id"] = id }),
        }.ToJsonString();

    /// <summary>O corpo de erro documentado: <c>error.message</c>, <c>type</c>, <c>code</c>, <c>error_data.details</c>, <c>fbtrace_id</c>. A mensagem e os detalhes são livres (podem ecoar dados).</summary>
    public static string Error(int code, string message = "(#0) mensagem livre do provedor", string details = "detalhes livres do provedor") =>
        new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "OAuthException",
                ["code"] = code,
                ["error_data"] = new JsonObject { ["messaging_product"] = "whatsapp", ["details"] = details },
                ["fbtrace_id"] = "AbCdEfGh",
            },
        }.ToJsonString();
}

/// <summary>Os pedidos de envio e o adaptador com HTTP simulado.</summary>
internal static class Sends
{
    public const string Canary = "EAAG-CANARIO-DO-TENANT-A-9f3a1c";
    public const string PhoneNumberId = "106540352242922";
    public const string Recipient = "5511999990001";
    public const string Callback = "msg:6f1c2d3e-0001";

    public static ProviderSendRequest Request(
        string token = Canary,
        string phone = PhoneNumberId,
        string to = Recipient,
        string text = "Olá! Você precisa de jejum de 8 horas.",
        string? callback = Callback) =>
        new(token, phone, to, text, callback);

    public static (WhatsAppCloudAdapter Adapter, FakeHandler Handler, HttpClient Http) Adapter(Action<WhatsAppOptions>? configure = null, TimeProvider? clock = null)
    {
        var opcoes = new WhatsAppOptions();
        configure?.Invoke(opcoes);
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new WhatsAppCloudAdapter(http, opcoes, clock), handler, http);
    }

    public static WhatsAppCloudAdapter Plain() => Adapter().Adapter;
}

/// <summary>A assinatura do webhook, calculada como a Meta o faz: HMAC-SHA256 dos BYTES do corpo com o App Secret, em hexadecimal minúsculo.</summary>
internal static class Signatures
{
    public const string AppSecret = "segredo-do-aplicativo-FICTICIO-0001";

    public static string Header(byte[] body, string secret = AppSecret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
}

/// <summary>Os webhooks da Meta, montados por objeto (a forma documentada em <c>webhooks/reference/messages</c> e <c>/status</c>). Cada chamada devolve nós novos (um nó JSON só tem um pai).</summary>
internal static class Webhooks
{
    public const string Phone = "106540352242922";
    public const string OtherPhone = "209876543210987";

    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    /// <summary>O envelope <c>whatsapp_business_account</c> com uma entrada por valor (<c>entry[].changes[0]</c> de campo <c>messages</c>).</summary>
    public static JsonObject Envelope(params JsonObject[] valores)
    {
        var entradas = new JsonArray();
        var i = 0;
        foreach (var valor in valores)
        {
            entradas.Add(new JsonObject
            {
                ["id"] = $"waba-{++i}",
                ["changes"] = new JsonArray(new JsonObject { ["value"] = valor, ["field"] = "messages" }),
            });
        }

        return new JsonObject { ["object"] = "whatsapp_business_account", ["entry"] = entradas };
    }

    public static JsonObject Value(string phone = Phone, JsonArray? contacts = null, JsonArray? messages = null, JsonArray? statuses = null)
    {
        var valor = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = new JsonObject { ["display_phone_number"] = "551130000000", ["phone_number_id"] = phone },
        };
        if (contacts is not null)
        {
            valor["contacts"] = contacts;
        }

        if (messages is not null)
        {
            valor["messages"] = messages;
        }

        if (statuses is not null)
        {
            valor["statuses"] = statuses;
        }

        return valor;
    }

    public static JsonObject Contact(string waId, string name) => new() { ["profile"] = new JsonObject { ["name"] = name }, ["wa_id"] = waId };

    public static JsonObject TextMessage(string id, string from, string body, string timestamp = "1749416383") =>
        new() { ["from"] = from, ["id"] = id, ["timestamp"] = timestamp, ["type"] = "text", ["text"] = new JsonObject { ["body"] = body } };

    public static JsonObject MediaMessage(string type, string id, string from, JsonObject midia, string timestamp = "1749416383") =>
        new() { ["from"] = from, ["id"] = id, ["timestamp"] = timestamp, ["type"] = type, [type] = midia };

    public static JsonObject Status(string id, string status, string timestamp = "1750030073", string? callback = null, int? errorCode = null)
    {
        var s = new JsonObject { ["id"] = id, ["status"] = status, ["timestamp"] = timestamp, ["recipient_id"] = "5511999990001" };
        if (callback is not null)
        {
            s["biz_opaque_callback_data"] = callback;
        }

        if (errorCode is { } codigo)
        {
            s["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = codigo,
                ["title"] = "TITULO LIVRE DO PROVEDOR",
                ["message"] = "MENSAGEM LIVRE DO PROVEDOR",
                ["error_data"] = new JsonObject { ["details"] = "DETALHES LIVRES DO PROVEDOR" },
            });
        }

        return s;
    }
}
