using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using Microsoft.Extensions.Logging;

namespace IaBimeta.Capabilities.Infrastructure.Outbound;

/// <summary>
/// O conector REST/JSON genérico (ADR 0035, §4), tipo <c>rest-json</c>. Monta a chamada SÓ a partir do molde da operação e dos valores já validados: caminho com escape, consulta, cabeçalhos permitidos e corpo JSON
/// tipado; nunca de texto livre. Autentica por API Key, Basic ou OAuth 2.0 Client Credentials. Repete só leitura ou escrita com idempotência comprovada (o motor decide e passa <see cref="ConnectorCall.AllowRetries"/>).
/// Nunca segue redirecionamento, exige JSON na resposta e limita bytes e tempo. Nunca lança por falha do sistema externo e nunca registra segredo, parâmetro ou corpo: os logs levam só identificadores e o resultado.
/// </summary>
public sealed class RestJsonConnector : ICapabilityConnector
{
    public const string ConnectorKind = "rest-json";

    private const int MaxJsonDepth = 32;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    private readonly IOutboundHttp _http;
    private readonly OAuthTokenCache _tokens;
    private readonly CapabilitiesOptions _options;
    private readonly ILogger<RestJsonConnector>? _logger;

    public RestJsonConnector(IOutboundHttp http, OAuthTokenCache tokens, CapabilitiesOptions options, ILogger<RestJsonConnector>? logger = null)
    {
        _http = http;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    public string Kind => ConnectorKind;

    public async Task<ConnectorResult> ProbeAsync(ConnectorConnection connection, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var url = Compose(connection.BaseUrl, connection.HealthPath, string.Empty);
        if (url is null)
        {
            return new ConnectorResult(ConnectorOutcome.Blocked, null, null, 0, 0, false);
        }

        var resultado = await SendWithAuthAsync(connection, HttpMethod.Get, url, [], null, requireJson: false, cancellationToken);
        return resultado with { Attempts = 1, ElapsedMs = cronometro.ElapsedMilliseconds };
    }

    public async Task<ConnectorResult> InvokeAsync(ConnectorCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var cronometro = Stopwatch.StartNew();
        var conexao = call.Connection;

        PreparedRequest preparada;
        try
        {
            preparada = Build(call);
        }
        catch (InvalidOperationException)
        {
            // O molde e os valores já foram validados: se ainda assim a URL não fecha, o destino não é o da conexão. Nada sai.
            return new ConnectorResult(ConnectorOutcome.Blocked, null, null, 0, cronometro.ElapsedMilliseconds, false);
        }

        var tentativas = 0;
        ConnectorResult resultado;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tentativas++;
            resultado = await SendWithAuthAsync(conexao, preparada.Method, preparada.Url, preparada.Headers, preparada.Body, requireJson: true, cancellationToken);

            var repetivel = resultado.Outcome is ConnectorOutcome.Unavailable or ConnectorOutcome.Timeout or ConnectorOutcome.NetworkError;
            if (!repetivel || !call.AllowRetries || tentativas >= Math.Clamp(call.MaxAttempts, 1, 3))
            {
                break;
            }

            await Task.Delay(Backoff(tentativas, resultado.RetryAfterHint()), cancellationToken);
        }

        _logger?.LogInformation(
            "Integração {ConnectionId} {Method} resultado {Outcome} status {Status} tentativas {Attempts} em {ElapsedMs} ms",
            conexao.ConnectionId, call.Operation.Method, resultado.Outcome, resultado.UpstreamStatus, tentativas, cronometro.ElapsedMilliseconds);
        return resultado with { Attempts = tentativas, ElapsedMs = cronometro.ElapsedMilliseconds };
    }

    // ---------------------------------------------------------------- montagem

    private sealed record PreparedRequest(HttpMethod Method, Uri Url, List<KeyValuePair<string, string>> Headers, byte[]? Body);

