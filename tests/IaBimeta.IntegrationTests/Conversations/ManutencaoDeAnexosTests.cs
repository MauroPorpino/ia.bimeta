using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// O prazo de guarda dos arquivos (recebidos e enviados) e a manutenção do armazenamento (ADR 0032), com PostgreSQL real e pastas de teste: expurgo idempotente, auditado ANTES de apagar, que preserva a mensagem e
/// o nome e nunca toca outra empresa; prazo zero que nunca apaga; conferência banco x pasta com reparo; varredura posterior; conversão para a cifra; uso por empresa.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ManutencaoDeAnexosTests(PostgresFixture banco) : IAsyncDisposable
{
    private readonly AnexosDeTeste _t = new(banco);

    private static readonly byte[] Png = AnexosDeTeste.Png;

    public ValueTask DisposeAsync() => _t.DisposeAsync();

    private static RetentionPass Passada(CenarioDeConversas c, int dias) =>
        new(c.Tenant.TenantId.Value, Array.Empty<Guid>(), dias, DateTimeOffset.UtcNow - TimeSpan.FromDays(dias), "teste");

    private async Task<IngestInboundMessageResult> RecebidoDisponivelAsync(ComposicaoDeAnexos k, CenarioDeConversas c)
    {
        var m = await _t.ReceberImagemAsync(k, c);
        Assert.Equal(AttachmentStatus.Available, (await _t.CompletarAsync(k, c, m.MessageId)).Status);
        return m;
    }

    // Prazo de guarda

    [Fact]
    public async Task O_prazo_dos_recebidos_zero_nunca_apaga_nada_so_informa()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manprazo0");
        var k = _t.Criar();
        var m = await RecebidoDisponivelAsync(k, c);
        await _t.EnvelhecerAsync(k, c, m.MessageId, 4000);
        var auditoria = new AuditoriaDeExpurgoDeTeste();

        var resultado = await _t.Expurgo(k, MessageDirection.Inbound, auditoria).RunAsync(Passada(c, 0), dryRun: false);

        Assert.NotNull(resultado.Skipped);
        Assert.Equal((0, 0), (resultado.Eligible, resultado.Deleted));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c));
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, m.MessageId)).Status);
        Assert.Empty(auditoria.Itens);
    }

    [Fact]
    public async Task Passado_o_prazo_o_objeto_dos_recebidos_e_apagado_a_mensagem_fica_e_a_auditoria_registra_so_identificadores()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manprazo1");
        var k = _t.Criar();
        var velho = await RecebidoDisponivelAsync(k, c);
        var novo = await RecebidoDisponivelAsync(k, c);
        await _t.EnvelhecerAsync(k, c, velho.MessageId, 91);
        var anexoVelho = (await _t.LinhaAsync(k, c, velho.MessageId)).Id;
        var auditoria = new AuditoriaDeExpurgoDeTeste();
        var tarefa = _t.Expurgo(k, MessageDirection.Inbound, auditoria);

        // Simulação: conta e não muda nada.
        var simulacao = await tarefa.RunAsync(Passada(c, 90), dryRun: true);
        Assert.Equal((1, 0), (simulacao.Eligible, simulacao.Deleted));
        Assert.Equal(2, AnexosDeTeste.Objetos(k, c));
        Assert.Empty(auditoria.Itens);

        var resultado = await tarefa.RunAsync(Passada(c, 90), dryRun: false);

        Assert.Equal((1, 1), (resultado.Eligible, resultado.Deleted));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c));
        var linha = await _t.LinhaAsync(k, c, velho.MessageId);
        Assert.Equal((AttachmentStatus.Unavailable, "retention", (string?)null, (string?)null), (linha.Status, linha.FailureCode, linha.StorageKey, linha.Sha256));
        Assert.Null(await _t.AbrirAsync(k, c, velho.SessionId, velho.MessageId));
        Assert.Equal(Png, await _t.AbrirAsync(k, c, novo.SessionId, novo.MessageId)); // o mais novo segue intacto
        var registro = Assert.Single(auditoria.Itens);
        Assert.Equal((c.Tenant.TenantId.Value, AttachmentObjectsRetentionTask.AuditOperation, "attachment", anexoVelho.ToString()), (registro.TenantId, registro.Operation, registro.ResourceType, registro.ResourceId));

        // A mensagem continua no histórico, com o arquivo indisponível pelo prazo.
        var historico = (await c.HistoricoAsync(velho.SessionId))!.Items.Single(m => m.Id == velho.MessageId);
        Assert.Equal((AttachmentStatus.Unavailable, "retention"), (historico.Attachment!.Status, historico.Attachment.FailureCode));

        // Repetir é seguro: nada mais a fazer e nada novo na trilha.
        var repetido = await tarefa.RunAsync(Passada(c, 90), dryRun: false);
        Assert.Equal((0, 0), (repetido.Eligible, repetido.Deleted));
        Assert.Single(auditoria.Itens);
    }

    [Fact]
    public async Task O_expurgo_de_uma_empresa_nunca_toca_o_arquivo_de_outra_e_cada_sentido_so_apaga_o_seu()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "manprazo2a");
        var b = await CenarioDeConversas.CriarAsync(banco, "manprazo2b");
        var k = _t.Criar();
        var recebidoA = await RecebidoDisponivelAsync(k, a);
        var recebidoB = await RecebidoDisponivelAsync(k, b);
        var (sessaoA, autorA) = await AnexosDeTeste.HumanoAsync(a);
        var enviadoA = (await _t.EnviarArquivoAsync(k, a, sessaoA, autorA)).MessageId!.Value;
        await _t.EnvelhecerAsync(k, a, recebidoA.MessageId, 91);
        await _t.EnvelhecerAsync(k, b, recebidoB.MessageId, 91);
        await _t.EnvelhecerAsync(k, a, enviadoA, 91);

        // A passada da empresa A para os RECEBIDOS: só o recebido de A.
        await _t.Expurgo(k, MessageDirection.Inbound).RunAsync(Passada(a, 90), dryRun: false);
        Assert.Equal(AttachmentStatus.Unavailable, (await _t.LinhaAsync(k, a, recebidoA.MessageId)).Status);
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, b, recebidoB.MessageId)).Status); // outra empresa: intacta
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, a, enviadoA)).Status); // outro sentido: intacto

        // A passada de A para os ENVIADOS: agora o enviado de A, e o recebido de B segue intacto.
        await _t.Expurgo(k, MessageDirection.Outbound).RunAsync(Passada(a, 30), dryRun: false);
        Assert.Equal(AttachmentStatus.Unavailable, (await _t.LinhaAsync(k, a, enviadoA)).Status);
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, b, recebidoB.MessageId)).Status);
        Assert.Equal(1, AnexosDeTeste.Objetos(k, b));
        Assert.Equal(0, AnexosDeTeste.Objetos(k, a));
    }

    [Fact]
    public async Task Se_a_trilha_de_auditoria_falha_nada_e_apagado_e_a_proxima_rodada_apaga_e_registra()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manprazo3");
        var k = _t.Criar();
        var m = await RecebidoDisponivelAsync(k, c);
        await _t.EnvelhecerAsync(k, c, m.MessageId, 91);
        var auditoria = new AuditoriaDeExpurgoDeTeste { Falhar = true };
        var tarefa = _t.Expurgo(k, MessageDirection.Inbound, auditoria);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tarefa.RunAsync(Passada(c, 90), dryRun: false));

        // Fecha em falha: nenhum objeto apagado, a linha continua disponível, nada registrado.
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c));
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, m.MessageId)).Status);
        Assert.Empty(auditoria.Itens);

        auditoria.Falhar = false;
        var resultado = await tarefa.RunAsync(Passada(c, 90), dryRun: false);
        Assert.Equal((1, 1), (resultado.Eligible, resultado.Deleted));
        Assert.Single(auditoria.Itens);
        Assert.Equal(0, AnexosDeTeste.Objetos(k, c));
    }

    [Fact]
    public async Task O_objeto_orfao_antigo_e_removido_com_auditoria_e_o_recente_nunca_e_tocado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manorfao");
        var k = _t.Criar();
        var armazenamento = k.Provedor.GetRequiredService<IAttachmentContentStore>();
        var antigoId = Guid.NewGuid();
        var recenteId = Guid.NewGuid();
        var antigo = await armazenamento.WriteAsync(c.Tenant.TenantId, antigoId, Png, default);
        await armazenamento.WriteAsync(c.Tenant.TenantId, recenteId, Png, default);
        File.SetLastWriteTimeUtc(AnexosDeTeste.CaminhoDe(k, antigo), DateTime.UtcNow.AddDays(-45));
        var auditoria = new AuditoriaDeExpurgoDeTeste();

        var resultado = await _t.Expurgo(k, MessageDirection.Outbound, auditoria).RunAsync(Passada(c, 30), dryRun: false);

        Assert.Equal((1, 1), (resultado.Eligible, resultado.Deleted));
        Assert.Equal(1, AnexosDeTeste.Objetos(k, c));
        var registro = Assert.Single(auditoria.Itens);
        Assert.Equal((AttachmentObjectsRetentionTask.OrphanAuditOperation, antigoId.ToString()), (registro.Operation, registro.ResourceId));
    }

    // Conferência banco x pasta

    [Fact]
    public async Task A_conferencia_acha_o_objeto_sumido_o_alterado_e_o_orfao_e_so_o_reparo_marca_o_sumido_como_perdido_com_auditoria()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mancheck1");
        var outra = await CenarioDeConversas.CriarAsync(banco, "mancheck1b");
        var k = _t.Criar();
        var integro = await RecebidoDisponivelAsync(k, c);
        var sumido = await RecebidoDisponivelAsync(k, c);
        var alterado = await RecebidoDisponivelAsync(k, c);
        var deOutra = await RecebidoDisponivelAsync(k, outra);
        File.Delete(AnexosDeTeste.CaminhoDe(k, (await _t.LinhaAsync(k, c, sumido.MessageId)).StorageKey!));
        var adulterado = (byte[])Png.Clone();
        adulterado[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(AnexosDeTeste.CaminhoDe(k, (await _t.LinhaAsync(k, c, alterado.MessageId)).StorageKey!), adulterado);
        var orfaoId = Guid.NewGuid();
        var orfao = await k.Provedor.GetRequiredService<IAttachmentContentStore>().WriteAsync(c.Tenant.TenantId, orfaoId, Png, default);
        File.SetLastWriteTimeUtc(AnexosDeTeste.CaminhoDe(k, orfao), DateTime.UtcNow.AddDays(-3));
        var auditoria = new AuditoriaDeExpurgoDeTeste();
        var servico = _t.Manutencao(k, auditoria);
        var empresa = c.Tenant.TenantId.Value;

        var leitura = await servico.CheckAsync(repair: false, TimeSpan.FromDays(1), empresa);

        Assert.Equal((3, 1, 1, 1, 1, 0), (leitura.Checked, leitura.Ok, leitura.MissingCount, leitura.MismatchCount, leitura.OrphanCount, leitura.UnreadableCount));
        Assert.True(leitura.HasProblems);
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, sumido.MessageId)).Status); // sem --repair nada muda
        Assert.Equal(3, AnexosDeTeste.Objetos(k, c)); // o íntegro, o alterado e o órfão (o objeto do sumido não existe mais)
        Assert.Empty(auditoria.Itens);

        var reparo = await servico.CheckAsync(repair: true, TimeSpan.FromDays(1), empresa);

        Assert.Equal((1, 1), (reparo.RepairedMissing, reparo.RemovedOrphans));
        var perdida = await _t.LinhaAsync(k, c, sumido.MessageId);
        Assert.Equal((AttachmentStatus.Unavailable, MessageAttachment.LostFailureCode, (string?)null), (perdida.Status, perdida.FailureCode, perdida.StorageKey));
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, alterado.MessageId)).Status); // o que não confere NUNCA é alterado
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, integro.MessageId)).Status);
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, outra, deOutra.MessageId)).Status); // outra empresa: fora do alcance
        Assert.Equal(2, AnexosDeTeste.Objetos(k, c)); // o órfão antigo foi apagado
        Assert.Contains(auditoria.Itens, i => i.Operation == "conversations.attachment.lost" && i.ResourceId == perdida.Id.ToString());
        Assert.Contains(auditoria.Itens, i => i.Operation == AttachmentObjectsRetentionTask.OrphanAuditOperation && i.ResourceId == orfaoId.ToString());

        // Repetir o reparo não acha mais o que já foi tratado.
        var depois = await servico.CheckAsync(repair: true, TimeSpan.FromDays(1), empresa);
        Assert.Equal((0, 0, 1), (depois.RepairedMissing, depois.RemovedOrphans, depois.MismatchCount));
    }

    [Fact]
    public async Task O_reparo_recusa_marcar_tudo_como_perdido_quando_a_pasta_nao_tem_nenhum_objeto_e_so_roda_com_a_permissao_explicita()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mancheck4");
        var k = _t.Criar();
        var a = await RecebidoDisponivelAsync(k, c);
        var b = await RecebidoDisponivelAsync(k, c);
        var empresa = c.Tenant.TenantId.Value;

        // O caminho da pasta está errado (ou a pasta ainda não foi restaurada): outra pasta, vazia, com o MESMO banco.
        var pastaErrada = _t.Criar(pasta: Path.Combine(banco.PastaDeAnexos, "seg-pasta-errada-" + Guid.NewGuid().ToString("N")));
        var servico = _t.Manutencao(pastaErrada);

        // Só ler nunca altera e mostra o quadro.
        var leitura = await servico.CheckAsync(repair: false, TimeSpan.FromDays(1), empresa);
        Assert.Equal((2, 2, 0), (leitura.Checked, leitura.MissingCount, leitura.RepairedMissing));

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.CheckAsync(repair: true, TimeSpan.FromDays(1), empresa));
        Assert.Contains("RootPath", erro.Message, StringComparison.Ordinal);
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, a.MessageId)).Status); // nada foi alterado
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, b.MessageId)).Status);
        Assert.NotNull((await _t.LinhaAsync(k, c, a.MessageId)).StorageKey); // a chave que permite reencontrar o arquivo continua no banco

        // Só com a permissão explícita (a perda é real) ele marca.
        var reparo = await servico.CheckAsync(repair: true, TimeSpan.FromDays(1), empresa, allowEmptyFolder: true);
        Assert.Equal(2, reparo.RepairedMissing);

        // Uma pasta que tem objetos da empresa (só um sumiu) NÃO aciona a trava: é o caso comum de um arquivo perdido.
        var c2 = await CenarioDeConversas.CriarAsync(banco, "mancheck4b");
        var k2 = _t.Criar();
        var ficou = await RecebidoDisponivelAsync(k2, c2);
        var sumiu = await RecebidoDisponivelAsync(k2, c2);
        File.Delete(AnexosDeTeste.CaminhoDe(k2, (await _t.LinhaAsync(k2, c2, sumiu.MessageId)).StorageKey!));
        var normal = await _t.Manutencao(k2).CheckAsync(repair: true, TimeSpan.FromDays(1), c2.Tenant.TenantId.Value);
        Assert.Equal((1, 1), (normal.RepairedMissing, normal.Ok));
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k2, c2, ficou.MessageId)).Status);
    }

    [Fact]
    public async Task O_orfao_mais_novo_que_o_prazo_minimo_nunca_e_apagado_pelo_reparo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mancheck2");
        var k = _t.Criar();
        var recente = await k.Provedor.GetRequiredService<IAttachmentContentStore>().WriteAsync(c.Tenant.TenantId, Guid.NewGuid(), Png, default); // pode ser uma gravação em andamento

        var relatorio = await _t.Manutencao(k).CheckAsync(repair: true, TimeSpan.FromDays(1), c.Tenant.TenantId.Value);

        Assert.Equal((1, 0), (relatorio.OrphanCount, relatorio.RemovedOrphans));
        Assert.True(File.Exists(AnexosDeTeste.CaminhoDe(k, recente)));
    }

    [Fact]
    public async Task Objeto_cifrado_que_nao_decifra_e_relatado_como_ilegivel_e_nunca_alterado_pelo_reparo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "mancheck3");
        var chave = AnexosDeTeste.NovaChave();
        var k = _t.Criar(o => { o.Encryption.ActiveKeyId = "k1"; o.Encryption.Keys["k1"] = chave; });
        var m = await RecebidoDisponivelAsync(k, c);
        var errada = _t.Criar(o => { o.Encryption.ActiveKeyId = "k1"; o.Encryption.Keys["k1"] = AnexosDeTeste.NovaChave(); }, pasta: k.Pasta);

        var relatorio = await _t.Manutencao(errada).CheckAsync(repair: true, TimeSpan.Zero, c.Tenant.TenantId.Value);

        Assert.Equal((1, 1, 0, 0), (relatorio.Checked, relatorio.UnreadableCount, relatorio.MissingCount, relatorio.RepairedMissing));
        Assert.Equal(AttachmentStatus.Available, (await _t.LinhaAsync(k, c, m.MessageId)).Status);
        Assert.Equal(Png, await _t.AbrirAsync(k, c, m.SessionId, m.MessageId)); // com a chave certa o arquivo continua abrindo
    }

    // Varredura posterior

    [Fact]
    public async Task A_varredura_posterior_marca_o_limpo_bloqueia_e_apaga_o_infectado_e_deixa_como_esta_se_o_verificador_estiver_fora()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manrescan");
        var antes = _t.Criar();
        var limpo = await RecebidoDisponivelAsync(antes, c);
        var infectado = await RecebidoDisponivelAsync(antes, c);
        var linhaInfectada = await _t.LinhaAsync(antes, c, infectado.MessageId);
        await File.WriteAllBytesAsync(AnexosDeTeste.CaminhoDe(antes, linhaInfectada.StorageKey!), Png.Concat(VerificadorDeTeste.MarcaInfectada).ToArray()); // o conteúdo guardado, hoje, é o que o verificador reconhece como infectado
        var motor = new VerificadorDeTeste { Resposta = ScanVerdict.Clean };
        var depois = _t.CriarComVerificacaoExigida(motor, pasta: antes.Pasta);
        var auditoria = new AuditoriaDeExpurgoDeTeste();
        var servico = _t.Manutencao(depois, auditoria);
        var empresa = c.Tenant.TenantId.Value;

        // Verificador fora do ar: o limpo fica como está (indisponível); o que o verificador reconhece como infectado é bloqueado. (O motor de teste decide pela marca; a EICAR nunca é gravada em disco.)
        motor.Resposta = ScanVerdict.Unavailable;
        var fora = await servico.RescanAsync(dryRun: false, empresa);
        Assert.Equal((2, 0, 1, 1), (fora.Candidates, fora.MarkedClean, fora.Infected, fora.Unavailable));
        Assert.Equal(AttachmentScanStatus.NotScanned, (await _t.LinhaAsync(depois, c, limpo.MessageId)).ScanStatus);

        // Verificador de volta: o limpo é marcado.
        motor.Resposta = ScanVerdict.Clean;
        var dentro = await servico.RescanAsync(dryRun: false, empresa);
        Assert.Equal((1, 1, 0), (dentro.Candidates, dentro.MarkedClean, dentro.Infected));
        Assert.Equal(AttachmentScanStatus.Clean, (await _t.LinhaAsync(depois, c, limpo.MessageId)).ScanStatus);
        Assert.Equal(Png, await _t.AbrirAsync(depois, c, limpo.SessionId, limpo.MessageId));

        // O infectado foi bloqueado: linha recusada por malware, objeto apagado, trilha registrada.
        var bloqueada = await _t.LinhaAsync(depois, c, infectado.MessageId);
        Assert.Equal((AttachmentStatus.Rejected, "malware", (string?)null), (bloqueada.Status, bloqueada.FailureCode, bloqueada.StorageKey));
        Assert.Equal(1, AnexosDeTeste.Objetos(depois, c));
        Assert.Contains(auditoria.Itens, i => i.Operation == "conversations.attachment.malware-blocked" && i.ResourceId == bloqueada.Id.ToString());
    }

    [Fact]
    public async Task A_simulacao_da_varredura_nao_muda_nada_e_sem_verificador_configurado_ela_nem_roda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manrescan2");
        var antes = _t.Criar();
        var m = await RecebidoDisponivelAsync(antes, c);
        var depois = _t.CriarComVerificacaoExigida(new VerificadorDeTeste { Resposta = ScanVerdict.Clean }, pasta: antes.Pasta);

        var simulacao = await _t.Manutencao(depois).RescanAsync(dryRun: true, c.Tenant.TenantId.Value);

        Assert.Equal((1, 1), (simulacao.Candidates, simulacao.MarkedClean));
        Assert.Equal(AttachmentScanStatus.NotScanned, (await _t.LinhaAsync(depois, c, m.MessageId)).ScanStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _t.Manutencao(antes).RescanAsync(dryRun: false, c.Tenant.TenantId.Value));
    }

    // Conversão para a cifra

    [Fact]
    public async Task A_conversao_cifra_os_objetos_antigos_no_lugar_sem_mudar_o_que_abre_e_repetir_nao_faz_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manencripta");
        var outra = await CenarioDeConversas.CriarAsync(banco, "manencriptb");
        var antes = _t.Criar();
        var m = await RecebidoDisponivelAsync(antes, c);
        var deOutra = await RecebidoDisponivelAsync(antes, outra);
        var chave = AnexosDeTeste.NovaChave();
        var depois = _t.Criar(o => { o.Encryption.ActiveKeyId = "k1"; o.Encryption.Keys["k1"] = chave; }, pasta: antes.Pasta);
        var servico = _t.Manutencao(depois);
        var empresa = c.Tenant.TenantId.Value;
        var caminho = AnexosDeTeste.CaminhoDe(antes, (await _t.LinhaAsync(antes, c, m.MessageId)).StorageKey!);
        var caminhoDeOutra = AnexosDeTeste.CaminhoDe(antes, (await _t.LinhaAsync(antes, outra, deOutra.MessageId)).StorageKey!);

        var simulacao = await servico.EncryptExistingAsync(dryRun: true, empresa);
        Assert.Equal((1, 0), (simulacao.Plaintext, simulacao.Converted));
        Assert.False(EncryptedAttachmentContentStore.HasEncryptedHeader(await File.ReadAllBytesAsync(caminho)));

        var resultado = await servico.EncryptExistingAsync(dryRun: false, empresa);

        Assert.Equal((1, 1, 0), (resultado.Plaintext, resultado.Converted, resultado.Unreadable));
        Assert.True(EncryptedAttachmentContentStore.HasEncryptedHeader(await File.ReadAllBytesAsync(caminho)));
        Assert.False(EncryptedAttachmentContentStore.HasEncryptedHeader(await File.ReadAllBytesAsync(caminhoDeOutra))); // outra empresa: fora do alcance
        Assert.Equal(Png, await _t.AbrirAsync(depois, c, m.SessionId, m.MessageId));

        var repetido = await servico.EncryptExistingAsync(dryRun: false, empresa);
        Assert.Equal((0, 0, 1), (repetido.Plaintext, repetido.Converted, repetido.AlreadyEncrypted));

        // Sem a cifra ligada a conversão não roda.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _t.Manutencao(antes).EncryptExistingAsync(dryRun: false, empresa));

        // E a conferência passa a apontar o que ainda está em texto claro (a outra empresa) quando a cifra está ligada.
        var conferencia = await servico.CheckAsync(repair: false, TimeSpan.FromDays(1), outra.Tenant.TenantId.Value);
        Assert.Equal((1, 1), (conferencia.Ok, conferencia.LegacyCount));
    }

    // Uso

    [Fact]
    public async Task O_uso_por_empresa_conta_arquivos_bytes_sentido_nao_verificados_indisponiveis_e_recusados()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "manuso");
        var k = _t.Criar(o => o.TenantQuotaBytes = 10_000);
        var (sessao, autor) = await AnexosDeTeste.HumanoAsync(c);
        await RecebidoDisponivelAsync(k, c);
        var indisponivel = await _t.ReceberImagemAsync(k, c);
        await _t.ComoAsync(k, c, sp => sp.GetRequiredService<IAttachmentCommands>().FailAsync(new FailAttachmentCommand(indisponivel.MessageId, AttachmentStatus.Unavailable, "expired")));
        var recusado = await _t.ReceberImagemAsync(k, c);
        await _t.CompletarAsync(k, c, recusado.MessageId, "<html></html>"u8.ToArray(), "image/png");
        await _t.EnviarArquivoAsync(k, c, sessao, autor);

        var uso = (await _t.Manutencao(k).UsageAsync()).Single(u => u.TenantId == c.Tenant.TenantId.Value);

        Assert.Equal((2L, 2L * Png.Length, 1L, 1L), (uso.Files, uso.Bytes, uso.Inbound, uso.Outbound));
        Assert.Equal((2L, 1L, 1L, 10_000L), (uso.NotScanned, uso.Unavailable, uso.Rejected, uso.QuotaBytes));
    }
}
