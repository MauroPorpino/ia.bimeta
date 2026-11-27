using System.Net;
using System.Net.Http.Headers;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// O adaptador CONVERSACIONAL do Ollama para a porta <see cref="IAiProvider"/> (Etapa 04): <c>HttpClient</c> direto em <c>POST /api/chat</c>, sem streaming, sem ferramentas e sem SDK. Como nos demais adaptadores,
/// a falha do provedor é RESULTADO, nunca exceção (só o cancelamento do chamador sai como <see cref="OperationCanceledException"/>); não há fallback para outro provedor nem modelo, nem nova tentativa interna, nem
/// estado entre chamadas, e o adaptador não conhece tenant. O servidor não exige chave: <see cref="RequiresCredential"/> é falso, o AiGateway entrega <see cref="AiCredential.None"/> e o adaptador nunca a lê.
/// O endereço vem só da configuração da infraestrutura; o cliente não segue redirecionamentos e a validação de certificado do HTTPS fica intacta.
/// </summary>
public sealed partial class OllamaChatProvider : IAiProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly OllamaChatOptions _options;
    private readonly TimeProvider _clock;
    private readonly bool _ownsClient;

    public OllamaChatProvider(HttpClient http, OllamaChatOptions options, TimeProvider? clock = null, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = http;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _ownsClient = ownsClient;
    }

    public string ProviderId => OllamaChatOptions.ProviderId;

    /// <summary>O Ollama local ou de rede privada não tem chave comercial por tenant.</summary>
    public bool RequiresCredential => false;

    public static OllamaChatProvider Create(OllamaChatOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var cliente = new HttpClient(CreateHandler(options)) { Timeout = Timeout.InfiniteTimeSpan };
        return new OllamaChatProvider(cliente, options, clock, ownsClient: true);
    }

    internal static SocketsHttpHandler CreateHandler(OllamaChatOptions options) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = options.ConnectTimeout,
        PooledConnectionLifetime = options.PooledConnectionLifetime,
        UseCookies = false,
    };

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inicio = _clock.GetTimestamp();
        TimeSpan Decorrido() => _clock.GetElapsedTime(inicio);

        if (request.MaxOutputTokens < 1)
        {
            return OllamaChatResponseReader.Fail(AiErrorKind.InvalidRequest, "max-output-tokens-too-low", AiUsageCertainty.NoneIncurred, Decorrido());
        }

        using var mensagem = OllamaChatRequestFactory.Create(request, _options, out var problema, out var numCtx);
        if (mensagem is null)
        {
            // Nada foi enviado ao servidor.
            return OllamaChatResponseReader.Fail(problema == "context-budget-exceeded" ? AiErrorKind.ContextTooLong : AiErrorKind.InvalidRequest, problema!, AiUsageCertainty.NoneIncurred, Decorrido());
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(60));

        HttpResponseMessage? resposta = null;
        try
        {
            resposta = await _http.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            var corpo = await ReadBoundedAsync(resposta.Content, _options.MaxResponseBytes, limite.Token);
            return corpo is null
                ? OllamaChatResponseReader.Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, Decorrido())
                : OllamaChatResponseReader.Read((int)resposta.StatusCode, corpo, Decorrido(), RetryAfter(resposta), numCtx);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // O servidor pode ter continuado a gerar: consumo desconhecido, nunca zero.
            return OllamaChatResponseReader.Fail(AiErrorKind.Transient, "timeout", AiUsageCertainty.Unknown, Decorrido());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Servidor indisponível ou conexão caída: só o TIPO da falha vira código (a mensagem pode carregar o endereço interno).
            return OllamaChatResponseReader.Fail(AiErrorKind.Transient, "server-unavailable", AiUsageCertainty.Unknown, Decorrido());
        }
        finally
        {
            resposta?.Dispose();
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent conteudo, int limite, CancellationToken cancellationToken)
    {
        if (conteudo.Headers.ContentLength is { } declarado && declarado > limite)
        {
            return null;
        }

        await using var fluxo = await conteudo.ReadAsStreamAsync(cancellationToken);
        using var memoria = new MemoryStream();
        var bloco = new byte[8192];
        int lidos;
        while ((lidos = await fluxo.ReadAsync(bloco, cancellationToken)) > 0)
        {
            if (memoria.Length + lidos > limite)
            {
                return null;
            }

            memoria.Write(bloco, 0, lidos);
        }

        return memoria.ToArray();
    }

    private TimeSpan? RetryAfter(HttpResponseMessage resposta)
    {
        RetryConditionHeaderValue? espera = resposta.Headers.RetryAfter;
        if (espera is null)
        {
            return null;
        }

        if (espera.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return espera.Date is { } data ? (data - _clock.GetUtcNow() is var resto && resto > TimeSpan.Zero ? resto : TimeSpan.Zero) : null;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