    private PreparedRequest Build(ConnectorCall call)
    {
        var spec = call.Operation;
        var caminho = spec.PathTemplate;
        var consulta = new List<string>();
        var cabecalhos = new List<KeyValuePair<string, string>>();
        JsonObject? corpo = null;

        foreach (var parametro in spec.Parameters)
        {
            if (!call.Values.TryGetValue(parametro.Name, out var valor))
            {
                continue;
            }

            var nomeExterno = parametro.WireName ?? parametro.Name;
            var texto = Format(valor);
            switch (parametro.Location)
            {
                case CapabilityParameterLocation.Path:
                    // Defesa em profundidade (o validador já recusa): um valor de caminho nunca leva separador, escape ou segmento de travessia.
                    if (texto is "." or ".." || texto.IndexOfAny(['/', (char)92, '?', '#', '%']) >= 0)
                    {
                        throw new InvalidOperationException("path-value");
                    }

                    caminho = caminho.Replace("{" + parametro.Name + "}", Uri.EscapeDataString(texto), StringComparison.Ordinal);
                    break;
                case CapabilityParameterLocation.Query:
                    consulta.Add($"{Uri.EscapeDataString(nomeExterno)}={Uri.EscapeDataString(texto)}");
                    break;
                case CapabilityParameterLocation.Header:
                    if (!call.Connection.AllowedHeaders.Contains(nomeExterno, StringComparer.OrdinalIgnoreCase) || OperationSpecRules.IsForbiddenHeader(nomeExterno) || texto.Any(char.IsControl))
                    {
                        throw new InvalidOperationException("header");
                    }

                    cabecalhos.Add(new(nomeExterno, texto));
                    break;
                case CapabilityParameterLocation.Body:
                    corpo ??= new JsonObject();
                    SetBodyValue(corpo, nomeExterno, valor);
                    break;
            }
        }

        // Cabeçalhos padrão da conexão (públicos) entram antes; os do parâmetro e os de autenticação, depois.
        var todos = new List<KeyValuePair<string, string>>();
        foreach (var (nome, valor) in call.Connection.DefaultHeaders)
        {
            todos.Add(new(nome, valor));
        }

        todos.AddRange(cabecalhos);
        if (spec.IdempotencyHeader is not null && call.IdempotencyKey is not null && call.IsWrite)
        {
            todos.Add(new(spec.IdempotencyHeader, call.IdempotencyKey));
        }

        if (caminho.Contains('{', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("path");
        }

        var url = Compose(call.Connection.BaseUrl, caminho, string.Join('&', consulta)) ?? throw new InvalidOperationException("url");
        var metodo = spec.Method switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            _ => throw new InvalidOperationException("method"),
        };

        byte[]? bytes = null;
        if (corpo is not null)
        {
            bytes = Encoding.UTF8.GetBytes(corpo.ToJsonString());
        }
        else if (metodo == HttpMethod.Post)
        {
            bytes = [];
        }

        return new PreparedRequest(metodo, url, todos, bytes);
    }

