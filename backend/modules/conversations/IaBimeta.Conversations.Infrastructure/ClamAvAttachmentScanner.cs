using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// A detecção EMBUTIDA da amostra de teste padrão de antivírus (EICAR, inofensiva por desenho), que vale mesmo sem nenhum verificador externo: é a amostra com que os testes e a homologação provam o bloqueio sem
/// usar código malicioso de verdade. A cadeia é montada em tempo de execução (nunca escrita inteira no código-fonte), para os antivírus das máquinas de desenvolvimento não tratarem o próprio repositório como infectado.
/// </summary>
public static class EicarTestSample
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes(string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE", "!$H+H*"));

    /// <summary>A amostra completa (para os testes e a homologação).</summary>
    public static byte[] Bytes() => (byte[])Signature.Clone();

    /// <summary>O conteúdo contém a amostra de teste?</summary>
    public static bool IsPresentIn(ReadOnlySpan<byte> content) => content.IndexOf(Signature) >= 0;
}

/// <summary>
/// O cliente do daemon do ClamAV (<c>clamd</c>) pelo protocolo <c>INSTREAM</c> por TCP (ADR 0032): <c>zINSTREAM\0</c>, blocos com o tamanho em 4 bytes (big-endian) e um bloco final de tamanho zero; a resposta é
/// <c>stream: OK</c> (limpo), <c>stream: &lt;assinatura&gt; FOUND</c> (infectado) ou um erro. O conteúdo vai por soquete: nenhum arquivo temporário é criado. Só <c>OK</c> e <c>FOUND</c> são respostas definitivas;
/// conexão recusada, tempo esgotado, erro do daemon (inclusive "limite de tamanho excedido") e resposta ilegível são <see cref="ScanVerdict.Unavailable"/>, NUNCA "limpo". Nada do conteúdo nem do nome da assinatura
/// vai a log ou a resultado.
/// </summary>
public sealed class ClamAvAttachmentScanner : IAttachmentScanner
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxResponseBytes = 1024;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    public ClamAvAttachmentScanner(string host, int port, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _port = port;
        _timeout = timeout;
    }

    public async Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_timeout);
        try
        {
            using var cliente = new TcpClient { NoDelay = true };
            await cliente.ConnectAsync(_host, _port, limite.Token);
            await using var fluxo = cliente.GetStream();

            await fluxo.WriteAsync("zINSTREAM\0"u8.ToArray(), limite.Token);
            var tamanho = new byte[4];
            for (var deslocamento = 0; deslocamento < content.Length; deslocamento += ChunkSize)
            {
                var bloco = content.Slice(deslocamento, Math.Min(ChunkSize, content.Length - deslocamento));
                BinaryPrimitives.WriteUInt32BigEndian(tamanho, (uint)bloco.Length);
                await fluxo.WriteAsync(tamanho, limite.Token);
                await fluxo.WriteAsync(bloco, limite.Token);
            }

            BinaryPrimitives.WriteUInt32BigEndian(tamanho, 0);
            await fluxo.WriteAsync(tamanho, limite.Token);
            await fluxo.FlushAsync(limite.Token);

            return Interpret(await ReadResponseAsync(fluxo, limite.Token));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return ScanVerdict.Unavailable;
        }
    }

    private static async Task<string> ReadResponseAsync(NetworkStream fluxo, CancellationToken cancellationToken)
    {
        var resposta = new byte[MaxResponseBytes];
        var total = 0;
        while (total < resposta.Length)
        {
            var lidos = await fluxo.ReadAsync(resposta.AsMemory(total), cancellationToken);
            if (lidos == 0)
            {
                break;
            }

            total += lidos;
            if (resposta.AsSpan(0, total).IndexOfAny((byte)0, (byte)'\n') >= 0)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(resposta, 0, total).Trim('\0', '\n', '\r', ' ');
    }

    /// <summary>"stream: OK" é limpo; "... FOUND" é infectado; qualquer outra coisa (inclusive "... ERROR") é indisponível. A comparação é pelo FIM da resposta: a assinatura é texto do daemon e não decide nada.</summary>
    internal static ScanVerdict Interpret(string resposta)
    {
        if (resposta.EndsWith(" OK", StringComparison.Ordinal) && resposta.StartsWith("stream:", StringComparison.Ordinal))
        {
            return ScanVerdict.Clean;
        }

        if (resposta.EndsWith(" FOUND", StringComparison.Ordinal) && resposta.StartsWith("stream:", StringComparison.Ordinal))
        {
            return ScanVerdict.Infected;
        }

        return ScanVerdict.Unavailable;
    }
}

/// <summary>
/// O verificador efetivo (ADR 0032): primeiro a detecção embutida da amostra EICAR (sempre, sem depender de nada externo) e, se houver um mecanismo configurado, a varredura dele. Sem mecanismo, o que passou pela
/// detecção embutida é <see cref="ScanVerdict.NotScanned"/>: não foi verificado, e com a varredura exigida isso nunca libera um arquivo (a configuração com <c>Required</c> e sem mecanismo nem sobe).
/// </summary>
public sealed class ConfiguredAttachmentScanner : IAttachmentScanner
{
    private readonly IAttachmentScanner? _engine;

    public ConfiguredAttachmentScanner(IAttachmentScanner? engine)
    {
        _engine = engine;
    }

    public async Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (EicarTestSample.IsPresentIn(content.Span))
        {
            return ScanVerdict.Infected;
        }

        return _engine is null ? ScanVerdict.NotScanned : await _engine.ScanAsync(kind, mimeType, content, cancellationToken);
    }
}
