using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>Como o <c>clamd</c> falso responde à próxima conexão (pode mudar durante o teste para simular o daemon caindo e voltando).</summary>
public enum ModoDoClamd
{
    Ok,
    Found,
    ErroDeLimite,
    FecharSemResponder,
    Lixo,
    OkNoMeioDoTexto,
    RespostaVazia,
    NaoResponde,
}

/// <summary>O que o <c>clamd</c> falso recebeu numa conexão: o comando, o conteúdo reunido, o tamanho de cada bloco e se terminou com o bloco de tamanho zero.</summary>
internal sealed record VistoPeloClamd(string Comando, byte[] Conteudo, List<int> TamanhosDosBlocos, bool TerminouComBlocoZero);

/// <summary>
/// Um <c>clamd</c> falso: aceita conexões em loopback, lê o protocolo INSTREAM de verdade (comando <c>zINSTREAM</c>, blocos com tamanho big-endian, terminador de tamanho zero) e responde conforme o <see cref="Modo"/>,
/// que o teste pode trocar a qualquer momento. Nenhuma amostra de código malicioso é necessária: o teste escolhe a resposta.
/// </summary>
internal sealed class ClamdFalso : IAsyncDisposable
{
    private readonly TcpListener _escuta = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _parar = new();
    private readonly Task _laco;
    private readonly List<VistoPeloClamd> _recebidos = [];
    private int _modo;

    public ClamdFalso(ModoDoClamd modo = ModoDoClamd.Ok)
    {
        _modo = (int)modo;
        _escuta.Start();
        Porta = ((IPEndPoint)_escuta.LocalEndpoint).Port;
        _laco = Task.Run(AceitarAsync);
    }

    public int Porta { get; }

    public ModoDoClamd Modo
    {
        get => (ModoDoClamd)Volatile.Read(ref _modo);
        set => Volatile.Write(ref _modo, (int)value);
    }

    public IReadOnlyList<VistoPeloClamd> Recebidos
    {
        get
        {
            lock (_recebidos)
            {
                return [.. _recebidos];
            }
        }
    }

    private async Task AceitarAsync()
    {
        try
        {
            while (!_parar.IsCancellationRequested)
            {
                var cliente = await _escuta.AcceptTcpClientAsync(_parar.Token);
                _ = Task.Run(() => AtenderAsync(cliente));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // encerrando
        }
    }

    private async Task AtenderAsync(TcpClient cliente)
    {
        using (cliente)
        {
            try
            {
                await using var fluxo = cliente.GetStream();
                var comando = new byte[10];
                await LerExatoAsync(fluxo, comando);
                var conteudo = new MemoryStream();
                var blocos = new List<int>();
                var zero = false;
                var cabecalho = new byte[4];
                while (true)
                {
                    await LerExatoAsync(fluxo, cabecalho);
                    var tamanho = (int)BinaryPrimitives.ReadUInt32BigEndian(cabecalho);
                    if (tamanho == 0)
                    {
                        zero = true;
                        break;
                    }

                    var bloco = new byte[tamanho];
                    await LerExatoAsync(fluxo, bloco);
                    conteudo.Write(bloco);
                    blocos.Add(tamanho);
                }

                lock (_recebidos)
                {
                    _recebidos.Add(new VistoPeloClamd(Encoding.ASCII.GetString(comando), conteudo.ToArray(), blocos, zero));
                }

                var modo = Modo;
                switch (modo)
                {
                    case ModoDoClamd.NaoResponde:
                        await Task.Delay(Timeout.Infinite, _parar.Token);
                        break;
                    case ModoDoClamd.FecharSemResponder:
                        break;
                    case ModoDoClamd.RespostaVazia:
                        await fluxo.WriteAsync("\0"u8.ToArray());
                        break;
                    default:
                        await fluxo.WriteAsync(Encoding.ASCII.GetBytes(Resposta(modo)));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or EndOfStreamException)
            {
                // o cliente desistiu
            }
        }
    }

    private static string Resposta(ModoDoClamd modo) => modo switch
    {
        ModoDoClamd.Ok => "stream: OK\0",
        ModoDoClamd.Found => "stream: Eicar-Signature FOUND\0",
        ModoDoClamd.ErroDeLimite => "INSTREAM size limit exceeded. ERROR\0",
        ModoDoClamd.Lixo => "isto nao e uma resposta do clamd\0",
        ModoDoClamd.OkNoMeioDoTexto => "stream: OK mas depois veio outra coisa FOUND?\0x",
        _ => string.Empty,
    };

    private static async Task LerExatoAsync(NetworkStream fluxo, byte[] destino)
    {
        var lidos = 0;
        while (lidos < destino.Length)
        {
            var n = await fluxo.ReadAsync(destino.AsMemory(lidos));
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            lidos += n;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _parar.CancelAsync();
        _escuta.Stop();
        try
        {
            await _laco;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // já encerrado
        }

        _parar.Dispose();
    }
}