    /// <summary>Junta a base e o caminho e confere que o resultado continua no MESMO destino (esquema, host e porta) da conexão: um valor de parâmetro jamais troca o servidor.</summary>
    private static Uri? Compose(string baseUrl, string path, string query)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var texto = baseUrl.TrimEnd('/') + path + (query.Length > 0 ? "?" + query : string.Empty);
        if (!Uri.TryCreate(texto, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var mesmoDestino = uri.Scheme == baseUri.Scheme && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == baseUri.Port && string.IsNullOrEmpty(uri.UserInfo);
        return mesmoDestino ? uri : null;
    }

    private static string Format(object valor) => valor switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(valor, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static void SetBodyValue(JsonObject raiz, string caminho, object valor)
    {
        var partes = caminho.Split('.');
        var atual = raiz;
        for (var i = 0; i < partes.Length - 1; i++)
        {
            if (atual[partes[i]] is not JsonObject filho)
            {
                filho = new JsonObject();
                atual[partes[i]] = filho;
            }

            atual = filho;
        }

        atual[partes[^1]] = valor switch
        {
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            long l => JsonValue.Create(l),
            decimal d => JsonValue.Create(d),
            _ => JsonValue.Create(Format(valor)),
        };
    }

    // ---------------------------------------------------------------- envio

    private async Task<ConnectorResult> SendWithAuthAsync(
        ConnectorConnection conexao,
        HttpMethod metodo,
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> cabecalhosBase,
        byte[]? corpo,
        bool requireJson,
        CancellationToken cancellationToken)
    {
        var refeito = false;
        while (true)
        {
            var cabecalhos = new List<KeyValuePair<string, string>>(cabecalhosBase);
            var destino = url;
            try
            {
                switch (conexao.AuthType)
                {
                    case ConnectorAuthType.ApiKey:
                        var chave = conexao.Secret.Values.GetValueOrDefault("apiKey") ?? throw new InvalidOperationException("secret");
                        var nome = conexao.AuthSettings.GetValueOrDefault("name") ?? "X-API-Key";
                        var prefixo = conexao.AuthSettings.GetValueOrDefault("prefix") ?? string.Empty;
                        if (conexao.AuthSettings.GetValueOrDefault("location") == "query")
                        {
                            var separador = string.IsNullOrEmpty(url.Query) ? "?" : "&";
                            destino = new Uri(url.AbsoluteUri + separador + Uri.EscapeDataString(nome) + "=" + Uri.EscapeDataString(prefixo + chave));
                        }
                        else
                        {
                            cabecalhos.Add(new(nome, prefixo + chave));
                        }

                        break;
                    case ConnectorAuthType.Basic:
                        var usuario = conexao.Secret.Values.GetValueOrDefault("username") ?? throw new InvalidOperationException("secret");
                        var senha = conexao.Secret.Values.GetValueOrDefault("password") ?? throw new InvalidOperationException("secret");
                        cabecalhos.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(usuario + ":" + senha))));
                        break;
                    case ConnectorAuthType.OAuth2ClientCredentials:
                        var token = await _tokens.GetAsync(conexao, _http, _options, forceRefresh: refeito, cancellationToken);
                        cabecalhos.Add(new("Authorization", "Bearer " + token));
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                return new ConnectorResult(ConnectorOutcome.SecretUnavailable, null, null, 0, 0, false);
            }
            catch (OAuthTokenException ex)
            {
                return new ConnectorResult(ex.Outcome, ex.UpstreamStatus, null, 0, 0, false);
            }

            var resultado = await SendOnceAsync(conexao, metodo, destino, cabecalhos, corpo, requireJson, cancellationToken);

            // Um 401 com token OAuth pode ser só token vencido/revogado: renova UMA vez e repete (a chamada ainda não foi aceita, então não há efeito duplicado).
            if (resultado.Outcome == ConnectorOutcome.AuthFailed && resultado.UpstreamStatus == 401 && conexao.AuthType == ConnectorAuthType.OAuth2ClientCredentials && !refeito)
            {
                refeito = true;
                continue;
            }

            return resultado;
        }
    }

