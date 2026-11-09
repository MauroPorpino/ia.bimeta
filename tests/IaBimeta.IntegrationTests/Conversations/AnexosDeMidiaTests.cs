using System.Security.Cryptography;
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

/// <summary>
/// O ciclo do arquivo de uma mensagem de mídia com PostgreSQL real e a pasta privada de teste (ADR 0028): nasce pendente junto da mensagem, é validado antes de ser guardado, aparece no histórico (com o estado e sem
/// caminho, identificador do provedor ou URL), só abre para quem pode ler a sessão do mesmo tenant e nunca vaza entre tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnexosDeMidiaTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n%%EOF\n");

    private static InboundAttachment Midia(string id = "mid-1", string? mime = "image/png", string? nome = null, string? legenda = null) => new(id, mime, nome, legenda);

    private async Task<IngestInboundMessageResult> IngerirAsync(
        CenarioDeConversas c, MessageKind tipo, InboundAttachment? anexo, string? evento = null, string contato = Contato, string? texto = null)
    {
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(new IngestInboundMessageCommand(
            c.CanalId, contato, null, c.OrganizacaoId, null, evento ?? $"evt-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, tipo, texto, Attachment: anexo));
    }

    private Task<T> ComoSistemaAsync<T>(CenarioDeConversas c, Func<IServiceProvider, Task<T>> acao) => c.ComoAsync(c.ContextoDeSistema, acao);

    private Task<AttachmentChangeResult> CompletarAsync(CenarioDeConversas c, Guid mensagem, byte[] conteudo, string? tipo = "image/png", string? resumo = null) =>
        ComoSistemaAsync(c, sp => sp.GetRequiredService<IAttachmentCommands>().CompleteAsync(new CompleteAttachmentCommand(mensagem, conteudo, tipo, resumo)));

    private Task<AttachmentChangeResult> FalharAsync(CenarioDeConversas c, Guid mensagem, AttachmentStatus estado, string codigo) =>
        ComoSistemaAsync(c, sp => sp.GetRequiredService<IAttachmentCommands>().FailAsync(new FailAttachmentCommand(mensagem, estado, codigo)));

    private async Task<AttachmentView?> VisaoAsync(CenarioDeConversas c, Guid sessao, Guid mensagem, RequestContext? contexto = null)
    {
        var historico = await c.HistoricoAsync(sessao, contexto: contexto);
        return historico!.Items.Single(m => m.Id == mensagem).Attachment;
    }

    private async Task<byte[]?> AbrirAsync(CenarioDeConversas c, Guid sessao, Guid mensagem, RequestContext? contexto = null)
    {
        await using var escopo = banco.NovoEscopo(contexto ?? c.ContextoDeSistema);
        await using var conteudo = await escopo.ServiceProvider.GetRequiredService<IAttachmentQueries>().OpenAsync(sessao, mensagem);
        if (conteudo is null)
        {
            return null;
        }

        using var memoria = new MemoryStream();
        await conteudo.Content.CopyToAsync(memoria);
        return memoria.ToArray();
    }

    private Task<T> BancoAsync<T>(CenarioDeConversas c, Func<ConversationsDbContext, Task<T>> consulta) =>
        ComoSistemaAsync(c, sp => consulta(sp.GetRequiredService<ConversationsDbContext>()));

    // Nascimento

    [Fact]
    public async Task A_mensagem_de_midia_nasce_com_o_arquivo_pendente_na_mesma_transacao_e_o_historico_o_mostra_sem_caminho_nem_id_do_provedor()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxnasce");

        var r = await IngerirAsync(c, MessageKind.Image, Midia(legenda: "Segue a foto\u0007 do exame"));

        Assert.Equal(IngestOutcome.Registered, r.Outcome);
        Assert.Equal(ContentState.Pending, r.ContentState);
        var visao = await VisaoAsync(c, r.SessionId, r.MessageId);
        Assert.NotNull(visao);
        Assert.Equal(AttachmentStatus.Pending, visao.Status);
        Assert.Equal("Segue a foto do exame", visao.Caption);
        Assert.Null(visao.MimeType);
        Assert.Null(visao.SizeBytes);
        Assert.Null(visao.FailureCode);

        // O que sai ao atendente não tem caminho de armazenamento, id do provedor nem URL (a visão simplesmente não tem esses campos).
        var campos = typeof(AttachmentView).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(campos, n => n.Contains("Key", StringComparison.Ordinal) || n.Contains("Provider", StringComparison.Ordinal) || n.Contains("Url", StringComparison.OrdinalIgnoreCase) || n.Contains("Path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mensagem_de_texto_nao_tem_arquivo_e_texto_com_arquivo_e_recusado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxtexto");

        var texto = await IngerirAsync(c, MessageKind.Text, null, texto: "Olá");
        Assert.Null(await VisaoAsync(c, texto.SessionId, texto.MessageId));

        await Assert.ThrowsAsync<ConversationsValidationException>(() => IngerirAsync(c, MessageKind.Text, Midia(), texto: "Olá"));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => IngerirAsync(c, MessageKind.Image, Midia(id: "tem espaco")));
    }

    [Fact]
    public async Task O_mesmo_evento_do_provedor_nao_cria_outro_arquivo_nem_muda_o_primeiro()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxdup");

        var primeira = await IngerirAsync(c, MessageKind.Image, Midia(legenda: "primeira"), evento: "wamid.dup");
        var repetida = await IngerirAsync(c, MessageKind.Image, Midia(id: "outro-id", legenda: "segunda"), evento: "wamid.dup");

        Assert.Equal(IngestOutcome.Duplicate, repetida.Outcome);
        Assert.Equal(primeira.MessageId, repetida.MessageId);
        Assert.Equal(1, await BancoAsync(c, db => db.Set<MessageAttachment>().AsNoTracking().CountAsync(a => a.MessageId == primeira.MessageId)));
        Assert.Equal("primeira", (await VisaoAsync(c, primeira.SessionId, primeira.MessageId))!.Caption);
    }

    // Conclusão

    [Fact]
    public async Task O_arquivo_valido_e_guardado_fica_disponivel_e_abre_com_os_mesmos_bytes_sem_mudar_a_interpretacao_para_a_IA()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxok");
        var r = await IngerirAsync(c, MessageKind.Image, Midia(nome: "../x/foto.png"));

        var plano = await ComoSistemaAsync(c, sp => sp.GetRequiredService<IAttachmentCommands>().GetFetchPlanAsync(r.MessageId));
        Assert.NotNull(plano);
        Assert.Equal((AttachmentStatus.Pending, MessageKind.Image, "mid-1", 5L * 1024 * 1024), (plano.Status, plano.Kind, plano.ProviderMediaId, plano.MaxBytes));
        Assert.Contains("image/png", plano.AllowedMimeTypes);

        var resultado = await CompletarAsync(c, r.MessageId, Png, resumo: Convert.ToHexString(SHA256.HashData(Png)));

        Assert.Equal((AttachmentChangeOutcome.Changed, AttachmentStatus.Available), (resultado.Outcome, resultado.Status));
        var visao = await VisaoAsync(c, r.SessionId, r.MessageId);
        Assert.Equal((AttachmentStatus.Available, "image/png", "foto.png", (long?)Png.Length), (visao!.Status, visao.MimeType, visao.FileName, visao.SizeBytes));
        Assert.Equal(Png, await AbrirAsync(c, r.SessionId, r.MessageId));

        // O arquivo disponível ao atendente NÃO transforma a mídia em texto para a IA: a mensagem segue pendente (o MVP não transcreve nem reconhece imagem).
        var mensagem = (await c.HistoricoAsync(r.SessionId))!.Items.Single(m => m.Id == r.MessageId);
        Assert.Equal(ContentState.Pending, mensagem.ContentState);
        Assert.Null(mensagem.Text);

        // O objeto existe na pasta privada do tenant, com a chave opaca, e o banco guarda só a chave.
        var chave = await BancoAsync(c, db => db.Set<MessageAttachment>().AsNoTracking().Where(a => a.MessageId == r.MessageId).Select(a => a.StorageKey).SingleAsync());
        Assert.Equal(1, Directory.EnumerateFiles(Path.Combine(banco.PastaDeAnexos, c.Tenant.TenantId.Value.ToString("N")), "*.bin").Count(f => chave!.EndsWith(Path.GetFileName(f), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Repetir_a_conclusao_nao_muda_nada_e_nao_cria_outro_objeto()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxrep");
        var r = await IngerirAsync(c, MessageKind.Document, Midia(mime: "application/pdf", nome: "laudo.pdf"));

        var primeira = await CompletarAsync(c, r.MessageId, Pdf, "application/pdf");
        var segunda = await CompletarAsync(c, r.MessageId, Pdf, "application/pdf");
        var falha = await FalharAsync(c, r.MessageId, AttachmentStatus.Unavailable, "expired");

        Assert.Equal(AttachmentChangeOutcome.Changed, primeira.Outcome);
        Assert.Equal(AttachmentChangeOutcome.AlreadyResolved, segunda.Outcome);
        Assert.Equal(AttachmentChangeOutcome.AlreadyResolved, falha.Outcome);
        Assert.Equal(AttachmentStatus.Available, (await VisaoAsync(c, r.SessionId, r.MessageId))!.Status);
        Assert.Equal(Pdf, await AbrirAsync(c, r.SessionId, r.MessageId));
    }

    [Fact]
    public async Task Reinicio_no_meio_da_conclusao_o_objeto_ja_gravado_e_regravado_e_o_arquivo_conclui_sem_duplicar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxreinicio");
        var r = await IngerirAsync(c, MessageKind.Image, Midia());
        var anexoId = await BancoAsync(c, db => db.Set<MessageAttachment>().AsNoTracking().Where(a => a.MessageId == r.MessageId).Select(a => a.Id).SingleAsync());

        // O processo caiu DEPOIS de gravar o objeto e ANTES de marcar o arquivo como disponível: o objeto existe e a linha segue pendente.
        await ComoSistemaAsync(c, sp => sp.GetRequiredService<IAttachmentContentStore>().WriteAsync(c.Tenant.TenantId, anexoId, Png, default));
        Assert.Equal(AttachmentStatus.Pending, (await VisaoAsync(c, r.SessionId, r.MessageId))!.Status);

        // A entrega repetida conclui, e há UM objeto para o arquivo.
        Assert.Equal(AttachmentChangeOutcome.Changed, (await CompletarAsync(c, r.MessageId, Png)).Outcome);
        Assert.Equal(Png, await AbrirAsync(c, r.SessionId, r.MessageId));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(banco.PastaDeAnexos, c.Tenant.TenantId.Value.ToString("N")), $"{anexoId:N}*"));
    }

    // Recusas

    [Fact]
    public async Task Conteudo_que_nao_e_o_que_o_tipo_diz_e_recusado_nada_e_guardado_e_nunca_abre()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxmime");
        var r = await IngerirAsync(c, MessageKind.Image, Midia());
        var html = Encoding.UTF8.GetBytes("<html><script>document.location='http://evil.example'</script></html>");

        var resultado = await CompletarAsync(c, r.MessageId, html, "image/png");

        Assert.Equal((AttachmentChangeOutcome.Rejected, AttachmentStatus.Rejected, "content-mismatch"), (resultado.Outcome, resultado.Status, resultado.FailureCode));
        var visao = await VisaoAsync(c, r.SessionId, r.MessageId);
        Assert.Equal((AttachmentStatus.Rejected, "content-mismatch"), (visao!.Status, visao.FailureCode));
        Assert.Null(await AbrirAsync(c, r.SessionId, r.MessageId));
        Assert.Null(await BancoAsync(c, db => db.Set<MessageAttachment>().AsNoTracking().Where(a => a.MessageId == r.MessageId).Select(a => a.StorageKey).SingleAsync()));
    }

    [Theory]
    [InlineData("application/x-msdownload", "type-not-allowed")]
    [InlineData("image/svg+xml", "type-not-allowed")]
    [InlineData(null, "type-not-allowed")]
    public async Task Tipo_fora_da_lista_e_recusado_com_o_motivo(string? tipo, string motivo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxtipo");
        var r = await IngerirAsync(c, MessageKind.Image, Midia(mime: null));

        var resultado = await CompletarAsync(c, r.MessageId, Png, tipo);

        Assert.Equal((AttachmentStatus.Rejected, motivo), (resultado.Status, resultado.FailureCode));
    }

    [Fact]
    public async Task Resumo_diferente_do_informado_pelo_provedor_e_recusado_por_integridade()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxhash");
        var r = await IngerirAsync(c, MessageKind.Image, Midia());

        var resultado = await CompletarAsync(c, r.MessageId, Png, resumo: Convert.ToHexString(SHA256.HashData(Pdf)));

        Assert.Equal((AttachmentStatus.Rejected, "hash-mismatch"), (resultado.Status, resultado.FailureCode));
    }

    [Fact]
    public async Task Arquivo_acima_do_limite_do_tipo_e_recusado_e_vazio_tambem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxtam");
        var grande = await IngerirAsync(c, MessageKind.Image, Midia(), contato: "5511999990001");
        var vazio = await IngerirAsync(c, MessageKind.Image, Midia(id: "mid-2"), contato: "5511999990002");

        var excesso = new byte[(5 * 1024 * 1024) + 1];
        Array.Copy(Png, excesso, Png.Length);

        Assert.Equal("too-large", (await CompletarAsync(c, grande.MessageId, excesso)).FailureCode);
        Assert.Equal("empty", (await CompletarAsync(c, vazio.MessageId, [])).FailureCode);
    }

    [Fact]
    public async Task Indisponivel_registra_o_motivo_e_nao_ha_o_que_abrir_e_a_conclusao_depois_nao_ressuscita_o_arquivo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxexp");
        var r = await IngerirAsync(c, MessageKind.Audio, Midia(mime: "audio/ogg"));

        var falha = await FalharAsync(c, r.MessageId, AttachmentStatus.Unavailable, "expired");

        Assert.Equal((AttachmentChangeOutcome.Changed, AttachmentStatus.Unavailable), (falha.Outcome, falha.Status));
        var visao = await VisaoAsync(c, r.SessionId, r.MessageId);
        Assert.Equal((AttachmentStatus.Unavailable, "expired"), (visao!.Status, visao.FailureCode));
        Assert.Null(await AbrirAsync(c, r.SessionId, r.MessageId));
        Assert.Equal(AttachmentChangeOutcome.AlreadyResolved, (await CompletarAsync(c, r.MessageId, [0x4F, 0x67, 0x67, 0x53, 0, 2], "audio/ogg")).Outcome);
        Assert.Equal(AttachmentStatus.Unavailable, (await VisaoAsync(c, r.SessionId, r.MessageId))!.Status);
    }

    [Fact]
    public async Task A_falha_so_aceita_o_estado_de_falha_e_mensagem_inexistente_e_NotFound()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxfalha");
        var r = await IngerirAsync(c, MessageKind.Image, Midia());

        await Assert.ThrowsAsync<ConversationsValidationException>(() => FalharAsync(c, r.MessageId, AttachmentStatus.Available, "x"));
        Assert.Equal(AttachmentChangeOutcome.NotFound, (await FalharAsync(c, Guid.NewGuid(), AttachmentStatus.Unavailable, "expired")).Outcome);
        Assert.Equal(AttachmentChangeOutcome.NotFound, (await CompletarAsync(c, Guid.NewGuid(), Png)).Outcome);
        Assert.Null(await ComoSistemaAsync(c, sp => sp.GetRequiredService<IAttachmentCommands>().GetFetchPlanAsync(Guid.NewGuid())));
    }

    // Isolamento e autorização

    [Fact]
    public async Task Outro_tenant_nao_ve_nao_abre_nao_conclui_e_nao_planeja_o_arquivo()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "anxisola");
        var b = await CenarioDeConversas.CriarAsync(banco, "anxisolb");
        var r = await IngerirAsync(a, MessageKind.Image, Midia());
        Assert.Equal(AttachmentChangeOutcome.Changed, (await CompletarAsync(a, r.MessageId, Png)).Outcome);

        // O sistema do tenant B, com os identificadores do tenant A na mão:
        Assert.Null(await AbrirAsync(b, r.SessionId, r.MessageId));
        Assert.Null(await ComoSistemaAsync(b, sp => sp.GetRequiredService<IAttachmentCommands>().GetFetchPlanAsync(r.MessageId)));
        Assert.Equal(AttachmentChangeOutcome.NotFound, (await CompletarAsync(b, r.MessageId, Pdf, "image/png")).Outcome);
        Assert.Equal(AttachmentChangeOutcome.NotFound, (await FalharAsync(b, r.MessageId, AttachmentStatus.Unavailable, "expired")).Outcome);
        Assert.Equal(0, await BancoAsync(b, db => db.Set<MessageAttachment>().AsNoTracking().CountAsync()));

        // O tenant A segue intacto.
        Assert.Equal(Png, await AbrirAsync(a, r.SessionId, r.MessageId));
    }

    [Fact]
    public async Task Abrir_exige_a_leitura_da_sessao_na_organizacao_e_a_mensagem_precisa_ser_da_sessao_informada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxauth");
        var outraOrganizacao = (await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização anx")).Id;
        var r1 = await IngerirAsync(c, MessageKind.Image, Midia(), contato: "5511999990001");
        var r2 = await IngerirAsync(c, MessageKind.Image, Midia(id: "mid-2"), contato: "5511999990002");
        await CompletarAsync(c, r1.MessageId, Png);
        await CompletarAsync(c, r2.MessageId, Png);
        Assert.NotEqual(r1.SessionId, r2.SessionId);

        var leitor = c.Usuario(c.OrganizacaoId, Permission.ConversationRead);
        var deOutraOrganizacao = c.Usuario(outraOrganizacao, Permission.ConversationRead);
        var semPermissao = c.Usuario(c.OrganizacaoId);

        Assert.Equal(Png, await AbrirAsync(c, r1.SessionId, r1.MessageId, leitor));
        Assert.Null(await AbrirAsync(c, r1.SessionId, r1.MessageId, deOutraOrganizacao));
        Assert.Null(await AbrirAsync(c, r1.SessionId, r1.MessageId, semPermissao));

        // A mensagem da sessão 2 pedida pela sessão 1 (ou o contrário) não existe para o pedido.
        Assert.Null(await AbrirAsync(c, r1.SessionId, r2.MessageId, leitor));
        Assert.Null(await AbrirAsync(c, r2.SessionId, r1.MessageId, leitor));
        Assert.Null(await AbrirAsync(c, Guid.NewGuid(), r1.MessageId, leitor));
    }

    // Histórico

    [Fact]
    public async Task A_paginacao_do_historico_preserva_o_arquivo_de_cada_mensagem_com_o_estado_dele()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxpag");
        var ids = new List<Guid>();
        Guid? sessao = null;
        for (var i = 0; i < 5; i++)
        {
            var r = await IngerirAsync(c, MessageKind.Image, Midia(id: $"mid-{i}", legenda: $"legenda {i}"));
            ids.Add(r.MessageId);
            sessao = r.SessionId;
        }

        await CompletarAsync(c, ids[0], Png);
        await FalharAsync(c, ids[1], AttachmentStatus.Unavailable, "expired");
        await CompletarAsync(c, ids[2], Pdf, "image/png");

        var vistas = new List<MessageView>();
        string? cursor = null;
        var paginas = 0;
        do
        {
            var pagina = (await c.HistoricoAsync(sessao!.Value, take: 2, cursor: cursor))!;
            vistas.AddRange(pagina.Items);
            cursor = pagina.NextCursor;
            paginas++;
        }
        while (cursor is not null);

        Assert.True(paginas >= 3);
        Assert.Equal(ids, vistas.Select(m => m.Id).ToList());
        Assert.All(vistas, m => Assert.NotNull(m.Attachment));
        Assert.Equal(
            new[] { AttachmentStatus.Available, AttachmentStatus.Unavailable, AttachmentStatus.Rejected, AttachmentStatus.Pending, AttachmentStatus.Pending },
            vistas.Select(m => m.Attachment!.Status).ToArray());
        Assert.Equal(new[] { "legenda 0", "legenda 1", "legenda 2", "legenda 3", "legenda 4" }, vistas.Select(m => m.Attachment!.Caption).ToArray());

        // Do mais novo ao mais antigo, o mesmo resultado.
        var invertido = (await c.HistoricoAsync(sessao!.Value, take: 100, ordem: HistoryOrder.NewestFirst))!.Items;
        Assert.Equal(ids.AsEnumerable().Reverse().ToList(), invertido.Select(m => m.Id).ToList());
        Assert.All(invertido, m => Assert.NotNull(m.Attachment));
    }

    // Garantias do banco

    [Fact]
    public async Task O_banco_recusa_arquivo_disponivel_sem_conteudo_e_dois_arquivos_para_a_mesma_mensagem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "anxbanco");
        var r = await IngerirAsync(c, MessageKind.Image, Midia());
        var tenant = c.Tenant.TenantId.Value;

        // Disponível sem chave, tipo, tamanho e resumo.
        var semConteudo = await Assert.ThrowsAnyAsync<Exception>(() => BancoAsync(c, db => db.Database.ExecuteSqlRawAsync(
            "update conversations.message_attachments set status = 2 where tenant_id = {0} and message_id = {1}", tenant, r.MessageId)));
        Assert.Contains("ck_message_attachments_available", semConteudo.ToString(), StringComparison.Ordinal);

        // Indisponível sem o motivo.
        var semMotivo = await Assert.ThrowsAnyAsync<Exception>(() => BancoAsync(c, db => db.Database.ExecuteSqlRawAsync(
            "update conversations.message_attachments set status = 3 where tenant_id = {0} and message_id = {1}", tenant, r.MessageId)));
        Assert.Contains("ck_message_attachments_failure", semMotivo.ToString(), StringComparison.Ordinal);

        // Um segundo arquivo para a mesma mensagem.
        var duplicado = await Assert.ThrowsAnyAsync<Exception>(() => BancoAsync(c, db => db.Database.ExecuteSqlRawAsync(
            "insert into conversations.message_attachments (tenant_id, id, message_id, session_id, kind, status, provider_media_id, created_at, updated_at) " +
            "values ({0}, {1}, {2}, {3}, 3, 1, 'x', now(), now())", tenant, Guid.NewGuid(), r.MessageId, r.SessionId)));
        Assert.Contains("ux_message_attachments_tenant_id_message_id", duplicado.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_banco_recusa_arquivo_ligado_a_mensagem_de_outro_tenant()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "anxfka");
        var b = await CenarioDeConversas.CriarAsync(banco, "anxfkb");
        var r = await IngerirAsync(a, MessageKind.Text, null, texto: "Olá");

        var erro = await Assert.ThrowsAnyAsync<Exception>(() => BancoAsync(b, db => db.Database.ExecuteSqlRawAsync(
            "insert into conversations.message_attachments (tenant_id, id, message_id, session_id, kind, status, provider_media_id, created_at, updated_at) " +
            "values ({0}, {1}, {2}, {3}, 2, 1, 'x', now(), now())", b.Tenant.TenantId.Value, Guid.NewGuid(), r.MessageId, r.SessionId)));

        // 23503 é a violação de chave estrangeira (o texto da mensagem do PostgreSQL depende do idioma do servidor).
        Assert.Contains("23503", erro.ToString(), StringComparison.Ordinal);
    }
}
