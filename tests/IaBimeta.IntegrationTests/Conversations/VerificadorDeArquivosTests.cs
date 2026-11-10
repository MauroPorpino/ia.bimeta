using System.Net;
using System.Net.Sockets;
using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Infrastructure;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O cliente do clamd (ADR 0032) contra um daemon FALSO que fala o protocolo INSTREAM de verdade, sem banco e sem ClamAV instalado: o enquadramento (comando, blocos de até 64 KB, tamanho em big-endian, terminador),
/// a interpretação da resposta (só OK e FOUND são definitivos) e o princípio de que falha do verificador NUNCA é "limpo". A amostra usada é a EICAR (inofensiva por desenho), sempre só em memória.
/// </summary>
public sealed class VerificadorDeArquivosTests : IAsyncDisposable
{
    private readonly List<ClamdFalso> _servidores = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var servidor in _servidores)
        {
            await servidor.DisposeAsync();
        }
    }

    private ClamdFalso Subir(ModoDoClamd modo)
    {
        var servidor = new ClamdFalso(modo);
        _servidores.Add(servidor);
        return servidor;
    }

    private static ClamAvAttachmentScanner Cliente(ClamdFalso servidor, int milissegundos = 2000) => new("127.0.0.1", servidor.Porta, TimeSpan.FromMilliseconds(milissegundos));

    [Fact]
    public async Task Resposta_OK_e_limpo_e_o_conteudo_chega_inteiro_e_enquadrado_como_INSTREAM()
    {
        var servidor = Subir(ModoDoClamd.Ok);
        var conteudo = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray(); // 4 blocos: 3 de 64 KB e 1 menor

        var veredito = await Cliente(servidor).ScanAsync(MessageKind.Document, "application/pdf", conteudo, default);

        Assert.Equal(ScanVerdict.Clean, veredito);
        var visto = Assert.Single(servidor.Recebidos);
        Assert.Equal("zINSTREAM\0", visto.Comando);
        Assert.Equal(conteudo, visto.Conteudo);
        Assert.Equal([65536, 65536, 65536, 200_000 - (3 * 65536)], visto.TamanhosDosBlocos);
        Assert.True(visto.TerminouComBlocoZero);
    }

    [Fact]
    public async Task Conteudo_vazio_ainda_envia_o_comando_e_o_terminador()
    {
        var servidor = Subir(ModoDoClamd.Ok);

        var veredito = await Cliente(servidor).ScanAsync(MessageKind.Document, "text/plain", ReadOnlyMemory<byte>.Empty, default);

        Assert.Equal(ScanVerdict.Clean, veredito);
        var visto = Assert.Single(servidor.Recebidos);
        Assert.Empty(visto.Conteudo);
        Assert.True(visto.TerminouComBlocoZero);
    }

    [Fact]
    public async Task Resposta_FOUND_e_infectado_e_o_nome_da_assinatura_nao_decide_nada()
    {
        var servidor = Subir(ModoDoClamd.Found);

        var veredito = await Cliente(servidor).ScanAsync(MessageKind.Document, "application/pdf", "qualquer coisa"u8.ToArray(), default);

        Assert.Equal(ScanVerdict.Infected, veredito);
    }

    [Theory]
    [InlineData(ModoDoClamd.ErroDeLimite)]
    [InlineData(ModoDoClamd.FecharSemResponder)]
    [InlineData(ModoDoClamd.Lixo)]
    [InlineData(ModoDoClamd.OkNoMeioDoTexto)]
    [InlineData(ModoDoClamd.RespostaVazia)]
    public async Task Qualquer_outra_resposta_e_indisponivel_e_nunca_limpo(ModoDoClamd modo)
    {
        var servidor = Subir(modo);

        var veredito = await Cliente(servidor).ScanAsync(MessageKind.Image, "image/png", "conteudo"u8.ToArray(), default);

        Assert.Equal(ScanVerdict.Unavailable, veredito);
    }

    [Fact]
    public async Task Daemon_que_nao_responde_estoura_o_prazo_e_e_indisponivel()
    {
        var servidor = Subir(ModoDoClamd.NaoResponde);
        var relogio = System.Diagnostics.Stopwatch.StartNew();

        var veredito = await Cliente(servidor, milissegundos: 300).ScanAsync(MessageKind.Image, "image/png", "conteudo"u8.ToArray(), default);

        Assert.Equal(ScanVerdict.Unavailable, veredito);
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(10), "o prazo do verificador precisa limitar a espera");
    }

    [Fact]
    public async Task Conexao_recusada_e_indisponivel()
    {
        int portaSemDono;
        using (var temporario = new TcpListener(IPAddress.Loopback, 0))
        {
            temporario.Start();
            portaSemDono = ((IPEndPoint)temporario.LocalEndpoint).Port;
        } // fechado: ninguém escuta mais nessa porta

        var veredito = await new ClamAvAttachmentScanner("127.0.0.1", portaSemDono, TimeSpan.FromSeconds(2)).ScanAsync(MessageKind.Document, "application/pdf", "x"u8.ToArray(), default);

        Assert.Equal(ScanVerdict.Unavailable, veredito);
    }

    [Fact]
    public async Task Cancelamento_de_quem_chamou_propaga_e_nao_vira_veredito()
    {
        var servidor = Subir(ModoDoClamd.NaoResponde);
        using var cancelamento = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Cliente(servidor, milissegundos: 30_000).ScanAsync(MessageKind.Image, "image/png", "conteudo"u8.ToArray(), cancelamento.Token));
    }

    [Fact]
    public async Task A_amostra_EICAR_e_detectada_sem_nenhum_verificador_e_sem_chamar_o_daemon()
    {
        var motor = new MotorQueContaChamadas(ScanVerdict.Clean);
        var configurado = new ConfiguredAttachmentScanner(motor);
        var amostra = EicarTestSample.Bytes();
        var embutida = Enumerable.Repeat((byte)'a', 1000).Concat(amostra).Concat(Enumerable.Repeat((byte)'b', 1000)).ToArray();

        Assert.Equal(ScanVerdict.Infected, await configurado.ScanAsync(MessageKind.Document, "text/plain", amostra, default));
        Assert.Equal(ScanVerdict.Infected, await configurado.ScanAsync(MessageKind.Document, "text/plain", embutida, default));
        Assert.Equal(ScanVerdict.Infected, await new ConfiguredAttachmentScanner(null).ScanAsync(MessageKind.Document, "text/plain", amostra, default));
        Assert.Equal(0, motor.Chamadas); // a detecção embutida vale antes e independe do motor
    }

    [Fact]
    public async Task Sem_motor_o_que_nao_e_EICAR_e_NotScanned_e_com_motor_vale_o_veredito_do_motor()
    {
        var conteudo = "um documento comum"u8.ToArray();

        Assert.Equal(ScanVerdict.NotScanned, await new ConfiguredAttachmentScanner(null).ScanAsync(MessageKind.Document, "text/plain", conteudo, default));

        foreach (var veredito in new[] { ScanVerdict.Clean, ScanVerdict.Infected, ScanVerdict.Unavailable })
        {
            var motor = new MotorQueContaChamadas(veredito);
            Assert.Equal(veredito, await new ConfiguredAttachmentScanner(motor).ScanAsync(MessageKind.Document, "text/plain", conteudo, default));
            Assert.Equal(1, motor.Chamadas);
        }
    }

    [Fact]
    public void A_amostra_EICAR_nunca_esta_inteira_no_codigo_fonte_do_repositorio()
    {
        // O texto completo no repositório faria os antivírus das máquinas tratarem o próprio código como infectado.
        var amostra = Encoding.ASCII.GetString(EicarTestSample.Bytes());
        var raiz = ProcurarRaiz();
        var achados = Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(c => !c.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !c.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(c => File.ReadAllText(c).Contains(amostra, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(achados);
    }

    private static string ProcurarRaiz()
    {
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "CLAUDE.md")))
        {
            pasta = pasta.Parent;
        }

        return pasta is null ? throw new InvalidOperationException("Raiz do repositório não encontrada.") : Path.Combine(pasta.FullName, "backend");
    }

    private sealed class MotorQueContaChamadas : IAttachmentScanner
    {
        private readonly ScanVerdict _veredito;
        private int _chamadas;

        public MotorQueContaChamadas(ScanVerdict veredito) => _veredito = veredito;

        public int Chamadas => Volatile.Read(ref _chamadas);

        public Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _chamadas);
            return Task.FromResult(_veredito);
        }
    }
}