    private async Task<ConnectorResult> SendOnceAsync(
        ConnectorConnection conexao,
        HttpMethod metodo,
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> cabecalhos,
        byte[]? corpo,
        bool requireJson,
        CancellationToken cancellationToken)
    {
        var pedido = new OutboundRequest(metodo, url, cabecalhos, corpo, "application/json", TimeSpan.FromSeconds(conexao.TimeoutSeconds), conexao.MaxResponseBytes);
        OutboundResponse resposta;
        try
        {
            resposta = await _http.SendAsync(pedido, cancellationToken);
        }
        catch (OutboundBlockedException)
        {
            return new ConnectorResult(ConnectorOutcome.Blocked, null, null, 1, 0, false);
        }
        catch (OutboundTimeoutException)
        {
            return new ConnectorResult(ConnectorOutcome.Timeout, null, null, 1, 0, true);
        }
        catch (OutboundTooLargeException)
        {
            return new ConnectorResult(ConnectorOutcome.TooLarge, null, null, 1, 0, true);
        }
        catch (OutboundNetworkException ex)
        {
            return new ConnectorResult(ConnectorOutcome.NetworkError, null, null, 1, 0, !ex.ConnectStage);
        }

        var status = resposta.Status;
        ConnectorResult Com(ConnectorOutcome desfecho, bool pode, JsonElement? json = null) => new ConnectorResult(desfecho, status, json, 1, 0, pode) { RetryAfter = resposta.RetryAfter };

        if (status is >= 300 and < 400)
        {
            return Com(ConnectorOutcome.Redirected, true);
        }

        if (status is 401 or 403)
        {
            return Com(ConnectorOutcome.AuthFailed, false);
        }

        if (status is 408 or 429 or >= 500)
        {
            return Com(ConnectorOutcome.Unavailable, true);
        }

        if (status is >= 400)
        {
            return Com(ConnectorOutcome.Rejected, false);
        }

        if (status is < 200)
        {
            return Com(ConnectorOutcome.InvalidResponse, true);
        }

        if (resposta.Body.Length == 0)
        {
            return Com(ConnectorOutcome.Success, true);
        }

        if (!requireJson)
        {
            return Com(ConnectorOutcome.Success, true);
        }

        if (resposta.ContentType is null || !(resposta.ContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || resposta.ContentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            return Com(ConnectorOutcome.InvalidResponse, true);
        }

        try
        {
            using var documento = JsonDocument.Parse(resposta.Body, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            return Com(ConnectorOutcome.Success, true, documento.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Com(ConnectorOutcome.InvalidResponse, true);
        }
    }

    private TimeSpan Backoff(int tentativa, TimeSpan? sugestao)
    {
        var baseMs = Math.Min((long)_options.RetryBaseDelayMs << Math.Min(tentativa - 1, 10), _options.RetryMaxDelayMs);
        var espera = TimeSpan.FromMilliseconds(baseMs);
        if (sugestao is { } pedida && pedida > espera)
        {
            espera = pedida > MaxRetryAfter ? MaxRetryAfter : pedida;
        }

        return espera;
    }
}

internal static class ConnectorResultExtensions
{
    public static TimeSpan? RetryAfterHint(this ConnectorResult resultado) => resultado.RetryAfter;
}

/// <summary>O token OAuth não pôde ser obtido. Carrega só o desfecho e o status HTTP, nunca o corpo da resposta do servidor de autorização.</summary>
public sealed class OAuthTokenException : Exception
{
    public OAuthTokenException(ConnectorOutcome outcome, int? upstreamStatus)
        : base("Falha ao obter o token.")
    {
        Outcome = outcome;
        UpstreamStatus = upstreamStatus;
    }

    public ConnectorOutcome Outcome { get; }

    public int? UpstreamStatus { get; }
}

/// <summary>
/// O cache dos tokens OAuth 2.0 (Client Credentials), em MEMÓRIA e por processo: nunca persistido. A chave inclui tenant, conexão e a versão do segredo (trocar o segredo invalida o token). O pedido do token passa
/// pelo MESMO transporte protegido (a URL do token também não pode ser interna) e a resposta é limitada em tamanho.
/// </summary>
public sealed class OAuthTokenCache
{
    private const int MaxTokenResponseBytes = 16 * 1024;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Connection, int Version), CachedToken> _cache = new();
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Connection, int Version), SemaphoreSlim> _locks = new();

    public OAuthTokenCache(TimeProvider clock)
    {
        _clock = clock;
    }

    private sealed record CachedToken(string Value, DateTimeOffset ExpiresAt);

    internal async Task<string> GetAsync(ConnectorConnection conexao, IOutboundHttp http, CapabilitiesOptions options, bool forceRefresh, CancellationToken cancellationToken)
    {
        var chave = (conexao.TenantId, conexao.ConnectionId, conexao.SecretVersion);
        if (!forceRefresh && _cache.TryGetValue(chave, out var existente) && existente.ExpiresAt > _clock.GetUtcNow())
        {
            return existente.Value;
        }

        var trava = _locks.GetOrAdd(chave, _ => new SemaphoreSlim(1, 1));
        await trava.WaitAsync(cancellationToken);
        try
        {
            // Quem esperou a trava pode encontrar o token já renovado por outra chamada.
            if (!forceRefresh && _cache.TryGetValue(chave, out existente) && existente.ExpiresAt > _clock.GetUtcNow())
            {
                return existente.Value;
            }

            var token = await FetchAsync(conexao, http, cancellationToken);
            _cache[chave] = token;
            return token.Value;
        }
        finally
        {
            trava.Release();
        }
    }

    private async Task<CachedToken> FetchAsync(ConnectorConnection conexao, IOutboundHttp http, CancellationToken cancellationToken)
    {
        var tokenUrl = conexao.AuthSettings.GetValueOrDefault("tokenUrl");
        var clienteId = conexao.Secret.Values.GetValueOrDefault("clientId");
        var segredo = conexao.Secret.Values.GetValueOrDefault("clientSecret");
        if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var uri) || clienteId is null || segredo is null)
        {
            throw new OAuthTokenException(ConnectorOutcome.SecretUnavailable, null);
        }

