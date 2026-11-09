using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O lado do Conversations do ARQUIVO que o atendente envia (ADR 0031), com PostgreSQL real e a pasta privada de teste: o arquivo é validado pelo conteúdo, guardado e gravado com a mensagem e o evento de envio na
/// mesma operação, converge na repetição do pedido, nunca fica guardado quando a mensagem não foi gravada, só abre para quem pode ler a sessão do mesmo tenant, o Sender só lê o da saída, e o prazo de guarda apaga o
/// conteúdo (e os objetos órfãos) sem tocar na mensagem.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnvioDeArquivosTests(PostgresFixture banco)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n%%EOF\n");
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

    private static OutboundAttachment Imagem(string legenda = "Segue a foto", byte[]? conteudo = null, string nome = "foto.png") => new(nome, "image/png", conteudo ?? Png, legenda);

    private static async Task<(CenarioDeConversas C, Guid Sessao, Guid Autor)> HumanoAsync(PostgresFixture banco, string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var sessao = (await c.EntrarAsync("5511999990411", quando: DateTimeOffset.UtcNow)).SessionId;
        var autor = Guid.NewGuid();
        var estado = (await c.SessaoAsync(sessao))!.StateVersion;
        Assert.Equal(TransitionOutcome.Applied, (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, autor, estado)).Outcome);
        return (c, sessao, autor);
    }

    private static async Task<RegisterHumanReplyResult> EnviarAsync(
        CenarioDeConversas c, Guid sessao, Guid autor, Guid operacao, ReplyContent conteudo, Guid? responsavelExigido = null, RequestContext? contexto = null)
    {
        var s = (await c.SessaoAsync(sessao))!;
        return await c.ComoAsync(
            contexto ?? c.ContextoDeSistema,
            sp => sp.GetRequiredService<IHandlingCommands>().RegisterHumanReplyAsync(new RegisterHumanReplyCommand(
                sessao, operacao, autor, new ReplyPrecondition(s.HandlingPeriod, s.PlacementEpoch, ResponderMatch.Exact, responsavelExigido ?? autor), s.StateVersion, 0, conteudo)));
    }

    private string PastaDoTenant(CenarioDeConversas c) => Path.Combine(banco.PastaDeAnexos, c.Tenant.TenantId.Value.ToString("N"));

    private int Objetos(CenarioDeConversas c) => Directory.Exists(PastaDoTenant(c)) ? Directory.GetFiles(PastaDoTenant(c), "*.bin").Length : 0;

    private Task<T> BancoAsync<T>(CenarioDeConversas c, Func<ConversationsDbContext, Task<T>> consulta) =>
        c.ComoAsync(c.ContextoDeSistema, sp => consulta(sp.GetRequiredService<ConversationsDbContext>()));

    private static async Task<byte[]?> LerFluxoAsync(Stream? fluxo)
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

    private static Task<byte[]?> LerParaOSenderAsync(CenarioDeConversas c, Guid mensagem, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, async sp => await LerFluxoAsync(await sp.GetRequiredService<IOutboundAttachmentReader>().OpenAsync(mensagem)));

    private static Task<OutboundDispatchView?> DespachoAsync(CenarioDeConversas c, Guid mensagem, RequestContext? contexto = null) =>
        c.ComoAsync(contexto ?? c.ContextoDeSistema, sp => sp.GetRequiredService<IOutboundDispatchQueries>().GetAsync(mensagem));

    private static async Task<byte[]?> AbrirComoAtendenteAsync(CenarioDeConversas c, Guid sessao, Guid mensagem, RequestContext contexto) =>
        await c.ComoAsync(contexto, async sp =>
        {
            await using var conteudo = await sp.GetRequiredService<IAttachmentQueries>().OpenAsync(sessao, mensagem);
            return conteudo is null ? null : await LerFluxoAsync(conteudo.Content);
        });

    // O envio

    [Fact]
    public async Task O_arquivo_e_validado_guardado_e_gravado_com_a_mensagem_e_o_evento_de_envio_e_o_Sender_o_le_pelo_contexto_de_sistema()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqenvia");

        var resultado = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem("Segue a foto\u0007 do exame")));

        Assert.Equal(RegisterHumanReplyOutcome.Applied, resultado.Outcome);
        Assert.Equal(1, Objetos(c));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, sessao));

        // O despacho que o Sender lê: tipo de mídia, tipo verificado, nome e legenda saneados, tamanho, e SEM texto (a legenda mora no arquivo, nunca no texto que a IA lê).
        var despacho = await DespachoAsync(c, resultado.MessageId!.Value);
        Assert.Equal((MessageKind.Image, MessageAuthor.Human, (string?)null), (despacho!.Kind, despacho.Author, despacho.Text));
        Assert.Equal(new OutboundAttachmentInfo(MessageKind.Image, "image/png", "foto.png", Png.Length, "Segue a foto do exame"), despacho.Attachment);
        Assert.Equal(Png, await LerParaOSenderAsync(c, resultado.MessageId.Value));

        // O histórico mostra a mensagem como imagem do atendente, com o arquivo disponível e entregue ao Sender como "pedido".
        var historico = (await c.HistoricoAsync(sessao))!.Items.Single(m => m.Author == MessageAuthor.Human);
        Assert.Equal((MessageKind.Image, MessageDirection.Outbound, ContentState.Ready, (string?)null, DeliveryState.Requested), (historico.Kind, historico.Direction, historico.ContentState, historico.Text, historico.DeliveryState));
        Assert.Equal(new AttachmentView(historico.Attachment!.Id, AttachmentStatus.Available, "image/png", "foto.png", Png.Length, null, "Segue a foto do exame"), historico.Attachment);
    }

    [Fact]
    public async Task O_documento_e_o_audio_tem_o_tipo_de_midia_pelo_conteudo_e_o_atendente_autorizado_abre_o_mesmo_arquivo()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqtipos");
        byte[] ogg = [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 19, .. "OpusHead"u8, 1, 1, 0x38, 1, 0x80, 0xBB, 0, 0, 0, 0, 0];

        var documento = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: new OutboundAttachment("Laudo março.pdf", "application/pdf", Pdf, "O laudo")));
        var audio = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: new OutboundAttachment("nota", "audio/ogg", ogg)));

        Assert.Equal(RegisterHumanReplyOutcome.Applied, documento.Outcome);
        Assert.Equal(RegisterHumanReplyOutcome.Applied, audio.Outcome);
        var historico = (await c.HistoricoAsync(sessao))!.Items.Where(m => m.Author == MessageAuthor.Human).OrderBy(m => m.MessageSeq).ToList();
        Assert.Equal((MessageKind.Document, "application/pdf", "Laudo março.pdf"), (historico[0].Kind, historico[0].Attachment!.MimeType, historico[0].Attachment!.FileName));
        Assert.Equal((MessageKind.Audio, "audio/ogg", "nota.ogg", (string?)null), (historico[1].Kind, historico[1].Attachment!.MimeType, historico[1].Attachment!.FileName, historico[1].Attachment!.Caption));

        // O atendente com leitura da organização abre o arquivo da mensagem DESTA sessão.
        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        Assert.Equal(Pdf, await AbrirComoAtendenteAsync(c, sessao, documento.MessageId!.Value, leitor));
    }

    // Idempotência

    [Fact]
    public async Task O_mesmo_pedido_converge_sem_segunda_mensagem_segundo_objeto_nem_segundo_evento_e_outro_efeito_com_o_mesmo_identificador_e_recusado()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqidem");
        var operacao = Guid.NewGuid();
        var primeira = await EnviarAsync(c, sessao, autor, operacao, new ReplyContent(string.Empty, Attachment: Imagem()));

        var repetida = await EnviarAsync(c, sessao, autor, operacao, new ReplyContent(string.Empty, Attachment: Imagem()));
        Assert.Equal((RegisterHumanReplyOutcome.AlreadyApplied, primeira.MessageId), (repetida.Outcome, repetida.MessageId));

        // Outro conteúdo, outra legenda, outro nome, texto no lugar do arquivo: outros efeitos com o mesmo identificador.
        var outros = new[]
        {
            new ReplyContent(string.Empty, Attachment: new OutboundAttachment("foto.png", "image/png", [.. Png, 0], "Segue a foto")),
            new ReplyContent(string.Empty, Attachment: Imagem("Outra legenda")),
            new ReplyContent(string.Empty, Attachment: Imagem(nome: "outro.png")),
            new ReplyContent("Olá"),
        };
        foreach (var outro in outros)
        {
            Assert.Equal(RegisterHumanReplyOutcome.OperationIdReused, (await EnviarAsync(c, sessao, autor, operacao, outro)).Outcome);
        }

        Assert.Equal(1, Objetos(c));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, sessao));
        Assert.Single((await c.HistoricoAsync(sessao))!.Items, m => m.Author == MessageAuthor.Human);
        Assert.Equal(1, await BancoAsync(c, db => db.Set<MessageAttachment>().AsNoTracking().CountAsync(a => a.Direction == MessageDirection.Outbound)));
    }

    [Fact]
    public async Task Pedidos_iguais_ao_mesmo_tempo_gravam_uma_so_mensagem_e_um_so_objeto()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqconc");
        var operacao = Guid.NewGuid();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => EnviarAsync(c, sessao, autor, operacao, new ReplyContent(string.Empty, Attachment: Imagem()))));

        // A trava da sessão serializa: um aplica, os outros convergem (uma versão de estado velha pode pedir a releitura, mas nunca grava de novo).
        Assert.Equal(1, resultados.Count(r => r.Outcome == RegisterHumanReplyOutcome.Applied));
        Assert.All(resultados.Where(r => r.Outcome != RegisterHumanReplyOutcome.Applied), r => Assert.Contains(r.Outcome, new[] { RegisterHumanReplyOutcome.AlreadyApplied, RegisterHumanReplyOutcome.StaleState }));
        Assert.Single((await c.HistoricoAsync(sessao))!.Items, m => m.Author == MessageAuthor.Human);
        Assert.Equal(1, Objetos(c));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, sessao));
    }

    // O que é recusado não grava nada

    [Theory]
    [InlineData("empty", "foto.png", "image/png", "")]
    [InlineData("type-not-allowed", "programa.exe", "application/octet-stream", "")]
    [InlineData("content-mismatch", "foto.pdf", "image/png", "")]
    [InlineData("caption-too-long", "foto.png", "image/png", "L")]
    public async Task O_arquivo_recusado_pela_politica_nao_grava_mensagem_evento_nem_objeto(string motivo, string nome, string tipo, string marca)
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqrecusa" + motivo.Length);
        var conteudo = motivo == "empty" ? [] : motivo == "type-not-allowed" ? Exe : Png;
        var legenda = marca == "L" ? new string('x', 1025) : null;

        var resultado = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: new OutboundAttachment(nome, tipo, conteudo, legenda)));

        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, motivo), (resultado.Outcome, resultado.RejectionCode));
        Assert.Equal(0, Objetos(c));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, sessao));
        Assert.DoesNotContain((await c.HistoricoAsync(sessao))!.Items, m => m.Author == MessageAuthor.Human);
    }

    [Fact]
    public async Task O_arquivo_acima_do_maximo_do_tipo_e_o_audio_com_legenda_sao_recusados_com_o_motivo()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqlimite");
        var grande = new byte[(5 * 1024 * 1024) + 1];
        Png.CopyTo(grande, 0);

        var imagem = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: new OutboundAttachment("grande.png", "image/png", grande)));
        var audio = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(
            string.Empty, Attachment: new OutboundAttachment("a.aac", "audio/aac", [0xFF, 0xF1, 0x50, 0x80, 0, 0, 0, 0], "Legenda")));

        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "too-large"), (imagem.Outcome, imagem.RejectionCode));
        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "caption-not-supported"), (audio.Outcome, audio.RejectionCode));
        Assert.Equal(0, Objetos(c));
    }

    [Fact]
    public async Task Arquivo_com_texto_ou_com_bloco_de_modelo_e_recusado_e_o_texto_continua_exigindo_texto()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqforma");

        var comTexto = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent("texto junto", Attachment: Imagem()));
        var comBloco = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, null, new ChannelData(1, new Dictionary<string, string> { ["tpl.id"] = "x" }), Imagem()));
        var semNada = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty));

        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "invalid-shape"), (comTexto.Outcome, comTexto.RejectionCode));
        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "invalid-shape"), (comBloco.Outcome, comBloco.RejectionCode));
        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, (string?)null), (semNada.Outcome, semNada.RejectionCode));
        Assert.Equal(0, Objetos(c));
    }

    [Fact]
    public async Task Quando_o_atendimento_mudou_o_arquivo_nao_chega_ao_armazenamento()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqmudou");

        // Outro responsável (a pré-condição exige o autor), sessão encerrada: nada é gravado e nenhum objeto é escrito.
        var outro = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()), responsavelExigido: Guid.NewGuid());
        Assert.Equal(RegisterHumanReplyOutcome.HandlingChanged, outro.Outcome);
        Assert.Equal(0, Objetos(c));

        await c.EncerrarSessaoAsync(sessao);
        var encerrada = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        Assert.Equal(RegisterHumanReplyOutcome.SessionClosed, encerrada.Outcome);
        Assert.Equal(0, Objetos(c));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.OutboundMessageRequested, sessao));
    }

    // Autorização e isolamento

    [Fact]
    public async Task Quem_nao_e_o_contexto_de_sistema_nao_envia_nem_le_para_o_Sender_e_a_leitura_do_Sender_nunca_traz_o_arquivo_do_cliente()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqautoriza");
        var enviado = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        var usuario = c.Usuario(c.OrganizacaoId, Enum.GetValues<Permission>().Where(p => p != Permission.SystemOperation).ToArray());

        await Assert.ThrowsAsync<AccessDeniedException>(() => EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()), contexto: usuario));
        await Assert.ThrowsAsync<AccessDeniedException>(() => LerParaOSenderAsync(c, enviado.MessageId!.Value, usuario));
        Assert.Equal(Png, await LerParaOSenderAsync(c, enviado.MessageId!.Value));

        // O arquivo que o CLIENTE mandou é de entrada: o Sender nunca o recebe.
        var entrada = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(new IngestInboundMessageCommand(
            c.CanalId, "5511999990411", null, c.OrganizacaoId, null, $"evt-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, MessageKind.Image, null, Attachment: new InboundAttachment("mid-1", "image/png", null, null))));
        Assert.Null(await LerParaOSenderAsync(c, entrada.MessageId));
        Assert.Null(await DespachoAsync(c, entrada.MessageId));
    }

    [Fact]
    public async Task Outro_tenant_outra_organizacao_e_outra_sessao_nao_abrem_nem_leem_o_arquivo_enviado()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqisola");
        var enviado = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        var outro = await CenarioDeConversas.CriarAsync(banco, "arqisolab");
        var outraSessao = (await c.EntrarAsync("5511999990499", quando: DateTimeOffset.UtcNow)).SessionId;

        // Outro tenant: a mensagem, o despacho e o arquivo são inexistentes.
        Assert.Null(await LerParaOSenderAsync(outro, enviado.MessageId!.Value));
        Assert.Null(await DespachoAsync(outro, enviado.MessageId.Value));
        Assert.Null(await AbrirComoAtendenteAsync(outro, sessao, enviado.MessageId.Value, outro.Usuario(outro.OrganizacaoId, Permission.ConversationRead)));

        // Outra organização do mesmo tenant, ou sem permissão de leitura: sem arquivo.
        var outraOrganizacao = (await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização")).Id;
        Assert.Null(await AbrirComoAtendenteAsync(c, sessao, enviado.MessageId.Value, c.Usuario(outraOrganizacao, Permission.ConversationRead)));
        Assert.Null(await AbrirComoAtendenteAsync(c, sessao, enviado.MessageId.Value, c.Usuario(c.OrganizacaoId, Permission.OrganizationRead)));

        // A mensagem de uma sessão não abre pela outra.
        Assert.Null(await AbrirComoAtendenteAsync(c, outraSessao, enviado.MessageId.Value, c.Usuario(c.OrganizacaoId, Permission.ConversationRead)));
        Assert.Equal(Png, await AbrirComoAtendenteAsync(c, sessao, enviado.MessageId.Value, c.Usuario(c.OrganizacaoId, Permission.ConversationRead)));
    }

    // Reenvio

    [Fact]
    public async Task A_mensagem_de_arquivo_que_falhou_nao_e_reenviada_pelo_pedido_de_reenvio_e_o_atendente_envia_o_arquivo_de_novo()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqreenvio");
        var enviado = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        await c.EntregaAsync(enviado.MessageId!.Value, DeliveryState.Sending);
        await c.EntregaAsync(enviado.MessageId.Value, DeliveryState.Failed, "send-failed.upload-131053");
        var gerente = c.Usuario(c.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);

        var pedido = await c.PedirReenvioAsync(enviado.MessageId.Value, Guid.NewGuid(), gerente);

        Assert.Equal(ResendOutcome.MediaNotSupported, pedido.Outcome);
        Assert.Single((await c.HistoricoAsync(sessao))!.Items, m => m.Author == MessageAuthor.Human);

        // Enviar de novo (outro pedido) é uma nova resposta normal.
        var novo = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        Assert.Equal(RegisterHumanReplyOutcome.Applied, novo.Outcome);
    }

    // Prazo de guarda

    private OutboundAttachmentRetentionTask Tarefa(IServiceProvider sp) => new(
        sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IAttachmentContentStore>(), TimeProvider.System);

    private static RetentionPass Passada(CenarioDeConversas c, int dias = 30) =>
        new(c.Tenant.TenantId.Value, Array.Empty<Guid>(), dias, DateTimeOffset.UtcNow - TimeSpan.FromDays(dias), "teste");

    private Task EnvelhecerAsync(CenarioDeConversas c, Guid mensagem, int dias) =>
        c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            var antes = DateTimeOffset.UtcNow - TimeSpan.FromDays(dias);
            return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversations.message_attachments SET created_at = {antes} WHERE message_id = {mensagem}");
        });

    [Fact]
    public async Task Passado_o_prazo_o_conteudo_e_apagado_o_arquivo_fica_indisponivel_e_a_mensagem_e_o_nome_ficam_no_historico()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqprazo");
        var velho = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem("Velho", nome: "velho.png")));
        var novo = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem("Novo", nome: "novo.png")));
        await EnvelhecerAsync(c, velho.MessageId!.Value, 31);
        Assert.Equal(2, Objetos(c));

        // Só relatório: nada muda.
        var simulacao = await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: true));
        Assert.Equal((1, 0), (simulacao.Eligible, simulacao.Deleted));
        Assert.Equal(2, Objetos(c));

        var resultado = await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: false));

        Assert.Equal((1, 1), (resultado.Eligible, resultado.Deleted));
        Assert.Equal(1, Objetos(c)); // só o do arquivo novo
        var historico = (await c.HistoricoAsync(sessao))!.Items.Where(m => m.Author == MessageAuthor.Human).OrderBy(m => m.MessageSeq).ToList();
        Assert.Equal(
            new AttachmentView(historico[0].Attachment!.Id, AttachmentStatus.Unavailable, null, "velho.png", null, "retention", "Velho"),
            historico[0].Attachment);
        Assert.Equal(AttachmentStatus.Available, historico[1].Attachment!.Status);
        Assert.Equal((MessageKind.Image, DeliveryState.Requested), (historico[0].Kind, historico[0].DeliveryState));

        // Sem conteúdo: o Sender não recebe arquivo (a mensagem falha de forma visível, sem chamar a Meta) e ninguém abre.
        Assert.Null((await DespachoAsync(c, velho.MessageId.Value))!.Attachment);
        Assert.Null(await LerParaOSenderAsync(c, velho.MessageId.Value));
        Assert.Null(await AbrirComoAtendenteAsync(c, sessao, velho.MessageId.Value, c.Usuario(c.OrganizacaoId, Permission.ConversationRead)));
        Assert.Equal(Png, await LerParaOSenderAsync(c, novo.MessageId!.Value));

        // Repetir é seguro e não acha mais nada.
        var repetido = await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: false));
        Assert.Equal((0, 0), (repetido.Eligible, repetido.Deleted));

        // O pedido repetido de um arquivo cujo prazo venceu ainda é reconhecido como o mesmo efeito.
        var operacao = Guid.NewGuid();
        var original = await EnviarAsync(c, sessao, autor, operacao, new ReplyContent(string.Empty, Attachment: Imagem("Outra", nome: "outra.png")));
        await EnvelhecerAsync(c, original.MessageId!.Value, 31);
        await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: false));
        var depois = await EnviarAsync(c, sessao, autor, operacao, new ReplyContent(string.Empty, Attachment: Imagem("Outra", nome: "outra.png")));
        Assert.Equal((RegisterHumanReplyOutcome.AlreadyApplied, original.MessageId), (depois.Outcome, depois.MessageId));
    }

    [Fact]
    public async Task O_prazo_e_da_empresa_da_passada_e_nao_apaga_o_de_outra_empresa_nem_o_arquivo_do_cliente()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqprazoa");
        var (outro, outraSessao, outroAutor) = await HumanoAsync(banco, "arqprazob");
        var meu = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        var dele = await EnviarAsync(outro, outraSessao, outroAutor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        await EnvelhecerAsync(c, meu.MessageId!.Value, 40);
        await EnvelhecerAsync(outro, dele.MessageId!.Value, 40);

        // A passada é só da empresa A: a de B continua inteira.
        await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: false));

        Assert.Equal((0, 1), (Objetos(c), Objetos(outro)));
        Assert.Equal(AttachmentStatus.Available, (await outro.HistoricoAsync(outraSessao))!.Items.Single(m => m.Author == MessageAuthor.Human).Attachment!.Status);

        // O arquivo do CLIENTE nunca entra (é de entrada), por mais antigo que seja.
        var entrada = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(new IngestInboundMessageCommand(
            c.CanalId, "5511999990411", null, c.OrganizacaoId, null, $"evt-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, MessageKind.Image, null, Attachment: new InboundAttachment("mid-9", "image/png", null, null))));
        await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IAttachmentCommands>().CompleteAsync(new CompleteAttachmentCommand(entrada.MessageId, Png, "image/png", null)));
        await EnvelhecerAsync(c, entrada.MessageId, 400);
        var passada = await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: false));
        Assert.Equal((0, 0), (passada.Eligible, passada.Deleted));
        Assert.Equal(1, Objetos(c));
    }

    [Fact]
    public async Task O_objeto_antigo_sem_linha_e_recolhido_o_novo_e_o_que_tem_linha_nao()
    {
        var (c, sessao, autor) = await HumanoAsync(banco, "arqorfao");
        var vivo = await EnviarAsync(c, sessao, autor, Guid.NewGuid(), new ReplyContent(string.Empty, Attachment: Imagem()));
        var loja = banco.NovoEscopo(c.ContextoDeSistema);
        await using var _ = loja;
        var armazenamento = loja.ServiceProvider.GetRequiredService<IAttachmentContentStore>();

        // Dois objetos sem nenhuma linha (uma gravação que não se completou): um antigo e um recente.
        var antigoId = Guid.NewGuid();
        var recenteId = Guid.NewGuid();
        var antigo = await armazenamento.WriteAsync(c.Tenant.TenantId, antigoId, Png, default);
        await armazenamento.WriteAsync(c.Tenant.TenantId, recenteId, Png, default);
        File.SetLastWriteTimeUtc(Path.Combine(banco.PastaDeAnexos, antigo.Replace('/', Path.DirectorySeparatorChar)), DateTime.UtcNow.AddDays(-45));
        Assert.Equal(3, Objetos(c));

        var simulacao = await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: true));
        Assert.Equal((1, 0), (simulacao.Eligible, simulacao.Deleted));
        Assert.Equal(3, Objetos(c));

        var resultado = await c.ComoAsync(c.ContextoDeSistema, sp => Tarefa(sp).RunAsync(Passada(c), dryRun: false));

        Assert.Equal((1, 1), (resultado.Eligible, resultado.Deleted));
        Assert.Equal(2, Objetos(c)); // o recente (pode ser uma gravação em andamento) e o que tem linha
        Assert.Equal(Png, await LerParaOSenderAsync(c, vivo.MessageId!.Value));

        // O objeto de outro tenant nunca é apagado por uma chave que não é dele.
        var outro = await CenarioDeConversas.CriarAsync(banco, "arqorfaob");
        await armazenamento.DeleteAsync(outro.Tenant.TenantId, antigo, default);
        await armazenamento.DeleteAsync(c.Tenant.TenantId, "../../fora.bin", default);
        Assert.Equal(2, Objetos(c));
    }
}
