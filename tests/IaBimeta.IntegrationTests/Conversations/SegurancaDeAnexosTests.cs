using System.Text;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// A segurança dos arquivos de mídia (ADR 0032) com PostgreSQL real e pastas de teste: a cota de armazenamento por empresa (recebidos e enviados, exata sob concorrência, sem vazar entre empresas), a verificação de código
/// malicioso (exigida: nunca libera sem veredito limpo; indisponível não vale como limpo), a cifra em repouso (o disco nunca tem o texto claro, chave ausente ou errada nunca entrega conteúdo) e a conferência de
/// integridade na abertura (conteúdo alterado ou sumido nunca é entregue).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SegurancaDeAnexosTests(PostgresFixture banco) : IAsyncDisposable
{
    private readonly AnexosDeTeste _t = new(banco);

    private static readonly byte[] Png = AnexosDeTeste.Png;

    public ValueTask DisposeAsync() => _t.DisposeAsync();

    private static RetentionPass Passada(CenarioDeConversas c, int dias) =>
        new(c.Tenant.TenantId.Value, Array.Empty<Guid>(), dias, DateTimeOffset.UtcNow - TimeSpan.FromDays(dias), "teste");

    // Cota

    [Fact]
    public async Task O_arquivo_recebido_que_passa_da_cota_fica_indisponivel_sem_objeto_e_o_que_coube_continua_abrindo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcota1");
        var k = _t.Criar(o => o.TenantQuotaBytes = (2 * Png.Length) + 10);
        var a = await _t.ReceberImagemAsync(k, c);
        var b = await _t.ReceberImagemAsync(k, c);
        var d = await _t.ReceberImagemAsync(k, c);

        Assert.Equal(AttachmentChangeOutcome.Changed, (await _t.CompletarAsync(k, c, a.MessageId)).Outcome);
        Assert.Equal(AttachmentChangeOutcome.Changed, (await _t.CompletarAsync(k, c, b.MessageId)).Outcome);
        var terceiro = await _t.CompletarAsync(k, c, d.MessageId);

        Assert.Equal((AttachmentChangeOutcome.Changed, AttachmentStatus.Unavailable, "quota-exceeded"), (terceiro.Outcome, terceiro.Status, terceiro.FailureCode));
        Assert.Equal(2, AnexosDeTeste.Objetos(k, c)); // o objeto do terceiro foi apagado: nada fica guardado além da cota
        Assert.Equal(Png, await _t.AbrirAsync(k, c, a.SessionId, a.MessageId));
        Assert.Equal(Png, await _t.AbrirAsync(k, c, b.SessionId, b.MessageId));
        Assert.Null(await _t.AbrirAsync(k, c, d.SessionId, d.MessageId));
        var linha = await _t.LinhaAsync(k, c, d.MessageId);
        Assert.Equal((AttachmentStatus.Unavailable, "quota-exceeded", (string?)null, (long?)null), (linha.Status, linha.FailureCode, linha.StorageKey, linha.SizeBytes));

        // Repetir a conclusão não muda nada.
        Assert.Equal(AttachmentChangeOutcome.AlreadyResolved, (await _t.CompletarAsync(k, c, d.MessageId)).Outcome);
        Assert.Equal(2, AnexosDeTeste.Objetos(k, c));
    }

    [Fact]
    public async Task Sob_concorrencia_a_cota_e_exata_cabem_exatamente_os_que_cabem_e_o_espaco_nunca_passa_do_limite()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcota2");
        var k = _t.Criar(o => o.TenantQuotaBytes = 5L * Png.Length);
        var mensagens = new List<IngestInboundMessageResult>();
        for (var i = 0; i < 12; i++)
        {
            mensagens.Add(await _t.ReceberImagemAsync(k, c));
        }

        var resultados = await Task.WhenAll(mensagens.Select(m => _t.CompletarAsync(k, c, m.MessageId)));

        Assert.Equal(5, resultados.Count(r => r.Status == AttachmentStatus.Available));
        Assert.Equal(7, resultados.Count(r => r is { Status: AttachmentStatus.Unavailable, FailureCode: "quota-exceeded" }));
        var linhas = await _t.LinhasAsync(k, c);
        Assert.Equal(5L * Png.Length, linhas.Where(l => l.Status == AttachmentStatus.Available).Sum(l => l.SizeBytes ?? 0));
        Assert.Equal(5, AnexosDeTeste.Objetos(k, c));
    }

    [Fact]
    public async Task A_mesma_conclusao_repetida_ao_mesmo_tempo_ocupa_o_espaco_uma_unica_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcota3");
        var k = _t.Criar(o => o.TenantQuotaBytes = 2L * Png.Length);
        var a = await _t.ReceberImagemAsync(k, c);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _t.CompletarAsync(k, c, a.MessageId)));

        Assert.Equal(1, resultados.Count(r => r.Outcome == AttachmentChangeOutcome.Changed));
        Assert.Equal(5, resultados.Count(r => r.Outcome == AttachmentChangeOutcome.AlreadyResolved));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c));
        Assert.Equal(Png.Length, (await _t.LinhaAsync(k, c, a.MessageId)).SizeBytes);

        // O que sobrou da cota ainda cabe: um segundo arquivo entra.
        var b = await _t.ReceberImagemAsync(k, c);
        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, c, b.MessageId)).Status);
    }

    [Fact]
    public async Task A_cota_e_por_empresa_uma_empresa_cheia_nao_impede_a_outra()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "segcota4a");
        var b = await CenarioDeConversas.CriarAsync(banco, "segcota4b");
        var k = _t.Criar(o => o.TenantQuotaBytes = Png.Length);
        var a1 = await _t.ReceberImagemAsync(k, a);
        var a2 = await _t.ReceberImagemAsync(k, a);
        var b1 = await _t.ReceberImagemAsync(k, b);

        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, a, a1.MessageId)).Status);
        Assert.Equal("quota-exceeded", (await _t.CompletarAsync(k, a, a2.MessageId)).FailureCode);
        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, b, b1.MessageId)).Status); // a empresa B está vazia

        Assert.Equal(1, AnexosDeTeste.Objetos(k, a));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, b));
    }

    [Fact]
    public async Task Com_a_cota_desligada_nada_e_limitado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcota5");
        var k = _t.Criar(o => o.TenantQuotaBytes = 0);

        for (var i = 0; i < 6; i++)
        {
            var m = await _t.ReceberImagemAsync(k, c);
            Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, c, m.MessageId)).Status);
        }

        Assert.Equal(6, AnexosDeTeste.Objetos(k, c));
    }

    [Fact]
    public async Task O_arquivo_enviado_conta_na_mesma_cota_do_recebido_e_o_que_nao_cabe_e_recusado_sem_gravar_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcota6");
        var k = _t.Criar(o => o.TenantQuotaBytes = Png.Length + 10);
        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);
        var recebido = await _t.ReceberImagemAsync(k, c);
        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, c, recebido.MessageId)).Status); // ocupa 24 de 34 bytes

        var recusado = await _t.EnviarArquivoAsync(k, c, sessao, autor);

        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "quota-exceeded"), (recusado.Outcome, recusado.RejectionCode));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c)); // só o recebido: nada do envio recusado ficou guardado
        Assert.Single(await _t.LinhasAsync(k, c)); // nem linha de arquivo, nem mensagem

        // Com uma cota maior (mesma pasta e mesmo banco) o mesmo envio é aceito.
        var maior = _t.Criar(o => o.TenantQuotaBytes = 4L * Png.Length, pasta: k.Pasta);
        Assert.Equal(RegisterHumanReplyOutcome.Applied, (await _t.EnviarArquivoAsync(maior, c, sessao, autor)).Outcome);
        Assert.Equal(2, AnexosDeTeste.Objetos(maior, c));
    }

    [Fact]
    public async Task Liberar_espaco_pelo_prazo_de_guarda_devolve_a_cota_e_o_proximo_arquivo_entra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcota7");
        var k = _t.Criar(o => o.TenantQuotaBytes = 2L * Png.Length);
        var a = await _t.ReceberImagemAsync(k, c);
        var b = await _t.ReceberImagemAsync(k, c);
        var d = await _t.ReceberImagemAsync(k, c);
        await _t.CompletarAsync(k, c, a.MessageId);
        await _t.CompletarAsync(k, c, b.MessageId);
        Assert.Equal("quota-exceeded", (await _t.CompletarAsync(k, c, d.MessageId)).FailureCode);

        await _t.EnvelhecerAsync(k, c, a.MessageId, 91);
        var resultado = await _t.ComoAsync(k, c, sp => AttachmentObjectsRetentionTask
            .ForInbound(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IAttachmentContentStore>(), TimeProvider.System)
            .RunAsync(Passada(c, 90), dryRun: false));
        Assert.Equal(1, resultado.Deleted);

        var proximo = await _t.ReceberImagemAsync(k, c);
        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, c, proximo.MessageId)).Status);
    }

    // Verificação de código malicioso

    [Fact]
    public async Task Com_a_verificacao_exigida_o_verificador_fora_do_ar_nao_libera_o_arquivo_que_fica_pendente_e_a_nova_tentativa_conclui()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segscan1");
        var motor = new VerificadorDeTeste { Resposta = ScanVerdict.Unavailable };
        var k = _t.CriarComVerificacaoExigida(motor);
        var m = await _t.ReceberImagemAsync(k, c);

        var fora = await _t.CompletarAsync(k, c, m.MessageId);

        Assert.Equal((AttachmentChangeOutcome.ScanUnavailable, AttachmentStatus.Pending), (fora.Outcome, fora.Status));
        Assert.Equal(0, AnexosDeTeste.Objetos(k, c)); // nada foi guardado
        var pendente = await _t.LinhaAsync(k, c, m.MessageId);
        Assert.Equal((AttachmentStatus.Pending, AttachmentScanStatus.NotScanned, (string?)null), (pendente.Status, pendente.ScanStatus, pendente.StorageKey));
        Assert.Null(await _t.AbrirAsync(k, c, m.SessionId, m.MessageId));

        motor.Resposta = ScanVerdict.Clean;
        var dentro = await _t.CompletarAsync(k, c, m.MessageId);

        Assert.Equal((AttachmentChangeOutcome.Changed, AttachmentStatus.Available), (dentro.Outcome, dentro.Status));
        var pronta = await _t.LinhaAsync(k, c, m.MessageId);
        Assert.Equal((AttachmentScanStatus.Clean, true), (pronta.ScanStatus, pronta.ScannedAt is not null));
        Assert.Equal(Png, await _t.AbrirAsync(k, c, m.SessionId, m.MessageId));
        Assert.Equal(2, motor.Chamadas);
    }

    [Fact]
    public async Task O_arquivo_infectado_e_recusado_como_malware_e_nada_e_guardado_com_ou_sem_a_verificacao_exigida()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segscan2");
        var motor = new VerificadorDeTeste { Resposta = ScanVerdict.Infected };
        var k = _t.CriarComVerificacaoExigida(motor);
        var opcional = _t.CriarComVerificadorOpcional(motor, pasta: k.Pasta);
        var a = await _t.ReceberImagemAsync(k, c);
        var b = await _t.ReceberImagemAsync(opcional, c);

        var exigida = await _t.CompletarAsync(k, c, a.MessageId);
        var naoExigida = await _t.CompletarAsync(opcional, c, b.MessageId);

        Assert.Equal((AttachmentChangeOutcome.Rejected, AttachmentStatus.Rejected, "malware"), (exigida.Outcome, exigida.Status, exigida.FailureCode));
        Assert.Equal((AttachmentChangeOutcome.Rejected, AttachmentStatus.Rejected, "malware"), (naoExigida.Outcome, naoExigida.Status, naoExigida.FailureCode));
        Assert.Equal(0, AnexosDeTeste.Objetos(k, c));
        Assert.Null(await _t.AbrirAsync(k, c, a.SessionId, a.MessageId));
    }

    [Fact]
    public async Task A_amostra_EICAR_e_bloqueada_mesmo_sem_nenhum_verificador_configurado_no_recebimento_e_no_envio()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segscan3");
        var k = _t.Criar();
        var eicar = EicarTestSample.Bytes();
        var recebido = await _t.IngerirAsync(k, c, MessageKind.Document, AnexosDeTeste.Midia("text/plain", "teste.txt"));

        var resultado = await _t.CompletarAsync(k, c, recebido.MessageId, eicar, "text/plain");

        Assert.Equal((AttachmentChangeOutcome.Rejected, "malware"), (resultado.Outcome, resultado.FailureCode));
        Assert.Equal(0, AnexosDeTeste.Objetos(k, c));

        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);
        var enviado = await _t.EnviarArquivoAsync(k, c, sessao, autor, eicar, "teste.txt", "text/plain", legenda: null);
        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "malware"), (enviado.Outcome, enviado.RejectionCode));
        Assert.Equal(0, AnexosDeTeste.Objetos(k, c));
    }

    [Fact]
    public async Task Sem_a_verificacao_exigida_o_verificador_fora_do_ar_deixa_o_arquivo_passar_como_nao_verificado_e_sem_verificador_tambem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segscan4");
        var motor = new VerificadorDeTeste { Resposta = ScanVerdict.Unavailable };
        var comMotor = _t.CriarComVerificadorOpcional(motor);
        var semMotor = _t.Criar(pasta: comMotor.Pasta);
        var a = await _t.ReceberImagemAsync(comMotor, c);
        var b = await _t.ReceberImagemAsync(semMotor, c);

        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(comMotor, c, a.MessageId)).Status);
        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(semMotor, c, b.MessageId)).Status);

        Assert.Equal(AttachmentScanStatus.NotScanned, (await _t.LinhaAsync(comMotor, c, a.MessageId)).ScanStatus);
        Assert.Equal(AttachmentScanStatus.NotScanned, (await _t.LinhaAsync(semMotor, c, b.MessageId)).ScanStatus);
        Assert.Equal(Png, await _t.AbrirAsync(comMotor, c, a.SessionId, a.MessageId)); // não exigida: abre
    }

    [Fact]
    public async Task O_arquivo_enviado_com_a_verificacao_exigida_so_sai_se_o_verificador_o_declarou_limpo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segscan5");
        var motor = new VerificadorDeTeste { Resposta = ScanVerdict.Unavailable };
        var k = _t.CriarComVerificacaoExigida(motor);
        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);

        var fora = await _t.EnviarArquivoAsync(k, c, sessao, autor);
        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "scanner-unavailable"), (fora.Outcome, fora.RejectionCode));
        Assert.Equal(0, AnexosDeTeste.Objetos(k, c));

        motor.Resposta = ScanVerdict.Infected;
        var infectado = await _t.EnviarArquivoAsync(k, c, sessao, autor);
        Assert.Equal((RegisterHumanReplyOutcome.ContentRejected, "malware"), (infectado.Outcome, infectado.RejectionCode));

        motor.Resposta = ScanVerdict.Clean;
        var limpo = await _t.EnviarArquivoAsync(k, c, sessao, autor);
        Assert.Equal(RegisterHumanReplyOutcome.Applied, limpo.Outcome);
        Assert.Equal(AttachmentScanStatus.Clean, (await _t.LinhaAsync(k, c, limpo.MessageId!.Value)).ScanStatus);
        Assert.Equal(Png, await _t.LerParaOSenderAsync(k, c, limpo.MessageId.Value));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c));
    }

    [Fact]
    public async Task Com_a_verificacao_exigida_o_arquivo_guardado_sem_verificacao_nao_abre_nem_vai_ao_Sender_ate_a_varredura_posterior()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segscan6");
        var antes = _t.Criar(); // como era antes de o verificador existir: o arquivo é guardado "não verificado"
        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);
        var recebido = await _t.ReceberImagemAsync(antes, c);
        await _t.CompletarAsync(antes, c, recebido.MessageId);
        var enviado = (await _t.EnviarArquivoAsync(antes, c, sessao, autor)).MessageId!.Value;
        Assert.Equal(Png, await _t.AbrirAsync(antes, c, recebido.SessionId, recebido.MessageId));
        Assert.Equal(Png, await _t.LerParaOSenderAsync(antes, c, enviado));

        var motor = new VerificadorDeTeste { Resposta = ScanVerdict.Clean };
        var agora = _t.CriarComVerificacaoExigida(motor, pasta: antes.Pasta);
        Assert.Null(await _t.AbrirAsync(agora, c, recebido.SessionId, recebido.MessageId));
        Assert.Null(await _t.LerParaOSenderAsync(agora, c, enviado));

        var relatorio = await _t.Manutencao(agora).RescanAsync(dryRun: false, c.Tenant.TenantId.Value);

        Assert.Equal((2, 2, 0), (relatorio.Candidates, relatorio.MarkedClean, relatorio.Infected));
        Assert.Equal(Png, await _t.AbrirAsync(agora, c, recebido.SessionId, recebido.MessageId));
        Assert.Equal(Png, await _t.LerParaOSenderAsync(agora, c, enviado));
    }

    // Cifra em repouso e integridade

    private static ComposicaoDeAnexos ComCifra(AnexosDeTeste t, string chave, string id = "k1", string? pasta = null) =>
        t.Criar(
            o =>
            {
                o.Encryption.ActiveKeyId = id;
                o.Encryption.Keys[id] = chave;
                o.Encryption.Required = true;
            },
            pasta: pasta);

    [Fact]
    public async Task Com_a_cifra_ligada_o_disco_nunca_tem_o_texto_claro_e_o_arquivo_abre_com_os_mesmos_bytes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcifra1");
        var k = ComCifra(_t, AnexosDeTeste.NovaChave());
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% segredo-do-laudo-123\n1 0 obj\n<<>>\nendobj\ntrailer\n%%EOF\n");
        var m = await _t.IngerirAsync(k, c, MessageKind.Document, AnexosDeTeste.Midia("application/pdf", "laudo.pdf"));

        var resultado = await _t.CompletarAsync(k, c, m.MessageId, pdf, "application/pdf");

        Assert.Equal(AttachmentStatus.Available, resultado.Status);
        var linha = await _t.LinhaAsync(k, c, m.MessageId);
        var noDisco = await File.ReadAllBytesAsync(AnexosDeTeste.CaminhoDe(k, linha.StorageKey!));
        Assert.True(EncryptedAttachmentContentStore.HasEncryptedHeader(noDisco));
        Assert.Equal(-1, noDisco.AsSpan().IndexOf("segredo-do-laudo-123"u8));
        Assert.Equal(-1, noDisco.AsSpan().IndexOf("%PDF"u8));
        Assert.Equal(pdf, await _t.AbrirAsync(k, c, m.SessionId, m.MessageId));
        Assert.Equal((long)pdf.Length, linha.SizeBytes); // o tamanho e o resumo gravados são os do CONTEÚDO, não os do objeto cifrado
    }

    [Fact]
    public async Task O_arquivo_enviado_tambem_e_cifrado_e_o_Sender_o_le_decifrado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcifra2");
        var k = ComCifra(_t, AnexosDeTeste.NovaChave());
        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);

        var enviado = await _t.EnviarArquivoAsync(k, c, sessao, autor);

        Assert.Equal(RegisterHumanReplyOutcome.Applied, enviado.Outcome);
        var linha = await _t.LinhaAsync(k, c, enviado.MessageId!.Value);
        Assert.True(EncryptedAttachmentContentStore.HasEncryptedHeader(await File.ReadAllBytesAsync(AnexosDeTeste.CaminhoDe(k, linha.StorageKey!))));
        Assert.Equal(Png, await _t.LerParaOSenderAsync(k, c, enviado.MessageId.Value));
    }

    [Fact]
    public async Task Objeto_cifrado_adulterado_ou_com_chave_errada_ou_ausente_nunca_abre_nem_vai_ao_Sender_e_nao_lanca()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segcifra3");
        var chave = AnexosDeTeste.NovaChave();
        var k = ComCifra(_t, chave);
        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);
        var m = await _t.ReceberImagemAsync(k, c);
        await _t.CompletarAsync(k, c, m.MessageId);
        var enviado = (await _t.EnviarArquivoAsync(k, c, sessao, autor)).MessageId!.Value;
        Assert.Equal(Png, await _t.AbrirAsync(k, c, m.SessionId, m.MessageId));

        // Chave errada (o mesmo identificador com outro material) e chave ausente (a cifra nem está configurada neste ambiente).
        var errada = ComCifra(_t, AnexosDeTeste.NovaChave(), pasta: k.Pasta);
        var ausente = _t.Criar(pasta: k.Pasta);
        foreach (var ambiente in new[] { errada, ausente })
        {
            Assert.Null(await _t.AbrirAsync(ambiente, c, m.SessionId, m.MessageId));
            Assert.Null(await _t.LerParaOSenderAsync(ambiente, c, enviado));
        }

        // Adulterar um byte do texto cifrado.
        var linha = await _t.LinhaAsync(k, c, m.MessageId);
        var caminho = AnexosDeTeste.CaminhoDe(k, linha.StorageKey!);
        var bytes = await File.ReadAllBytesAsync(caminho);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(caminho, bytes);
        Assert.Null(await _t.AbrirAsync(k, c, m.SessionId, m.MessageId));
        Assert.Equal(Png, await _t.LerParaOSenderAsync(k, c, enviado)); // o outro arquivo continua íntegro
    }

    [Fact]
    public async Task Sem_a_cifra_conteudo_alterado_com_o_mesmo_tamanho_ou_objeto_sumido_nunca_e_entregue()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "segintegridade");
        var k = _t.Criar();
        var a = await _t.ReceberImagemAsync(k, c);
        var b = await _t.ReceberImagemAsync(k, c);
        await _t.CompletarAsync(k, c, a.MessageId);
        await _t.CompletarAsync(k, c, b.MessageId);
        var linhaA = await _t.LinhaAsync(k, c, a.MessageId);
        var linhaB = await _t.LinhaAsync(k, c, b.MessageId);

        var alterado = (byte[])Png.Clone();
        alterado[^1] ^= 0xFF; // o mesmo tamanho, outro conteúdo
        await File.WriteAllBytesAsync(AnexosDeTeste.CaminhoDe(k, linhaA.StorageKey!), alterado);
        File.Delete(AnexosDeTeste.CaminhoDe(k, linhaB.StorageKey!));

        Assert.Null(await _t.AbrirAsync(k, c, a.SessionId, a.MessageId));
        Assert.Null(await _t.AbrirAsync(k, c, b.SessionId, b.MessageId));
    }
}
