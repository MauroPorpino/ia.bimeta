using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>A composição do Conversations com as opções de arquivo do teste (ADR 0032) e a pasta própria dela.</summary>
internal sealed record ComposicaoDeAnexos(ServiceProvider Provedor, string Pasta);

/// <summary>Um verificador de teste: devolve o veredito configurado e conta as chamadas (nenhum processo externo).</summary>
internal sealed class VerificadorDeTeste : IAttachmentScanner
{
    private int _chamadas;

    /// <summary>A marca que o teste põe no conteúdo para ele valer como infectado (sem gravar a amostra EICAR em disco: o antivírus da máquina a bloquearia).</summary>
    public static readonly byte[] MarcaInfectada = "INFECTADO-DE-TESTE-NAO-E-VIRUS"u8.ToArray();

    public ScanVerdict Resposta { get; set; } = ScanVerdict.Clean;

    public int Chamadas => Volatile.Read(ref _chamadas);

    public Task<ScanVerdict> ScanAsync(MessageKind kind, string mimeType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _chamadas);
        return Task.FromResult(content.Span.IndexOf(MarcaInfectada) >= 0 ? ScanVerdict.Infected : Resposta);
    }
}

/// <summary>A trilha de auditoria de teste: guarda o que o expurgo registrou e pode falhar de propósito.</summary>
internal sealed class AuditoriaDeExpurgoDeTeste : IRetentionAuditSink
{
    private readonly List<RetentionAuditItem> _itens = [];

    public bool Falhar { get; set; }

    public IReadOnlyList<RetentionAuditItem> Itens
    {
        get
        {
            lock (_itens)
            {
                return [.. _itens];
            }
        }
    }

