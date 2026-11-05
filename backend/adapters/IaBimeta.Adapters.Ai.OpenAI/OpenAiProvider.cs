using System.Net;
using System.Net.Http.Headers;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// O adaptador da OpenAI para a porta <see cref="IAiProvider"/> (ADR 0017, seção B; BOUNDARIES §7): <c>HttpClient</c> direto na Responses API, sem SDK. A falha do provedor é RESULTADO, nunca exceção
/// (o contrato): só o cancelamento do chamador sai como <see cref="OperationCanceledException"/>, e o AiGateway o trata. O adaptador não tem fallback para outro provedor, não guarda estado entre chamadas e
/// não conhece tenant: a credencial BYOK chega em cada <see cref="AiCompletionRequest"/>, vai SÓ no cabeçalho daquela requisição e nunca aparece em resultado, código de erro, log nem mensagem.
/// O teto de tokens de saída que o AiGateway limitou é imposto na própria requisição (<c>max_output_tokens</c>), e o consumo informado pelo provedor volta normalizado, com a certeza declarada
/// (ver <see cref="OpenAiResponseReader"/>).
/// </summary>
public sealed class OpenAiProvider : IAiProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly TimeProvider _clock;
    private readonly bool _ownsClient;

    /// <param name="http">O cliente HTTP. NÃO deve ter cabeçalho padrão de autorização: a credencial é por requisição.</param>
    /// <param name="options">A configuração já validada.</param>
    /// <param name="clock">O relógio (a latência e a espera do <c>Retry-After</c> em data).</param>
    /// <param name="ownsClient">Verdadeiro quando o adaptador criou o cliente e deve descartá-lo.</param>
    public OpenAiProvider(HttpClient http, OpenAiOptions options, TimeProvider? clock = null, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = http;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _ownsClient = ownsClient;
    }

    public string ProviderId => OpenAiOptions.ProviderId;

    /// <summary>O adaptador com o próprio cliente HTTP: sem redirecionamento (a credencial nunca segue para outro endereço), sem tempo limite do cliente (cada chamada tem o dela) e com a vida da conexão limitada.</summary>
    public static OpenAiProvider Create(OpenAiOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var cliente = new HttpClient(CreateHandler(options)) { Timeout = Timeout.InfiniteTimeSpan };
        return new OpenAiProvider(cliente, options, clock, ownsClient: true);
    }

    internal static SocketsHttpHandler CreateHandler(OpenAiOptions options) => new()
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

        // O teto de saída é obrigatório e a API tem um mínimo: abaixo dele nada é enviado (não houve chamada, então nada foi cobrado).
        if (request.MaxOutputTokens < OpenAiOptions.MinOutputTokens)
        {
            return OpenAiResponseReader.Fail(AiErrorKind.InvalidRequest, "max-output-tokens-too-low", AiUsageCertainty.NoneIncurred, Decorrido(), null);
        }

        using var mensagem = OpenAiRequestFactory.Create(request, _options, out var problema);
        if (mensagem is null)
        {
            // A credencial fora do formato de chave é credencial inválida; o resto é pedido inválido. Nenhum dos dois chegou ao provedor.
            var tipo = problema == "credential-invalid-format" ? AiErrorKind.InvalidCredentials : AiErrorKind.InvalidRequest;
            return OpenAiResponseReader.Fail(tipo, problema!, AiUsageCertainty.NoneIncurred, Decorrido(), null);
        }

        // O tempo limite da chamada é do adaptador também (o AiGateway já impõe o dele ao token): esgotado sem o chamador ter desistido, é falha transitória de consumo desconhecido.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(60));

        HttpResponseMessage? resposta = null;
        try
        {
            resposta = await _http.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            var identificador = RequestId(resposta);
            var corpo = await ReadBoundedAsync(resposta.Content, _options.MaxResponseBytes, limite.Token);
            return corpo is null
                ? OpenAiResponseReader.Fail(AiErrorKind.Transient, "invalid-response", AiUsageCertainty.Unknown, Decorrido(), identificador)
                : OpenAiResponseReader.Read((int)resposta.StatusCode, corpo, identificador, Decorrido(), RetryAfter(resposta));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // O chamador desistiu (ou o AiGateway esgotou o tempo dele): a decisão é do AiGateway, que sabe a diferença.
            throw;
        }
        catch (OperationCanceledException)
        {
            return OpenAiResponseReader.Fail(AiErrorKind.Transient, "timeout", AiUsageCertainty.Unknown, Decorrido(), resposta is null ? null : RequestId(resposta));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Só o TIPO da falha vira código: a mensagem pode carregar o endereço ou trechos do pedido.
            return OpenAiResponseReader.Fail(AiErrorKind.Transient, "network-error", AiUsageCertainty.Unknown, Decorrido(), resposta is null ? null : RequestId(resposta));
        }
        finally
        {
            resposta?.Dispose();
        }
    }

    /// <summary>Lê o corpo até o limite. Acima dele (ou com o <c>Content-Length</c> acima) devolve nulo: a resposta é inválida, e nada gigante entra na memória.</summary>
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

    private static string? RequestId(HttpResponseMessage resposta) =>
        resposta.Headers.TryGetValues("x-request-id", out var valores) ? OpenAiResponseReader.Sanitize(valores.FirstOrDefault()) : null;

    /// <summary>A espera que o provedor sugeriu (<c>Retry-After</c> em segundos ou em data). Nunca negativa.</summary>
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
