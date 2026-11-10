using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IaBimeta.Api.Attachments;
using IaBimeta.Api.Operations;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O comando de operação <c>attachments</c> e a seção "Arquivos" do <c>status</c> (ADR 0032) com o Api e o Worker REAIS sobre PostgreSQL, a pasta privada e um clamd falso: o relatório de uso, a conferência banco x pasta
/// com reparo (só com confirmação), a varredura posterior e a conversão para a cifra (que recusam rodar sem verificador ou sem cifra), o alcance por empresa e o que a saída nunca mostra (nome de arquivo, caminho, chave).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ComandoDeAnexosTests(PostgresFixture banco) : IAsyncDisposable
{
    private const string Cliente = "5511999990092";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];

    private readonly List<ClamdFalso> _clamds = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var clamd in _clamds)
        {
            await clamd.DisposeAsync();
        }
    }

    private string Pasta(string prefixo) => Path.Combine(banco.PastaDeAnexos, prefixo);

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere() =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir("price"), 150, 0, 15, "req_cmdanexos"));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, ClamdFalso? clamd, string? chaveDeCifra = null)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["Conversations:Attachments:RootPath"] = Pasta(prefixo),
            ["Conversations:Attachments:TenantQuotaBytes"] = "1000",
            ["Agents:Options:ContentRecheck"] = "00:00:00.500",
        };
        if (clamd is not null)
        {
            configuracao["Conversations:Attachments:Scanner:Mode"] = "ClamAv";
            configuracao["Conversations:Attachments:Scanner:Port"] = clamd.Porta.ToString(CultureInfo.InvariantCulture);
            configuracao["Conversations:Attachments:Scanner:Timeout"] = "00:00:01";
            configuracao["Conversations:Attachments:Scanner:Required"] = "true";
        }

        if (chaveDeCifra is not null)
        {
            configuracao["Conversations:Attachments:Encryption:ActiveKeyId"] = "k1";
            configuracao["Conversations:Attachments:Encryption:Keys:k1"] = chaveDeCifra;
        }

        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere());
        return e;
    }

    /// <summary>Uma conversa assumida e UM arquivo recebido e disponível (a mensagem dele devolvida).</summary>
    private static async Task<(string Token, string ItemId, string Mensagem)> ConversaComArquivoAsync(EnsaioDeWhatsApp e)
    {
        var (_, token) = await e.NovoAtendenteAsync();
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.CMD{Guid.NewGuid():N}"[..20], Cliente, "Mauro", "Quanto custa o hemograma?")));
        var sessao = await e.EsperarSessaoAsync("Mauro");
        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                var itens = await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=50");
                var item = itens.AsArray().FirstOrDefault(i => i!["sessionId"]!.GetValue<Guid>() == sessao);
                itemId = item?["pending"] is null ? item?["id"]?.GetValue<string>() : null;
                return itemId is not null;
            },
            "a conversa deveria estar esperando na fila");
        var pedido = Guid.NewGuid();
        await e.EsperarAsync(
            async () => await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = pedido }) == HttpStatusCode.OK,
            "o atendente deveria conseguir assumir a conversa");

        e.Graph.RegistrarMidia("9009001", Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.CMD0001", Cliente, "Mauro", "9009001", "image/png")));
        string? mensagem = null;
        await EnsaioDeIa.AguardarAsync(
            async () =>
            {
                var itensDoHistorico = (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}/messages?take=100"))["items"]!.AsArray();
                var comAnexo = itensDoHistorico.FirstOrDefault(m => m!["attachment"] is not null);
                mensagem = comAnexo?["attachment"]?["status"]?.GetValue<string>() == "Available" ? comAnexo["id"]!.GetValue<string>() : null;
                return mensagem is not null;
            },
            "o arquivo deveria ficar disponível",
            40);
        return (token, itemId!, mensagem!);
    }

    private static async Task<HttpStatusCode> BaixarAsync(EnsaioDeWhatsApp e, string token, string itemId, string mensagem)
    {
        using var pedido = e.Ia.Api.RequisicaoDe(token, HttpMethod.Get, $"/contact-center/items/{itemId}/messages/{mensagem}/attachment");
        using var resposta = await e.Ia.Api.Client.SendAsync(pedido);
        return resposta.StatusCode;
    }

    private static async Task<(int Codigo, string Saida)> ComandoAsync(EnsaioDeWhatsApp e, string verbo, bool confirmar = false, bool reparar = false, Guid? empresa = null, bool aceitarPastaVazia = false)
    {
        var saida = new StringWriter();
        var codigo = await AttachmentsCommand.ExecuteAsync(e.Ia.Api.Services, verbo, confirmar, reparar, TimeSpan.Zero, saida, empresa, aceitarPastaVazia);
        return (codigo, saida.ToString());
    }

    /// <summary>O status do ambiente. Com a empresa, só a linha de uso dela; sem ela, o ambiente todo (a linha da configuração e a do verificador só saem aqui).</summary>
    private static async Task<string> StatusAsync(EnsaioDeWhatsApp e, bool soDaEmpresa)
    {
        var saida = new StringWriter();
        await StatusCommand.ExecuteAsync(e.Ia.Api.Services, new StatusOptions(soDaEmpresa ? e.Ia.Slug : null, null, false, false), saida);
        return saida.ToString();
    }

    private static Task<Guid> EmpresaAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(sp => Task.FromResult(sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value));

    private static Task<MessageAttachment> LinhaAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ConversationsDbContext>().Set<MessageAttachment>().AsNoTracking().SingleAsync(a => a.Direction == MessageDirection.Inbound));

    [Fact]
    public async Task O_relatorio_e_o_status_mostram_uso_cota_cifra_e_verificador_sem_nome_de_arquivo_caminho_nem_chave_e_avisam_quando_o_verificador_cai()
    {
        var clamd = new ClamdFalso();
        _clamds.Add(clamd);
        var chave = AnexosDeTeste.NovaChave();
        await using var e = await IniciarAsync("cmdanexos1", clamd, chave);
        await ConversaComArquivoAsync(e);

        var (_, relatorio) = await ComandoAsync(e, "report"); // o banco de teste é compartilhado: o código de saída (atenção por outras empresas) não é o foco aqui

        Assert.Contains("Cota por empresa : 1000 B", relatorio, StringComparison.Ordinal);
        Assert.Contains("Cifra em repouso : LIGADA (chave ativa 'k1', 1 chave(s) conhecida(s))", relatorio, StringComparison.Ordinal);
        Assert.Contains($"ClamAV em 127.0.0.1:{clamd.Porta}; EXIGIDO", relatorio, StringComparison.Ordinal);
        Assert.Contains("O verificador responde agora: sim", relatorio, StringComparison.Ordinal);
        Assert.Contains(e.Ia.Slug, relatorio, StringComparison.Ordinal);
        Assert.Contains($"{Png.Length} B", relatorio, StringComparison.Ordinal);
        Assert.DoesNotContain("arquivo.png", relatorio, StringComparison.Ordinal); // nome de arquivo
        Assert.DoesNotContain(Pasta("cmdanexos1"), relatorio, StringComparison.Ordinal); // caminho
        Assert.DoesNotContain(chave, relatorio, StringComparison.Ordinal); // chave

        var daEmpresa = await StatusAsync(e, soDaEmpresa: true);
        Assert.Contains("ARQUIVOS", daEmpresa, StringComparison.Ordinal);
        Assert.Contains($"1 arquivo(s), {Png.Length} B de 1000 B (", daEmpresa, StringComparison.Ordinal);
        var doAmbiente = await StatusAsync(e, soDaEmpresa: false);
        Assert.Contains("cota de 1000 B por empresa; cifra em repouso ligada; verificador ClamAV exigido.", doAmbiente, StringComparison.Ordinal);
        Assert.DoesNotContain("NÃO responde", doAmbiente, StringComparison.Ordinal);

        // O verificador cai: o relatório e o status avisam, e o relatório sai com atenção (1).
        clamd.Modo = ModoDoClamd.FecharSemResponder;
        var (codigoComFalha, relatorioComFalha) = await ComandoAsync(e, "report");
        Assert.Equal(1, codigoComFalha);
        Assert.Contains("O verificador responde agora: NÃO", relatorioComFalha, StringComparison.Ordinal);
        Assert.Contains("o verificador de arquivos NÃO responde: arquivos recebidos ficam pendentes e os enviados são recusados.", await StatusAsync(e, soDaEmpresa: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conferencia_acha_o_arquivo_sumido_so_o_reparo_confirmado_o_marca_como_perdido_e_o_atendente_deixa_de_ve_lo_disponivel()
    {
        var clamd = new ClamdFalso();
        _clamds.Add(clamd);
        await using var e = await IniciarAsync("cmdanexos2", clamd, AnexosDeTeste.NovaChave());
        var (token, itemId, mensagem) = await ConversaComArquivoAsync(e);
        var empresa = await EmpresaAsync(e);

        var (limpo, saidaLimpa) = await ComandoAsync(e, "check", empresa: empresa);
        Assert.Equal(0, limpo);
        Assert.Contains("Íntegras                      : 1", saidaLimpa, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await BaixarAsync(e, token, itemId, mensagem));

        // O objeto some da pasta (disco trocado, pasta restaurada sem ele): a conferência acusa, sem alterar nada.
        File.Delete(Assert.Single(Directory.EnumerateFiles(Pasta("cmdanexos2"), "*.bin", SearchOption.AllDirectories)));
        Assert.NotEqual(HttpStatusCode.OK, await BaixarAsync(e, token, itemId, mensagem));
        var (problema, saidaComProblema) = await ComandoAsync(e, "check", empresa: empresa);
        Assert.Equal(1, problema);
        Assert.Contains("Sem objeto no armazenamento", saidaComProblema, StringComparison.Ordinal);
        Assert.Contains("use --repair --confirm", saidaComProblema, StringComparison.Ordinal);
        Assert.Equal(AttachmentStatus.Available, (await LinhaAsync(e)).Status);

        // A trava: a pasta ficou SEM NENHUM objeto da empresa e todas as linhas estão sem objeto (o quadro de RootPath errado): o reparo recusa, e nada muda.
        var (recusado, saidaDaRecusa) = await ComandoAsync(e, "check", confirmar: true, reparar: true, empresa: empresa);
        Assert.Equal(2, recusado);
        Assert.Contains("Operação não executada", saidaDaRecusa, StringComparison.Ordinal);
        Assert.Contains("RootPath está errado", saidaDaRecusa, StringComparison.Ordinal);
        Assert.Equal(AttachmentStatus.Available, (await LinhaAsync(e)).Status);

        // Com a confirmação explícita de que a perda é real, o reparo marca a linha como perdida (nunca apaga a mensagem); repetir não encontra mais nada.
        var (reparado, saidaDoReparo) = await ComandoAsync(e, "check", confirmar: true, reparar: true, empresa: empresa, aceitarPastaVazia: true);
        Assert.Equal(1, reparado); // a divergência existia quando a conferência rodou
        Assert.Contains("1 marcada(s) como 'lost'", saidaDoReparo, StringComparison.Ordinal);
        var linha = await LinhaAsync(e);
        Assert.Equal((AttachmentStatus.Unavailable, MessageAttachment.LostFailureCode), (linha.Status, linha.FailureCode));
        var (depois, _) = await ComandoAsync(e, "check", empresa: empresa);
        Assert.Equal(0, depois);
        var historico = (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}/messages?take=100"))["items"]!.AsArray();
        Assert.Equal("lost", historico.Single(m => m!["id"]!.GetValue<string>() == mensagem)!["attachment"]!["failureCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_varredura_posterior_e_a_conversao_nada_tem_a_fazer_com_tudo_em_dia_e_o_alcance_por_empresa_nunca_atinge_outra()
    {
        var clamd = new ClamdFalso();
        _clamds.Add(clamd);
        await using var e = await IniciarAsync("cmdanexos3", clamd, AnexosDeTeste.NovaChave());
        await ConversaComArquivoAsync(e);
        var empresa = await EmpresaAsync(e);
        var outraEmpresa = Guid.NewGuid(); // uma empresa sem nenhum arquivo: o alcance dela é vazio

        var (codigoDaVarredura, varredura) = await ComandoAsync(e, "rescan", confirmar: true, empresa: empresa);
        Assert.Equal(0, codigoDaVarredura);
        Assert.Contains("Candidatos: 0", varredura, StringComparison.Ordinal);

        var (codigoDaConversao, conversao) = await ComandoAsync(e, "encrypt-existing", confirmar: true, empresa: empresa);
        Assert.Equal(0, codigoDaConversao);
        Assert.Contains("Em texto claro: 0", conversao, StringComparison.Ordinal);
        Assert.Contains("já cifrados: 1", conversao, StringComparison.Ordinal);

        var (_, deOutra) = await ComandoAsync(e, "check", empresa: outraEmpresa);
        Assert.Contains("Linhas disponíveis conferidas : 0", deOutra, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_verificador_nem_cifra_configurados_a_varredura_e_a_conversao_recusam_rodar_com_o_motivo_e_o_relatorio_informa()
    {
        await using var e = await IniciarAsync("cmdanexos4", clamd: null);

        var (codigoDaVarredura, varredura) = await ComandoAsync(e, "rescan", confirmar: true);
        var (codigoDaConversao, conversao) = await ComandoAsync(e, "encrypt-existing", confirmar: true);
        var (_, relatorio) = await ComandoAsync(e, "report");

        Assert.Equal(2, codigoDaVarredura);
        Assert.Contains("Operação não executada: Não há verificador configurado", varredura, StringComparison.Ordinal);
        Assert.Equal(2, codigoDaConversao);
        Assert.Contains("Operação não executada: A cifra de arquivos não está ligada", conversao, StringComparison.Ordinal);
        Assert.Contains("DESLIGADA (arquivos novos em texto claro)", relatorio, StringComparison.Ordinal);
        Assert.Contains("nenhum (só a amostra de teste EICAR é detectada)", relatorio, StringComparison.Ordinal);
        Assert.Contains("cifra em repouso DESLIGADA (texto claro); sem verificador (só a amostra EICAR)", await StatusAsync(e, soDaEmpresa: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_teste_do_verificador_prova_o_benigno_o_tamanho_e_a_amostra_EICAR_falando_direto_com_o_motor_e_reprova_o_daemon_que_nao_detecta_ou_nao_responde()
    {
        var clamd = new ClamdFalso(ModoDoClamd.ComoOClamd);
        _clamds.Add(clamd);
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Conversations:Attachments:RootPath"] = Pasta("cmdscanner");
        configuracao["Conversations:Attachments:Scanner:Mode"] = "ClamAv";
        configuracao["Conversations:Attachments:Scanner:Port"] = clamd.Porta.ToString(CultureInfo.InvariantCulture);
        configuracao["Conversations:Attachments:Scanner:Timeout"] = "00:00:05";
        await using var api = await ApiServer.StartAsync("Production", configuracao);

        // Um daemon que se comporta como o real: o benigno (pequeno e de 2 MB) passa, a amostra EICAR é detectada e CHEGOU ao daemon (a detecção embutida não a intercepta aqui).
        var saida = new StringWriter();
        var codigo = await AttachmentsCommand.ExecuteAsync(api.Services, "test-scanner", false, false, TimeSpan.Zero, saida, scannerTestMb: 2);
        Assert.Equal(0, codigo);
        Assert.Contains("Integração com o verificador confirmada", saida.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(saida.ToString(), "OK     ").Count);
        Assert.Equal(3, clamd.Recebidos.Count);
        Assert.Contains(clamd.Recebidos, r => r.Conteudo.Length == 2 * 1024 * 1024);
        Assert.Contains(clamd.Recebidos, r => EicarTestSample.IsPresentIn(r.Conteudo));

        // Um daemon que diz "limpo" para tudo (assinaturas ausentes) é reprovado: a amostra não foi detectada.
        clamd.Modo = ModoDoClamd.Ok;
        var semDeteccao = new StringWriter();
        Assert.Equal(1, await AttachmentsCommand.ExecuteAsync(api.Services, "test-scanner", false, false, TimeSpan.Zero, semDeteccao));
        Assert.Contains("FALHOU amostra de teste EICAR", semDeteccao.ToString(), StringComparison.Ordinal);
        Assert.Contains("A integração NÃO está correta", semDeteccao.ToString(), StringComparison.Ordinal);

        // Um daemon fora do ar é reprovado, nunca "limpo".
        clamd.Modo = ModoDoClamd.FecharSemResponder;
        var foraDoAr = new StringWriter();
        Assert.Equal(1, await AttachmentsCommand.ExecuteAsync(api.Services, "test-scanner", false, false, TimeSpan.Zero, foraDoAr));
        Assert.Contains("Unavailable", foraDoAr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_teste_do_verificador_recusa_rodar_sem_verificador_configurado()
    {
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var saida = new StringWriter();

        var codigo = await AttachmentsCommand.ExecuteAsync(api.Services, "test-scanner", false, false, TimeSpan.Zero, saida);

        Assert.Equal(2, codigo);
        Assert.Contains("não há verificador configurado", saida.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Os_argumentos_invalidos_do_comando_saem_com_codigo_2_sem_subir_o_Api_e_o_reparo_exige_confirmacao()
    {
        string[][] uso =
        [
            [],
            ["apagar-tudo"],
            ["report", "--confirm", "--bogus"],
            ["check", "--repair"], // reparo sem --confirm
            ["check", "--allow-empty-folder"], // a permissão só vale com o reparo
            ["rescan", "--allow-empty-folder"],
            ["test-scanner", "--size-mb", "0"], // o tamanho do teste vai de 1 a 100 MB
            ["test-scanner", "--size-mb", "101"],
            ["test-scanner", "--size-mb"],
            ["check", "--size-mb", "2"], // só o test-scanner tem esta opção
            ["check", "--tenant", "nao-e-um-guid"],
            ["check", "--tenant", Guid.Empty.ToString()],
            ["report", "--tenant", Guid.NewGuid().ToString()], // o relatório não tem alcance por empresa
            ["check", "--orphan-days", "-1"],
            ["rescan", "--repair"],
        ];

        foreach (var argumentos in uso)
        {
            var saida = new StringWriter();
            Assert.True(await AttachmentsCommand.RunAsync(argumentos, saida) == 2, $"deveria recusar: {string.Join(' ', argumentos)}");
            Assert.NotEmpty(saida.ToString());
        }
    }
}
