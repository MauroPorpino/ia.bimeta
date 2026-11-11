using System.Net;
using System.Net.Sockets;
using IaBimeta.Capabilities.Domain;

namespace IaBimeta.Capabilities.Infrastructure.Outbound;

/// <summary>Uma chamada de saída já montada pelo conector. O destino, os cabeçalhos e o corpo foram montados a partir do molde da operação: nada aqui vem de texto livre.</summary>
public sealed record OutboundRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body,
    string? ContentType,
    TimeSpan Timeout,
    int MaxResponseBytes);

public sealed record OutboundResponse(int Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter);

/// <summary>O destino não é permitido (SSRF). Nunca chegou a conectar.</summary>
public sealed class OutboundBlockedException : Exception
{
    public OutboundBlockedException(string reason)
        : base("Destino não permitido.")
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed class OutboundTimeoutException : Exception
{
    public OutboundTimeoutException()
        : base("Tempo esgotado.")
    {
    }
}

public sealed class OutboundTooLargeException : Exception
{
    public OutboundTooLargeException()
        : base("Resposta acima do limite.")
    {
    }
}

/// <param name="ConnectStage">Falhou ANTES de a requisição sair (DNS, conexão, TLS): nada chegou ao destino.</param>
public sealed class OutboundNetworkException : Exception
{
    public OutboundNetworkException(bool connectStage)
        : base("Falha de rede.")
    {
        ConnectStage = connectStage;
    }

    public bool ConnectStage { get; }
}

/// <summary>A porta do transporte de saída: o conector só fala com ela, então os testes trocam a rede inteira por uma simulada.</summary>
public interface IOutboundHttp
{
    Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken);
}

/// <summary>A resolução de nomes, separada para que o teste simule um DNS que aponta para um endereço interno (rebinding).</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// O transporte HTTP de saída (ADR 0035, §4). Toda a proteção contra SSRF acontece aqui, em duas linhas de defesa: a URL é conferida antes de enviar (<see cref="OutboundDestinationPolicy.CheckUri"/>) e, o que
/// importa mais, cada ENDEREÇO resolvido é conferido no instante da CONEXÃO (<c>ConnectCallback</c>): resolve o nome uma vez, confere todos os endereços e conecta a um endereço aprovado, então não há janela entre
/// verificar e conectar (DNS rebinding). Redirecionamentos NUNCA são seguidos (a resposta 3xx volta ao chamador, que a trata como erro); proxy do sistema, cookies e credenciais implícitas são desligados; o certificado
/// TLS é validado normalmente. O corpo é lido em fluxo com teto de bytes (depois de descomprimir) e o tempo total é limitado.
/// </summary>
public sealed class GuardedHttpTransport : IOutboundHttp, IDisposable
{
    private readonly OutboundDestinationPolicy _policy;
    private readonly IHostResolver _resolver;
    private readonly HttpClient _client;

    public GuardedHttpTransport(OutboundDestinationPolicy policy, IHostResolver resolver)
    {
        _policy = policy;
        _resolver = resolver;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 32,
            ConnectCallback = ConnectGuardedAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var motivo = _policy.CheckUri(request.Uri);
        if (motivo is not null)
        {
            throw new OutboundBlockedException(motivo);
        }

        using var mensagem = new HttpRequestMessage(request.Method, request.Uri);
        mensagem.Headers.TryAddWithoutValidation("Accept", "application/json");
        mensagem.Headers.TryAddWithoutValidation("User-Agent", "ia.bimeta-integration/1");
        foreach (var (nome, valor) in request.Headers)
        {
            mensagem.Headers.TryAddWithoutValidation(nome, valor);
        }

        if (request.Body is not null)
        {
            mensagem.Content = new ByteArrayContent(request.Body);
            mensagem.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType ?? "application/json");
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(request.Timeout);

        try
        {
            using var resposta = await _client.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            var status = (int)resposta.StatusCode;
            var tipo = resposta.Content.Headers.ContentType?.MediaType;
            TimeSpan? espera = resposta.Headers.RetryAfter?.Delta ?? (resposta.Headers.RetryAfter?.Date is { } data ? data - DateTimeOffset.UtcNow : null);

            // Um redirecionamento nunca é seguido nem lido: o corpo dele não interessa.
            if (status is >= 300 and < 400)
            {
                return new OutboundResponse(status, tipo, [], espera);
            }

            if (resposta.Content.Headers.ContentLength is { } tamanho && tamanho > request.MaxResponseBytes)
            {
                throw new OutboundTooLargeException();
            }

            await using var fluxo = await resposta.Content.ReadAsStreamAsync(limite.Token);
            var corpo = await ReadLimitedAsync(fluxo, request.MaxResponseBytes, limite.Token);
            return new OutboundResponse(status, tipo, corpo, espera);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OutboundTimeoutException();
        }
        catch (HttpRequestException ex)
        {
            if (FindInner<OutboundBlockedException>(ex) is { } bloqueado)
            {
                throw new OutboundBlockedException(bloqueado.Reason);
            }

            // Sem StatusCode e antes de qualquer resposta: o erro foi de conexão (DNS, TCP, TLS); com requisição já enviada, o destino pode ter recebido.
            throw new OutboundNetworkException(connectStage: ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream fluxo, int maximo, CancellationToken cancellationToken)
    {
        using var saida = new MemoryStream();
        var buffer = new byte[8192];
        int lidos;
        while ((lidos = await fluxo.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (saida.Length + lidos > maximo)
            {
                throw new OutboundTooLargeException();
            }

            saida.Write(buffer, 0, lidos);
        }

        return saida.ToArray();
    }

    private static T? FindInner<T>(Exception ex)
        where T : Exception
    {
        for (Exception? atual = ex; atual is not null; atual = atual.InnerException)
        {
            if (atual is T alvo)
            {
                return alvo;
            }
        }

        return null;
    }

    /// <summary>
    /// A defesa que não tem janela: resolve o nome, confere CADA endereço e só então conecta, a um endereço aprovado. O nome resolvido aqui é o único usado pela conexão; um DNS que devolva 127.0.0.1 (ou o metadado
    /// de nuvem) para um nome público é recusado sem conectar.
    /// </summary>
    private async ValueTask<Stream> ConnectGuardedAsync(SocketsHttpConnectionContext contexto, CancellationToken cancellationToken)
    {
        var host = contexto.DnsEndPoint.Host;
        var porta = contexto.DnsEndPoint.Port;

        IPAddress[] enderecos;
        if (IPAddress.TryParse(host, out var literal))
        {
            enderecos = [literal];
        }
        else
        {
            if (OutboundDestinationPolicy.IsInternalHostname(host))
            {
                throw new OutboundBlockedException("internal-hostname");
            }

            try
            {
                enderecos = await _resolver.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }
        }

        if (enderecos.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // TODOS os endereços resolvidos precisam passar: um nome que aponta para um endereço público E um interno é recusado (não se escolhe o "bom" para fingir que o nome é confiável).
        string? recusa = null;
        foreach (var endereco in enderecos)
        {
            recusa ??= _policy.CheckAddress(endereco);
        }

        if (recusa is not null)
        {
            throw new OutboundBlockedException(recusa);
        }

        Exception? ultima = null;
        foreach (var endereco in enderecos)
        {
            var socket = new Socket(endereco.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(endereco, porta), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                ultima = ex;
            }
        }

        throw ultima ?? new SocketException((int)SocketError.HostUnreachable);
    }

    public void Dispose() => _client.Dispose();
}
