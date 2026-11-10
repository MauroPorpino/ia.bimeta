using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O ENVIO de arquivos (imagem, documento e áudio) pelo atendente (ADR 0031), de ponta a ponta e por HTTP de verdade: o atendente envia pela API como a interface faz, o Conversations valida pelo CONTEÚDO, guarda em pasta
/// privada e grava a mensagem e o evento no mesmo outbox do texto, o Sender sobe o arquivo e envia a mensagem pela Graph API simulada com o adaptador REAL, e o histórico mostra o arquivo e o estado de entrega. Prova a janela
/// de 24 horas, a autorização, a recusa por conteúdo, a idempotência, a falha da subida (sempre sem mensagem), a dúvida da mensagem (SendUnknown, nunca reenviada), o Worker derrubado e o arquivo cujo prazo de guarda
/// venceu. Nenhuma credencial é real e nenhuma chamada à Meta ou à OpenAI.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnvioDeMidiaTests(PostgresFixture banco)
{
    private const string Cliente = "5511999990081";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
    private static readonly byte[] OggOpus = [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 19, .. "OpusHead"u8, 1, 1, 0x38, 1, 0x80, 0xBB, 0, 0, 0, 0, 0];
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00];

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_envmidia"));

    private sealed record Atendimento(Guid Sessao, string ItemId, string Token);

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, Dictionary<string, string?>? extra = null)
    {
        var configuracao = new Dictionary<string, string?> { ["Conversations:Attachments:RootPath"] = Path.Combine(banco.PastaDeAnexos, prefixo) };
        foreach (var (chave, valor) in extra ?? new Dictionary<string, string?>())
        {
            configuracao[chave] = valor;
        }

        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        return e;
    }

    /// <summary>O cliente escreve (no relógio do provedor informado), a IA transfere e o atendente assume a conversa. Termina com o aviso de transferência já enviado (uma captura de texto na Meta).</summary>
    private static async Task<Atendimento> AtenderAsync(EnsaioDeWhatsApp e, string nome, string waId = Cliente, DateTimeOffset? momento = null)
    {
        var token = (await e.NovoAtendenteAsync()).Token;
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto("wamid.ENV" + Guid.NewGuid().ToString("N")[..12], waId, nome, "Preciso do resultado do meu exame.", momento: momento)));
        var sessao = await e.EsperarSessaoAsync(nome);

        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                var itens = await e.Ia.Api.LerAsync<JsonNode>(token, "/contact-center/items?status=Waiting&take=50");
                itemId = itens.AsArray().FirstOrDefault(i => i!["sessionId"]!.GetValue<Guid>() == sessao)?["id"]?.GetValue<string>();
                return itemId is not null;
            },
            "a conversa deveria estar esperando na fila");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count >= 1, "o aviso de transferência deveria sair pelo WhatsApp");

        // Assumir logo depois de o item aparecer na fila pode dar 409 transitório: repete o MESMO pedido até assentar.
        var pedido = Guid.NewGuid();
        await e.EsperarAsync(
            async () => await e.Ia.Api.StatusAsync(token, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = pedido }) == HttpStatusCode.OK,
            "o atendente deveria conseguir assumir a conversa");
        return new Atendimento(sessao, itemId!, token);
    }

    private static int Midias(EnsaioDeWhatsApp e, string tipo) => e.Graph.Capturas.Count(c => c.Tipo == tipo);

    private static int TodasAsMidias(EnsaioDeWhatsApp e) => e.Graph.Capturas.Count(c => c.Tipo is "image" or "audio" or "document");

    private int Objetos(string prefixo)
    {
        var pasta = Path.Combine(banco.PastaDeAnexos, prefixo);
        return Directory.Exists(pasta) ? Directory.EnumerateFiles(pasta, "*.bin", SearchOption.AllDirectories).Count() : 0;
    }

    private static async Task<IReadOnlyList<JsonNode>> HistoricoDoAtendenteAsync(EnsaioDeWhatsApp e, Atendimento a) =>
        (await e.Ia.Api.LerAsync<JsonNode>(a.Token, $"/contact-center/items/{a.ItemId}/messages?take=100"))["items"]!.AsArray().Select(m => m!).ToList();

    private static async Task<IReadOnlyList<JsonNode>> MensagensDoAtendenteAsync(EnsaioDeWhatsApp e, Atendimento a) =>
        (await HistoricoDoAtendenteAsync(e, a)).Where(m => m["author"]!.GetValue<string>() == "Human").OrderBy(m => m["messageSeq"]!.GetValue<long>()).ToList();

    private static async Task<(HttpStatusCode Status, byte[] Bytes, IReadOnlyDictionary<string, string> Cabecalhos)> BaixarAsync(EnsaioDeWhatsApp e, Atendimento a, string mensagem)
    {
        using var pedido = e.Ia.Api.RequisicaoDe(a.Token, HttpMethod.Get, $"/contact-center/items/{a.ItemId}/messages/{mensagem}/attachment");
        using var resposta = await e.Ia.Api.Client.SendAsync(pedido);
        var cabecalhos = resposta.Headers.Concat(resposta.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return (resposta.StatusCode, await resposta.Content.ReadAsByteArrayAsync(), cabecalhos);
    }

    private static Task<(HttpStatusCode Status, JsonNode? Corpo)> EnviarAsync(
        EnsaioDeWhatsApp e, Atendimento a, byte[] conteudo, string nome, string? tipo, string? legenda = null, Guid? pedido = null) =>
        e.EnviarArquivoAsync(a.Token, a.ItemId, conteudo, nome, tipo, legenda, pedido);

    // O caminho feliz

    [Fact]
    public async Task Imagem_documento_e_audio_saem_pela_Meta_pelo_mesmo_outbox_e_aparecem_no_historico_com_o_arquivo_e_o_estado()
    {
        await using var e = await IniciarAsync("envmidiaok");
        var a = await AtenderAsync(e, "Envio Feliz");
        var chamadasDeIaAntes = e.Ia.OpenAi.Roteiros.Capturas.Count;

        // Imagem com legenda, documento (nome com acento e legenda) e áudio (sem legenda): cada um com o tipo declarado pelo navegador, como a interface envia.
        var imagem = await EnviarAsync(e, a, Png, "exame.png", "image/png", "Segue a imagem do exame");
        var documento = await EnviarAsync(e, a, Pdf, "Laudo março.pdf", "application/pdf", "Segue o laudo");
        var audio = await EnviarAsync(e, a, OggOpus, "orientacao.ogg", "audio/ogg");
        foreach (var (status, corpo) in new[] { imagem, documento, audio })
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("Applied", corpo!["outcome"]!.GetValue<string>());
        }

        await e.EsperarAsync(async () => TodasAsMidias(e) == 3, "as três mensagens de arquivo deveriam chegar à Meta");
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 0);
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 1);
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 2);

        // A subida e a mensagem: o token só no cabeçalho, o número de origem na URL, o arquivo inteiro no formulário e o identificador devolvido na mensagem.
        var subidas = e.Graph.Subidas;
        Assert.Equal(3, subidas.Count);
        Assert.All(subidas, s =>
        {
            Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, s.Credencial);
            Assert.EndsWith($"/{e.NumeroDoCanal}/media", s.Caminho);
            Assert.Equal("whatsapp", s.Produto);
            Assert.Equal(s.Tipo, s.TipoDoArquivo);
        });
        Assert.Equal(("image/png", "upload.png"), (subidas[0].Tipo, subidas[0].NomeDoArquivo));
        Assert.Equal(Png, subidas[0].Conteudo);
        Assert.Equal(("application/pdf", "upload.pdf"), (subidas[1].Tipo, subidas[1].NomeDoArquivo));
        Assert.Equal(Pdf, subidas[1].Conteudo);
        Assert.Equal(("audio/ogg", "upload.ogg"), (subidas[2].Tipo, subidas[2].NomeDoArquivo));
        Assert.Equal(OggOpus, subidas[2].Conteudo);

        var envios = e.Graph.Capturas.Where(c => c.Tipo is "image" or "document" or "audio").ToList();
        Assert.All(envios, c =>
        {
            Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, c.Credencial);
            Assert.EndsWith($"/{e.NumeroDoCanal}/messages", c.Caminho);
            Assert.Equal(Cliente, c.Para);
            Assert.NotNull(c.DadoOpaco);
        });
        Assert.Equal(("image", subidas[0].Id, "Segue a imagem do exame", (string?)null), (envios[0].Tipo, envios[0].IdDoArquivo, envios[0].Legenda, envios[0].NomeDoDocumento));
        Assert.Equal(("document", subidas[1].Id, "Segue o laudo", "Laudo março.pdf"), (envios[1].Tipo, envios[1].IdDoArquivo, envios[1].Legenda, envios[1].NomeDoDocumento));
        Assert.Equal(("audio", subidas[2].Id, (string?)null, (string?)null), (envios[2].Tipo, envios[2].IdDoArquivo, envios[2].Legenda, envios[2].NomeDoDocumento));
        Assert.Equal(new[] { "id" }, envios[2].Arquivo!.AsObject().Select(p => p.Key).ToArray());

        // O histórico do atendente: tipo de mídia, arquivo disponível (tipo verificado, nome saneado, tamanho, legenda) e entrega; nada de caminho, identificador do provedor ou URL.
        var mensagens = await MensagensDoAtendenteAsync(e, a);
        Assert.Equal(new[] { "Image", "Document", "Audio" }, mensagens.Select(m => m["kind"]!.GetValue<string>()).ToArray());
        Assert.All(mensagens, m =>
        {
            Assert.Equal("Outbound", m["direction"]!.GetValue<string>());
            Assert.Equal("Sent", m["deliveryState"]!.GetValue<string>());
            Assert.Null(m["text"]);
            Assert.Equal("Available", m["attachment"]!["status"]!.GetValue<string>());
        });
        Assert.Equal(("image/png", "exame.png", Png.Length, "Segue a imagem do exame"), Visao(mensagens[0]));
        Assert.Equal(("application/pdf", "Laudo março.pdf", Pdf.Length, "Segue o laudo"), Visao(mensagens[1]));
        Assert.Equal(("audio/ogg", "orientacao.ogg", OggOpus.Length, null), Visao(mensagens[2]));
        var json = (await e.Ia.Api.LerAsync<JsonNode>(a.Token, $"/contact-center/items/{a.ItemId}/messages?take=100")).ToJsonString();
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(subidas[0].Id!, json, StringComparison.Ordinal);

        // O atendente abre o arquivo que enviou, com a mesma entrega segura do arquivo do cliente (imagem e áudio inline, o resto como download).
        var baixadaImagem = await BaixarAsync(e, a, mensagens[0]["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, baixadaImagem.Status);
        Assert.Equal(Png, baixadaImagem.Bytes);
        Assert.StartsWith("inline", baixadaImagem.Cabecalhos["Content-Disposition"], StringComparison.Ordinal);
        Assert.Equal("nosniff", baixadaImagem.Cabecalhos["X-Content-Type-Options"]);
        var baixadoDocumento = await BaixarAsync(e, a, mensagens[1]["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, baixadoDocumento.Status);
        Assert.Equal(Pdf, baixadoDocumento.Bytes);
        Assert.StartsWith("attachment", baixadoDocumento.Cabecalhos["Content-Disposition"], StringComparison.Ordinal);

        // A IA não participou e nenhum conteúdo, legenda, nome nem credencial foi para o log.
        Assert.Equal(chamadasDeIaAntes, e.Ia.OpenAi.Roteiros.Capturas.Count);
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.TokenDeAcesso) || l.Contains("Segue a imagem") || l.Contains("Laudo março") || l.Contains("Segue o laudo"));
    }

    private static (string? Tipo, string? Nome, long? Tamanho, string? Legenda) Visao(JsonNode mensagem)
    {
        var anexo = mensagem["attachment"]!;
        return (anexo["mimeType"]?.GetValue<string>(), anexo["fileName"]?.GetValue<string>(), anexo["sizeBytes"]?.GetValue<long>(), anexo["caption"]?.GetValue<string>());
    }

    // Janela, autorização e forma

    [Fact]
    public async Task Fora_da_janela_de_24_horas_o_arquivo_e_recusado_antes_de_gravar_guardar_ou_subir_e_o_texto_do_atendente_nao_muda()
    {
        await using var e = await IniciarAsync("envmidiajanela");
        var a = await AtenderAsync(e, "Envio Janela", momento: DateTimeOffset.UtcNow.AddHours(-26));

        var (status, problema) = await EnviarAsync(e, a, Png, "exame.png", "image/png", "Segue");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("ReplyWindowClosed", problema!["code"]!.GetValue<string>());
        Assert.Empty(e.Graph.Subidas);
        Assert.Equal(0, TodasAsMidias(e));
        Assert.Equal(0, Objetos("envmidiajanela"));
        Assert.Empty(await MensagensDoAtendenteAsync(e, a));
    }

    [Fact]
    public async Task Sem_sessao_ou_sem_ser_o_responsavel_o_arquivo_e_negado_e_a_forma_do_pedido_e_conferida()
    {
        await using var e = await IniciarAsync("envmidiaperm");
        var a = await AtenderAsync(e, "Envio Permissao");
        var outro = (await e.NovoAtendenteAsync()).Token;

        // Sem sessão: 401. Atendente que não é o responsável: negado (ou invisível) e nada é gravado nem subido.
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.EnviarArquivoAsync(string.Empty, a.ItemId, Png, "x.png", "image/png")).Status);
        var (negado, _) = await e.EnviarArquivoAsync(outro, a.ItemId, Png, "x.png", "image/png");
        Assert.Contains(negado, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        var admin = await e.TokenDeAsync(IaBimeta.Identity.Domain.Role.OrganizationAdmin);
        var (doAdmin, _) = await e.EnviarArquivoAsync(admin, a.ItemId, Png, "x.png", "image/png");
        Assert.Contains(doAdmin, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // A forma do pedido: não multipart, sem identificador, sem arquivo, arquivo vazio.
        var (naoForm, _) = await e.ChamarAsync(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply-media", new JsonObject { ["requestId"] = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, naoForm);

        using (var semId = new MultipartFormDataContent { { new ByteArrayContent(Png), "file", "x.png" } })
        using (var pedido = e.Ia.Api.RequisicaoDe(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply-media", semId))
        {
            using var resposta = await e.Ia.Api.Client.SendAsync(pedido);
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        }

        using (var semArquivo = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "requestId" } })
        using (var pedido = e.Ia.Api.RequisicaoDe(a.Token, HttpMethod.Post, $"/contact-center/items/{a.ItemId}/reply-media", semArquivo))
        {
            using var resposta = await e.Ia.Api.Client.SendAsync(pedido);
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await EnviarAsync(e, a, [], "vazio.png", "image/png")).Status);

        Assert.Empty(e.Graph.Subidas);
        Assert.Equal(0, Objetos("envmidiaperm"));
        Assert.Empty(await MensagensDoAtendenteAsync(e, a));
    }

    [Fact]
    public async Task O_arquivo_e_recusado_pelo_conteudo_com_o_motivo_sem_guardar_subir_nem_gravar_mensagem()
    {
        await using var e = await IniciarAsync("envmidiainv", new Dictionary<string, string?> { ["Conversations:Attachments:MaxImageBytes"] = "2048" });
        var a = await AtenderAsync(e, "Envio Invalido");
        var grande = new byte[3000];
        Png.CopyTo(grande, 0);

        // O que o navegador diz (extensão e tipo) não vale: um executável com nome de imagem, um PDF declarado como imagem, uma imagem grande demais, áudio com legenda e legenda longa demais.
        var casos = new (byte[] Conteudo, string Nome, string Tipo, string? Legenda, string Motivo)[]
        {
            (Exe, "foto.png", "image/png", null, "type-not-allowed"),
            (Pdf, "laudo.png", "image/png", null, "content-mismatch"),
            (grande, "grande.png", "image/png", null, "too-large"),
            (OggOpus, "nota.ogg", "audio/ogg", "legenda em audio", "caption-not-supported"),
            (Png, "foto.png", "image/png", new string('x', 1025), "caption-too-long"),
        };
        foreach (var (conteudo, nome, tipo, legenda, motivo) in casos)
        {
            var (status, problema) = await EnviarAsync(e, a, conteudo, nome, tipo, legenda);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Equal(("MediaRejected", motivo), (problema!["code"]!.GetValue<string>(), problema["reason"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(problema["detail"]!.GetValue<string>()));
        }

        Assert.Empty(e.Graph.Subidas);
        Assert.Equal(0, Objetos("envmidiainv"));
        Assert.Empty(await MensagensDoAtendenteAsync(e, a));
    }

    // Idempotência e concorrência

    [Fact]
    public async Task O_mesmo_pedido_repetido_ou_simultaneo_grava_guarda_e_envia_uma_so_vez()
    {
        await using var e = await IniciarAsync("envmidiadup");
        var a = await AtenderAsync(e, "Envio Duplicado");
        var pedido = Guid.NewGuid();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => EnviarAsync(e, a, Png, "exame.png", "image/png", "Segue", pedido)));

        // Um aplica; os demais convergem (200) ou pedem para reler o atendimento (409): nunca gravam de novo.
        Assert.Equal(1, respostas.Count(r => r.Status == HttpStatusCode.OK && r.Corpo!["outcome"]!.GetValue<string>() == "Applied"));
        Assert.All(respostas, r => Assert.Contains(r.Status, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        var repetida = await EnviarAsync(e, a, Png, "exame.png", "image/png", "Segue", pedido);
        Assert.Equal((HttpStatusCode.OK, "AlreadyApplied"), (repetida.Status, repetida.Corpo!["outcome"]!.GetValue<string>()));
        Assert.Equal(respostas.Single(r => r.Status == HttpStatusCode.OK && r.Corpo!["outcome"]!.GetValue<string>() == "Applied").Corpo!["messageId"]!.GetValue<Guid>(), repetida.Corpo["messageId"]!.GetValue<Guid>());

        // O mesmo pedido com outro conteúdo é outro efeito: recusado, sem novo arquivo.
        var outroEfeito = await EnviarAsync(e, a, [.. Png, 0], "exame.png", "image/png", "Segue", pedido);
        Assert.Equal(HttpStatusCode.Conflict, outroEfeito.Status);

        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 0);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(1, Midias(e, "image"));
        Assert.Single(e.Graph.Subidas);
        Assert.Equal(1, Objetos("envmidiadup"));
        Assert.Single(await MensagensDoAtendenteAsync(e, a));
    }

    // Falhas da Meta

    [Fact]
    public async Task Falha_passageira_da_subida_repete_a_subida_e_a_mensagem_sai_uma_so_vez_e_falha_definitiva_nao_envia_nada_e_permite_enviar_de_novo()
    {
        await using var e = await IniciarAsync("envmidiasubida");
        var a = await AtenderAsync(e, "Envio Subida");

        // (a) A subida falha duas vezes com 503 (a Meta pede para esperar 1 s) e na terceira passa: nenhuma mensagem saiu antes, e a mensagem sai UMA vez.
        var tentativas = 0;
        e.Graph.EsperaDaSubida = 1;
        e.Graph.RoteiroDaSubida = _ => Interlocked.Increment(ref tentativas) <= 2 ? (503, "{}") : (200, new JsonObject { ["id"] = "UPLOADOK1" }.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png")).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 0, segundos: 60);
        Assert.Equal(3, e.Graph.Subidas.Count);
        Assert.Equal(1, Midias(e, "image"));
        Assert.Equal("UPLOADOK1", e.Graph.Capturas.Single(c => c.Tipo == "image").IdDoArquivo);

        // (b) A Meta recusa o arquivo de forma definitiva (131053, tipo que não confere): a mensagem FALHA de forma visível, nenhuma mensagem sai e o atendente é avisado.
        e.Graph.EsperaDaSubida = null;
        e.Graph.RoteiroDaSubida = _ => ServidorGraphLoopback.Erro(400, 131053);
        var recusado = await EnviarAsync(e, a, Pdf, "laudo.pdf", "application/pdf");
        Assert.Equal(HttpStatusCode.OK, recusado.Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 1);
        Assert.Equal(4, e.Graph.Subidas.Count);
        Assert.Equal(0, Midias(e, "document"));
        await e.EsperarAsync(
            async () => (await e.Ia.Api.LerAsync<JsonNode>(a.Token, $"/contact-center/items/{a.ItemId}"))["deliveryFailures"]?.GetValue<int>() > 0,
            "o atendente deveria ser avisado da falha de envio");

        // (c) Enviar o arquivo de novo (outro pedido) é uma nova resposta normal: sai, e o aviso de falha é recolhido.
        e.Graph.RoteiroDaSubida = null;
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Pdf, "laudo.pdf", "application/pdf")).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 2);
        Assert.Equal(1, Midias(e, "document"));
        await e.EsperarAsync(
            async () => ((await e.Ia.Api.LerAsync<JsonNode>(a.Token, $"/contact-center/items/{a.ItemId}"))["deliveryFailures"]?.GetValue<int>() ?? 0) == 0,
            "o aviso de falha deveria sair depois do novo envio");
    }

    [Fact]
    public async Task Resultado_incerto_da_mensagem_depois_da_subida_vira_SendUnknown_e_nunca_e_reenviado_e_a_janela_do_provedor_falha_de_forma_visivel()
    {
        await using var e = await IniciarAsync("envmidiaduvida");
        var a = await AtenderAsync(e, "Envio Duvida");

        // A subida passa, a Meta responde 500 sem corpo de erro na mensagem: PODE ter sido entregue. SendUnknown, sem reenvio e sem nova subida.
        e.Graph.Roteiro = c => c.Tipo == "image" ? (500, string.Empty) : e.Graph.Aceita();
        var pedido = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png", null, pedido)).Status);
        await e.EsperarEntregaAsync(a.Sessao, "SendUnknown", autor: "Human", ordem: 0);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, Midias(e, "image"));
        Assert.Single(e.Graph.Subidas);

        // Repetir o pedido não reenvia (a mensagem já existe) e o estado segue incerto.
        e.Graph.Roteiro = _ => e.Graph.Aceita();
        Assert.Equal("AlreadyApplied", (await EnviarAsync(e, a, Png, "exame.png", "image/png", null, pedido)).Corpo!["outcome"]!.GetValue<string>());
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal((1, "SendUnknown"), (Midias(e, "image"), await e.EstadoDeEntregaAsync(a.Sessao, "Human", 0)));

        // A Meta diz que a janela de 24 horas do provedor fechou (131047): falha definitiva e visível, sem nova tentativa.
        e.Graph.Roteiro = c => c.Tipo == "document" ? ServidorGraphLoopback.Erro(400, 131047) : e.Graph.Aceita();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Pdf, "laudo.pdf", "application/pdf")).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 1);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Midias(e, "document"));
    }

    [Fact]
    public async Task Erro_de_conta_na_subida_pausa_a_conta_e_o_arquivo_seguinte_falha_sem_tocar_na_Meta()
    {
        await using var e = await IniciarAsync("envmidiaconta");
        var a = await AtenderAsync(e, "Envio Conta");
        var admin = await e.TokenDeAsync(IaBimeta.Identity.Domain.Role.OrganizationAdmin);

        e.Graph.RoteiroDaSubida = _ => ServidorGraphLoopback.Erro(401, 190);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png")).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 0);
        Assert.True((await e.ChamarAsync(admin, HttpMethod.Get, $"/channels/accounts/{e.ContaId}/sending")).Corpo!["paused"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Pdf, "laudo.pdf", "application/pdf")).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", ordem: 1);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(e.Graph.Subidas); // a conta pausada não chamou a Meta
        Assert.Equal(0, TodasAsMidias(e));
    }

    // Worker derrubado e prazo de guarda

    [Fact]
    public async Task Worker_que_cai_no_meio_da_mensagem_deixa_o_resultado_incerto_e_o_arquivo_nunca_e_reenviado()
    {
        await using var e = await IniciarAsync("envmidiareinicio", new Dictionary<string, string?>
        {
            ["Messaging:LeaseDuration"] = "00:00:03",
            ["Channels:Providers:WhatsApp:RequestTimeout"] = "00:01:00",
        });
        var a = await AtenderAsync(e, "Envio Reinicio");

        // A Meta recebe a mensagem do arquivo (a subida já passou) e nunca responde: o processo vai cair com a chamada em andamento.
        e.Graph.Antes = (captura, cancelamento) => captura.Tipo == "image" ? Task.Delay(Timeout.Infinite, cancelamento) : Task.CompletedTask;
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png")).Status);
        await e.EsperarAsync(async () => Midias(e, "image") == 1, "a mensagem do arquivo deveria chegar à Meta e ficar em andamento");

        e.Graph.Antes = null;
        e.Graph.Roteiro = _ => e.Graph.Aceita(); // se o novo Worker (erradamente) reenviasse, a Meta aceitaria e o arquivo sairia DUAS vezes
        await e.Ia.ReiniciarWorkerAsync();

        await e.EsperarEntregaAsync(a.Sessao, "SendUnknown", autor: "Human", segundos: 40);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Midias(e, "image"));
        Assert.Single(e.Graph.Subidas);
    }

    [Fact]
    public async Task Worker_que_cai_no_meio_da_subida_nunca_reenvia_e_a_mensagem_fica_incerta_por_prudencia()
    {
        await using var e = await IniciarAsync("envmidiaquedasub", new Dictionary<string, string?> { ["Messaging:LeaseDuration"] = "00:00:03", ["Channels:Providers:WhatsApp:MediaTimeout"] = "00:01:00" });
        var a = await AtenderAsync(e, "Envio Queda Subida");

        // O Worker cai durante a SUBIDA. Nenhuma mensagem saiu, mas o processo não sabe em que ponto parou: por prudência (a regra de efeito do Sender é a mesma do texto), o resultado fica incerto e NADA é reenviado.
        e.Graph.AntesDaSubida = (_, cancelamento) => Task.Delay(Timeout.Infinite, cancelamento);
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png")).Status);
        await e.EsperarAsync(async () => e.Graph.Subidas.Count == 1, "a subida deveria chegar à Meta e ficar em andamento");

        e.Graph.AntesDaSubida = null;
        await e.Ia.ReiniciarWorkerAsync();

        await e.EsperarEntregaAsync(a.Sessao, "SendUnknown", autor: "Human", segundos: 40);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(0, TodasAsMidias(e));
        Assert.Single(e.Graph.Subidas);

        // O atendente reenvia o arquivo por uma nova resposta (outro pedido): sai normalmente.
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png")).Status);
        await e.EsperarEntregaAsync(a.Sessao, "Sent", autor: "Human", ordem: 1);
        Assert.Equal(1, Midias(e, "image"));
    }

    [Fact]
    public async Task Arquivo_cujo_prazo_de_guarda_venceu_antes_do_Sender_falha_de_forma_visivel_sem_chamar_a_Meta()
    {
        // O Worker é parado e subido de novo no meio: o lease curto (como nos testes de reinício) evita que um evento da conversa que o Worker antigo segurava no instante da parada atrase a fila da mesma conversa.
        await using var e = await IniciarAsync("envmidiaprazo", new Dictionary<string, string?> { ["Messaging:LeaseDuration"] = "00:00:03" });
        var a = await AtenderAsync(e, "Envio Prazo");

        // O Worker está parado: a mensagem fica no outbox. Passado o prazo, o expurgo apaga o conteúdo (o arquivo vira "indisponível").
        await e.Ia.PararWorkerAsync();
        Assert.Equal(HttpStatusCode.OK, (await EnviarAsync(e, a, Png, "exame.png", "image/png", "Segue")).Status);
        var mensagem = (await MensagensDoAtendenteAsync(e, a)).Single()["id"]!.GetValue<Guid>();
        Assert.Equal(1, Objetos("envmidiaprazo"));

        await e.Ia.ComoSistemaAsync(async sp =>
        {
            var db = sp.GetRequiredService<ConversationsDbContext>();
            var antes = DateTimeOffset.UtcNow.AddDays(-40);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversations.message_attachments SET created_at = {antes} WHERE message_id = {mensagem}");

            // O armazenamento do Api e do Worker deste ensaio (a pasta própria dele): é nele que o expurgo apaga o objeto.
            var armazenamento = new FileSystemAttachmentContentStore(new AttachmentOptions { RootPath = Path.Combine(banco.PastaDeAnexos, "envmidiaprazo") });
            var tarefa = AttachmentObjectsRetentionTask.ForOutbound(sp.GetRequiredService<IServiceScopeFactory>(), armazenamento, TimeProvider.System);
            var tenant = (await db.Messages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == mensagem)).TenantId.Value;
            var resultado = await tarefa.RunAsync(new RetentionPass(tenant, Array.Empty<Guid>(), 30, DateTimeOffset.UtcNow.AddDays(-30), "teste"), dryRun: false);
            Assert.Equal((1, 1), (resultado.Eligible, resultado.Deleted));
            return 0;
        });
        Assert.Equal(0, Objetos("envmidiaprazo"));

        await e.Ia.SubirWorkerAsync();

        // O Sender não encontra o arquivo: a mensagem falha de forma visível, sem subida nem mensagem na Meta, e o histórico diz por quê.
        await e.EsperarEntregaAsync(a.Sessao, "Failed", autor: "Human", segundos: 40);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(e.Graph.Subidas);
        Assert.Equal(0, TodasAsMidias(e));
        var visao = (await MensagensDoAtendenteAsync(e, a)).Single()["attachment"]!;
        Assert.Equal(("Unavailable", "retention", "exame.png", "Segue"), (visao["status"]!.GetValue<string>(), visao["failureCode"]!.GetValue<string>(), visao["fileName"]!.GetValue<string>(), visao["caption"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, a, mensagem.ToString())).Status);
    }
}