    public Task RecordAsync(IReadOnlyCollection<RetentionAuditItem> items, CancellationToken cancellationToken = default)
    {
        if (Falhar)
        {
            throw new InvalidOperationException("a trilha de auditoria está fora do ar");
        }

        lock (_itens)
        {
            _itens.AddRange(items);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Os atalhos dos testes de segurança dos arquivos (ADR 0032): composições com cota, verificador e cifra próprios sobre o MESMO banco, uma pasta por composição, e as operações do ciclo (receber, concluir, abrir, enviar)
/// sempre pelo contexto de sistema do tenant do cenário. As composições são descartadas no fim do teste.
/// </summary>
internal sealed class AnexosDeTeste(PostgresFixture banco) : IAsyncDisposable
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];

    public static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n%%EOF\n");

    /// <summary>Uma chave de cifra de teste (Base64 de 32 bytes), diferente a cada chamada.</summary>
    public static string NovaChave() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private readonly List<ServiceProvider> _composicoes = [];
    private int _sequencia;

    public async ValueTask DisposeAsync()
    {
        foreach (var provedor in _composicoes)
        {
            await provedor.DisposeAsync();
        }
    }

    public ComposicaoDeAnexos Criar(Action<AttachmentOptions>? configurar = null, IAttachmentScanner? verificador = null, string? pasta = null)
    {
        var raiz = pasta ?? Path.Combine(banco.PastaDeAnexos, "seg-" + Guid.NewGuid().ToString("N"));
        var provedor = banco.ComporComAnexos(opcoes =>
        {
            opcoes.RootPath = raiz;
            configurar?.Invoke(opcoes);
        }, verificador);
        _composicoes.Add(provedor);
        return new ComposicaoDeAnexos(provedor, raiz);
    }

    /// <summary>Uma composição com a verificação EXIGIDA (o verificador de teste no lugar do clamd).</summary>
    public ComposicaoDeAnexos CriarComVerificacaoExigida(VerificadorDeTeste motor, Action<AttachmentOptions>? configurar = null, string? pasta = null) => Criar(
        opcoes =>
        {
            opcoes.Scanner.Mode = AttachmentScannerMode.ClamAv;
            opcoes.Scanner.Required = true;
            configurar?.Invoke(opcoes);
        },
        new ConfiguredAttachmentScanner(motor),
        pasta);

    /// <summary>Uma composição com um verificador que NÃO é exigido (o arquivo passa como "não verificado" se ele falhar).</summary>
    public ComposicaoDeAnexos CriarComVerificadorOpcional(VerificadorDeTeste motor, Action<AttachmentOptions>? configurar = null, string? pasta = null) => Criar(
        opcoes =>
        {
            opcoes.Scanner.Mode = AttachmentScannerMode.ClamAv;
            configurar?.Invoke(opcoes);
        },
        new ConfiguredAttachmentScanner(motor),
        pasta);

    public static InboundAttachment Midia(string? mime = "image/png", string? nome = null, string? legenda = null) => new($"mid-{Guid.NewGuid():N}", mime, nome, legenda);

    public async Task<T> ComoAsync<T>(ComposicaoDeAnexos k, CenarioDeConversas c, Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = PostgresFixture.NovoEscopoDe(k.Provedor, c.ContextoDeSistema);
        return await acao(escopo.ServiceProvider);
    }

    public Task<IngestInboundMessageResult> IngerirAsync(ComposicaoDeAnexos k, CenarioDeConversas c, MessageKind tipo, InboundAttachment anexo, string contato = "5511999990001") =>
        ComoAsync(k, c, sp => sp.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(new IngestInboundMessageCommand(
            c.CanalId, contato, null, c.OrganizacaoId, null, $"evt-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, tipo, null, Attachment: anexo)));

    /// <summary>Recebe uma imagem e devolve a mensagem (o arquivo nasce pendente).</summary>
    public Task<IngestInboundMessageResult> ReceberImagemAsync(ComposicaoDeAnexos k, CenarioDeConversas c) => IngerirAsync(k, c, MessageKind.Image, Midia());

    public Task<AttachmentChangeResult> CompletarAsync(ComposicaoDeAnexos k, CenarioDeConversas c, Guid mensagem, byte[]? conteudo = null, string? mime = "image/png") =>
        ComoAsync(k, c, sp => sp.GetRequiredService<IAttachmentCommands>().CompleteAsync(new CompleteAttachmentCommand(mensagem, conteudo ?? Png, mime, null)));

    public async Task<byte[]?> AbrirAsync(ComposicaoDeAnexos k, CenarioDeConversas c, Guid sessao, Guid mensagem) =>
        await ComoAsync(k, c, async sp =>
        {
            await using var conteudo = await sp.GetRequiredService<IAttachmentQueries>().OpenAsync(sessao, mensagem);
            return conteudo is null ? null : await LerAsync(conteudo.Content);
        });

    public Task<byte[]?> LerParaOSenderAsync(ComposicaoDeAnexos k, CenarioDeConversas c, Guid mensagem) =>
        ComoAsync(k, c, async sp => await LerAsync(await sp.GetRequiredService<IOutboundAttachmentReader>().OpenAsync(mensagem)));

    public Task<MessageAttachment> LinhaAsync(ComposicaoDeAnexos k, CenarioDeConversas c, Guid mensagem) =>
        ComoAsync(k, c, sp => sp.GetRequiredService<ConversationsDbContext>().Set<MessageAttachment>().AsNoTracking().SingleAsync(a => a.MessageId == mensagem));

    public Task<List<MessageAttachment>> LinhasAsync(ComposicaoDeAnexos k, CenarioDeConversas c) =>
        ComoAsync(k, c, sp => sp.GetRequiredService<ConversationsDbContext>().Set<MessageAttachment>().AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync());

    /// <summary>Envelhece a linha do arquivo (o prazo de guarda conta da criação).</summary>
    public Task EnvelhecerAsync(ComposicaoDeAnexos k, CenarioDeConversas c, Guid mensagem, int dias) =>
        ComoAsync(k, c, async sp =>
        {
            var antes = DateTimeOffset.UtcNow - TimeSpan.FromDays(dias);
            return await sp.GetRequiredService<ConversationsDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE conversations.message_attachments SET created_at = {antes} WHERE message_id = {mensagem}");
        });

    /// <summary>Uma sessão em atendimento humano, para o atendente enviar arquivo (o cenário muda o modo; o envio usa a composição pedida).</summary>
    public static async Task<(Guid Sessao, Guid Autor)> HumanoAsync(CenarioDeConversas c, string contato = "5511999990411")
    {
        var sessao = (await c.EntrarAsync(contato, quando: DateTimeOffset.UtcNow)).SessionId;
        var autor = Guid.NewGuid();
        var estado = (await c.SessaoAsync(sessao))!.StateVersion;
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, autor, estado)).Outcome);
        return (sessao, autor);
    }

    public async Task<RegisterHumanReplyResult> EnviarArquivoAsync(
        ComposicaoDeAnexos k, CenarioDeConversas c, Guid sessao, Guid autor, byte[]? conteudo = null, string nome = "foto.png", string mime = "image/png", string? legenda = "Segue")
    {
        var s = (await c.SessaoAsync(sessao))!;
        return await ComoAsync(k, c, sp => sp.GetRequiredService<IHandlingCommands>().RegisterHumanReplyAsync(new RegisterHumanReplyCommand(
            sessao, Guid.NewGuid(), autor, new ReplyPrecondition(s.HandlingPeriod, s.PlacementEpoch, ResponderMatch.Exact, autor), s.StateVersion, 0,
            new ReplyContent(string.Empty, Attachment: new OutboundAttachment(nome, mime, conteudo ?? Png, legenda)))));
    }

    /// <summary>Os objetos da pasta da composição para o tenant do cenário.</summary>
    public static int Objetos(ComposicaoDeAnexos k, CenarioDeConversas c)
    {
        var pasta = PastaDoTenant(k, c);
        return Directory.Exists(pasta) ? Directory.GetFiles(pasta, "*.bin").Length : 0;
    }

    public static string PastaDoTenant(ComposicaoDeAnexos k, CenarioDeConversas c) => Path.Combine(k.Pasta, c.Tenant.TenantId.Value.ToString("N"));

    /// <summary>O caminho do objeto de um arquivo no disco (a chave de armazenamento da linha).</summary>
    public static string CaminhoDe(ComposicaoDeAnexos k, string chave) => Path.Combine(k.Pasta, chave.Replace('/', Path.DirectorySeparatorChar));

    public static async Task<byte[]?> LerAsync(Stream? fluxo)
    {
        if (fluxo is null)
        {
            return null;
        }

        await using (fluxo)
        {
            using var memoria = new MemoryStream();
            await fluxo.CopyToAsync(memoria);
            return memoria.ToArray();
        }
    }

    /// <summary>O serviço de manutenção da composição (o do host é por escopo; aqui o teste o monta com a trilha de auditoria que quiser).</summary>
    public AttachmentMaintenanceService Manutencao(ComposicaoDeAnexos k, IRetentionAuditSink? auditoria = null) => new(
        k.Provedor.GetRequiredService<IServiceScopeFactory>(),
        k.Provedor.GetRequiredService<EncryptedAttachmentContentStore>(),
        k.Provedor.GetRequiredService<AttachmentOptions>(),
        k.Provedor.GetRequiredService<IAttachmentScanner>(),
        TimeProvider.System,
        auditoria);

    /// <summary>O expurgo por prazo de UM sentido, montado com a trilha de auditoria do teste, sobre a composição.</summary>
    public AttachmentObjectsRetentionTask Expurgo(ComposicaoDeAnexos k, MessageDirection sentido, IRetentionAuditSink? auditoria = null) => sentido == MessageDirection.Inbound
        ? AttachmentObjectsRetentionTask.ForInbound(k.Provedor.GetRequiredService<IServiceScopeFactory>(), k.Provedor.GetRequiredService<IAttachmentContentStore>(), TimeProvider.System, auditoria)
        : AttachmentObjectsRetentionTask.ForOutbound(k.Provedor.GetRequiredService<IServiceScopeFactory>(), k.Provedor.GetRequiredService<IAttachmentContentStore>(), TimeProvider.System, auditoria);

    public int Proximo() => Interlocked.Increment(ref _sequencia);
}