        var campos = new List<string> { "grant_type=client_credentials" };
        var cabecalhos = new List<KeyValuePair<string, string>>();
        if (conexao.AuthSettings.GetValueOrDefault("clientAuth") == "basic")
        {
            cabecalhos.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(clienteId) + ":" + Uri.EscapeDataString(segredo)))));
        }
        else
        {
            campos.Add("client_id=" + Uri.EscapeDataString(clienteId));
            campos.Add("client_secret=" + Uri.EscapeDataString(segredo));
        }

        if (conexao.AuthSettings.GetValueOrDefault("scope") is { Length: > 0 } escopo)
        {
            campos.Add("scope=" + Uri.EscapeDataString(escopo));
        }

        var pedido = new OutboundRequest(HttpMethod.Post, uri, cabecalhos, Encoding.UTF8.GetBytes(string.Join('&', campos)), "application/x-www-form-urlencoded", TimeSpan.FromSeconds(conexao.TimeoutSeconds), MaxTokenResponseBytes);
        OutboundResponse resposta;
        try
        {
            resposta = await http.SendAsync(pedido, cancellationToken);
        }
        catch (OutboundBlockedException)
        {
            throw new OAuthTokenException(ConnectorOutcome.Blocked, null);
        }
        catch (OutboundTimeoutException)
        {
            throw new OAuthTokenException(ConnectorOutcome.Timeout, null);
        }
        catch (OutboundTooLargeException)
        {
            throw new OAuthTokenException(ConnectorOutcome.TooLarge, null);
        }
        catch (OutboundNetworkException)
        {
            throw new OAuthTokenException(ConnectorOutcome.NetworkError, null);
        }

        if (resposta.Status is < 200 or >= 300)
        {
            throw new OAuthTokenException(ConnectorOutcome.AuthFailed, resposta.Status);
        }

        try
        {
            using var documento = JsonDocument.Parse(resposta.Body, new JsonDocumentOptions { MaxDepth = 8 });
            var raiz = documento.RootElement;
            var valor = raiz.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(valor) || valor.Length > 4096 || valor.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            {
                throw new OAuthTokenException(ConnectorOutcome.InvalidResponse, resposta.Status);
            }

            var segundos = raiz.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) ? s : 300;
            segundos = Math.Clamp(segundos, 30, 3600);
            return new CachedToken(valor, _clock.GetUtcNow().AddSeconds(segundos - 20));
        }
        catch (JsonException)
        {
            throw new OAuthTokenException(ConnectorOutcome.InvalidResponse, resposta.Status);
        }
    }
}
