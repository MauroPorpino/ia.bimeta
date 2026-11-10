using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A segurança dos arquivos (ADR 0032) de ponta a ponta, com Api, Worker e PostgreSQL reais (Meta e OpenAI simuladas, verificador de código malicioso = um clamd FALSO que fala o protocolo de verdade): com a verificação
/// exigida e a cifra em repouso ligadas, o arquivo recebido só fica disponível depois do veredito limpo, o verificador fora do ar nunca libera (nova tentativa, depois "indisponível" com o motivo), o Worker que cai com a
/// varredura em andamento é substituído sem duplicar nada, o infectado nunca abre, a cota por empresa vale para o que o cliente e o atendente enviam, e as chaves de configuração ligam tudo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SegurancaDeAnexosEmOperacaoTests(PostgresFixture banco) : IAsyncDisposable
{
    private const string Cliente = "5511999990091";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];

    private readonly List<ClamdFalso> _clamds = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var clamd in _clamds)
        {
            await clamd.DisposeAsync();
        }
    }

    private ClamdFalso Subir(ModoDoClamd modo)
    {
        var clamd = new ClamdFalso(modo);
        _clamds.Add(clamd);
        return clamd;
    }

    private string Pasta(string prefixo) => Path.Combine(banco.PastaDeAnexos, prefixo);

    private int Objetos(string prefixo) => Directory.Exists(Pasta(prefixo)) ? Directory.EnumerateFiles(Pasta(prefixo), "*.bin", SearchOption.AllDirectories).Count() : 0;

    private static Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> Transfere(string motivo) =>
        (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir(motivo), 150, 0, 15, "req_segmidia"));

    /// <summary>O ensaio com a verificação EXIGIDA (o clamd falso), a cifra em repouso EXIGIDA e a pasta própria, igual para o Api e o Worker.</summary>
    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, ClamdFalso clamd, Dictionary<string, string?>? extra = null, string? chaveDeCifra = null)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["Conversations:Attachments:RootPath"] = Pasta(prefixo),
            ["Conversations:Attachments:Scanner:Mode"] = "ClamAv",
            ["Conversations:Attachments:Scanner:Host"] = "127.0.0.1",
            ["Conversations:Attachments:Scanner:Port"] = clamd.Porta.ToString(CultureInfo.InvariantCulture),
            ["Conversations:Attachments:Scanner:Timeout"] = "00:00:01",
            ["Conversations:Attachments:Scanner:Required"] = "true",
            ["Conversations:Attachments:Encryption:ActiveKeyId"] = "k1",
            ["Conversations:Attachments:Encryption:Keys:k1"] = chaveDeCifra ?? AnexosDeTeste.NovaChave(),
            ["Conversations:Attachments:Encryption:Required"] = "true",
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
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, Transfere("price"));
        return e;
    }

    /// <summary>Uma conversa que a IA transferiu e o atendente assumiu: o estado em que o cliente manda o arquivo e o atendente o vê.</summary>
    private static async Task<(string Token, string ItemId)> ConversaAssumidaAsync(EnsaioDeWhatsApp e, string waId = Cliente, string nome = "Mauro")
    {
        var (_, token) = await e.NovoAtendenteAsync();
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeTexto($"wamid.INI{Guid.NewGuid():N}"[..20], waId, nome, "Quanto custa o hemograma?")));
        var sessao = await e.EsperarSessaoAsync(nome);
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
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "o aviso da transferência deveria sair pelo WhatsApp");
        return (token, itemId!);
    }

    private static async Task<IReadOnlyList<JsonNode>> MensagensAsync(EnsaioDeWhatsApp e, string token, string itemId) =>
        (await e.Ia.Api.LerAsync<JsonNode>(token, $"/contact-center/items/{itemId}/messages?take=100"))["items"]!.AsArray().Select(m => m!).ToList();

    private static async Task<string> IdDoRecebidoAsync(EnsaioDeWhatsApp e, string token, string itemId, int ordem)
    {
        string? id = null;
        await e.EsperarAsync(
            async () =>
            {
                var recebidos = (await MensagensAsync(e, token, itemId)).Where(m => m["attachment"] is not null && m["direction"]!.GetValue<string>() == "Inbound").ToList();
                id = recebidos.Count > ordem ? recebidos[ordem]["id"]!.GetValue<string>() : null;
                return id is not null;
            },
            $"o arquivo recebido {ordem} deveria estar no histórico");
        return id!;
    }

    private static async Task<JsonNode?> AnexoAsync(EnsaioDeWhatsApp e, string token, string itemId, string mensagem) =>
        (await MensagensAsync(e, token, itemId)).Single(m => m["id"]!.GetValue<string>() == mensagem)["attachment"];

    private static async Task EsperarEstadoAsync(EnsaioDeWhatsApp e, string token, string itemId, string mensagem, string estado, int segundos = 40, Func<Task>? aCadaTentativa = null) =>
        await EnsaioDeIa.AguardarAsync(
            async () =>
            {
                if (aCadaTentativa is not null)
                {
                    await aCadaTentativa();
                }

                return (await AnexoAsync(e, token, itemId, mensagem))?["status"]?.GetValue<string>() == estado;
            },
            $"o arquivo deveria ficar em {estado}",
            segundos);

    private static async Task<(HttpStatusCode Status, byte[] Bytes)> BaixarAsync(EnsaioDeWhatsApp e, string token, string itemId, string mensagem)
    {
        using var pedido = e.Ia.Api.RequisicaoDe(token, HttpMethod.Get, $"/contact-center/items/{itemId}/messages/{mensagem}/attachment");
        using var resposta = await e.Ia.Api.Client.SendAsync(pedido);
        return (resposta.StatusCode, await resposta.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Dá a nova tentativa por vencida: a espera de 2 minutos entre as tentativas da busca de mídia é de verdade, e o teste adianta o relógio do evento no banco (nada além do horário da próxima tentativa muda).</summary>
    private static Task AdiantarNovaTentativaAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelsDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE channels.outbox_message SET next_attempt_at = now() WHERE event_type = 'channels.media-fetch-requested' AND state = 1 AND tenant_id = {0}",
            sp.GetRequiredService<IRequestContextAccessor>().GetRequired().TenantId.Value));

    private static Task<List<MessageAttachment>> LinhasAsync(EnsaioDeWhatsApp e) =>
        e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ConversationsDbContext>().Set<MessageAttachment>().AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync());

    private string ArquivoDoUnicoObjeto(string prefixo) => Assert.Single(Directory.EnumerateFiles(Pasta(prefixo), "*.bin", SearchOption.AllDirectories));

    // O verificador fora do ar e a nova tentativa

    [Fact]
    public async Task Verificador_fora_do_ar_deixa_o_arquivo_pendente_e_sem_objeto_e_ao_voltar_a_nova_tentativa_o_libera_cifrado_em_disco()
    {
        var clamd = Subir(ModoDoClamd.NaoResponde);
        var chave = AnexosDeTeste.NovaChave();
        await using var e = await IniciarAsync("segmidia1", clamd, chaveDeCifra: chave);
        var (token, itemId) = await ConversaAssumidaAsync(e);
        var midia = e.Graph.RegistrarMidia("8008001", Png, "image/png");

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.SEG0001", Cliente, "Mauro", "8008001", "image/png")));
        var id = await IdDoRecebidoAsync(e, token, itemId, 0);
        await e.EsperarAsync(async () => clamd.Recebidos.Count >= 1, "o arquivo deveria chegar ao verificador");
        await Task.Delay(TimeSpan.FromSeconds(2)); // o prazo do verificador (1 s) estoura: indisponível, nunca limpo

        // Nada liberado: o arquivo segue pendente, não há objeto guardado e o atendente não abre.
        Assert.Equal("Pending", (await AnexoAsync(e, token, itemId, id))!["status"]!.GetValue<string>());
        Assert.Equal(0, Objetos("segmidia1"));
        Assert.NotEqual(HttpStatusCode.OK, (await BaixarAsync(e, token, itemId, id)).Status);

        // O verificador volta; a nova tentativa vence a espera e conclui: um arquivo, um objeto, cifrado em disco, marcado como verificado.
        clamd.Modo = ModoDoClamd.Ok;
        await EsperarEstadoAsync(e, token, itemId, id, "Available", segundos: 60, aCadaTentativa: () => AdiantarNovaTentativaAsync(e));
        Assert.Equal(Png, (await BaixarAsync(e, token, itemId, id)).Bytes);
        Assert.Equal(2, midia.Downloads); // uma busca por tentativa: o arquivo nunca ficou guardado enquanto não verificado
        var objeto = await File.ReadAllBytesAsync(ArquivoDoUnicoObjeto("segmidia1"));
        Assert.True(EncryptedAttachmentContentStore.HasEncryptedHeader(objeto));
        Assert.Equal(-1, objeto.AsSpan().IndexOf("IHDR"u8)); // o texto claro do arquivo nunca está no disco
        var linha = Assert.Single(await LinhasAsync(e), l => l.Direction == MessageDirection.Inbound);
        Assert.Equal((AttachmentStatus.Available, AttachmentScanStatus.Clean, (long?)Png.Length), (linha.Status, linha.ScanStatus, linha.SizeBytes));
        Assert.Contains(clamd.Recebidos, r => r.Conteudo.SequenceEqual(Png)); // o verificador viu o arquivo inteiro

        // Nem o token da conta nem a chave de cifra foram para um log.
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(EnsaioDeWhatsApp.TokenDeAcesso, StringComparison.Ordinal) || l.Contains(chave, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verificador_que_continua_fora_do_ar_esgota_as_tentativas_e_o_arquivo_fica_indisponivel_com_o_motivo_nunca_liberado()
    {
        var clamd = Subir(ModoDoClamd.FecharSemResponder);
        await using var e = await IniciarAsync("segmidia2", clamd, new Dictionary<string, string?>
        {
            ["Messaging:MaxAttempts"] = "2",
            ["Messaging:DeadLetterSweepInterval"] = "00:00:01",
        });
        var (token, itemId) = await ConversaAssumidaAsync(e);
        e.Graph.RegistrarMidia("8008002", Png, "image/png");

        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.SEG0002", Cliente, "Mauro", "8008002", "image/png")));
        var id = await IdDoRecebidoAsync(e, token, itemId, 0);

        await EsperarEstadoAsync(e, token, itemId, id, "Unavailable", segundos: 90, aCadaTentativa: () => AdiantarNovaTentativaAsync(e));

        var visao = (await AnexoAsync(e, token, itemId, id))!;
        Assert.Equal("scan-unavailable", visao["failureCode"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, id)).Status);
        Assert.Equal(0, Objetos("segmidia2"));
        var linha = Assert.Single(await LinhasAsync(e), l => l.Direction == MessageDirection.Inbound);
        Assert.Equal((AttachmentStatus.Unavailable, (string?)null), (linha.Status, linha.StorageKey));
    }

    // O Worker que cai e o infectado

    [Fact]
    public async Task Worker_que_cai_com_a_varredura_em_andamento_o_novo_conclui_uma_unica_vez_e_o_infectado_nunca_abre_nem_e_guardado()
    {
        var clamd = Subir(ModoDoClamd.NaoResponde);
        await using var e = await IniciarAsync("segmidia3", clamd, new Dictionary<string, string?>
        {
            ["Messaging:LeaseDuration"] = "00:00:03",
            ["Conversations:Attachments:Scanner:Timeout"] = "00:00:30",
        });
        var (token, itemId) = await ConversaAssumidaAsync(e);
        e.Graph.RegistrarMidia("8008003", Png, "image/png");

        // O verificador recebe o arquivo e o segura: o Worker cai com a varredura em andamento.
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.SEG0003", Cliente, "Mauro", "8008003", "image/png")));
        var id = await IdDoRecebidoAsync(e, token, itemId, 0);
        await e.EsperarAsync(async () => clamd.Recebidos.Count == 1, "a varredura deveria estar em andamento");
        Assert.Equal("Pending", (await AnexoAsync(e, token, itemId, id))!["status"]!.GetValue<string>());
        Assert.Equal(0, Objetos("segmidia3"));

        clamd.Modo = ModoDoClamd.Ok;
        await e.Ia.ReiniciarWorkerAsync();

        // O novo Worker acha o evento com o lease vencido e conclui: um arquivo, um objeto.
        await EsperarEstadoAsync(e, token, itemId, id, "Available", segundos: 60);
        Assert.Equal(Png, (await BaixarAsync(e, token, itemId, id)).Bytes);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Objetos("segmidia3"));
        Assert.Single(await MensagensAsync(e, token, itemId), m => m["attachment"] is not null);

        // O verificador acha código malicioso em outro arquivo: recusado como malware, nunca guardado, nunca aberto.
        clamd.Modo = ModoDoClamd.Found;
        e.Graph.RegistrarMidia("8008004", Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.SEG0004", Cliente, "Mauro", "8008004", "image/png")));
        var infectado = await IdDoRecebidoAsync(e, token, itemId, 1);
        await EsperarEstadoAsync(e, token, itemId, infectado, "Rejected");
        Assert.Equal("malware", (await AnexoAsync(e, token, itemId, infectado))!["failureCode"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, infectado)).Status);
        Assert.Equal(1, Objetos("segmidia3"));
    }

    // A cota por empresa e o envio do atendente

    [Fact]
    public async Task A_cota_vale_para_o_que_o_cliente_recebe_e_o_atendente_envia_e_o_verificador_decide_o_que_o_atendente_pode_enviar()
    {
        var clamd = Subir(ModoDoClamd.Ok);
        await using var e = await IniciarAsync("segmidia4", clamd, new Dictionary<string, string?> { ["Conversations:Attachments:TenantQuotaBytes"] = ((2 * Png.Length) + 5).ToString(CultureInfo.InvariantCulture) });
        var (token, itemId) = await ConversaAssumidaAsync(e);

        // O verificador manda: infectado e fora do ar são recusados no envio do atendente (nada é guardado nem sobe à Meta).
        clamd.Modo = ModoDoClamd.Found;
        var infectado = await e.EnviarArquivoAsync(token, itemId, Png, "exame.png", "image/png", "Segue");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, infectado.Status);
        Assert.Equal(("MediaRejected", "malware"), (infectado.Corpo!["code"]!.GetValue<string>(), infectado.Corpo["reason"]!.GetValue<string>()));
        clamd.Modo = ModoDoClamd.FecharSemResponder;
        var fora = await e.EnviarArquivoAsync(token, itemId, Png, "exame.png", "image/png", "Segue");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, fora.Status);
        Assert.Equal(("MediaRejected", "scanner-unavailable"), (fora.Corpo!["code"]!.GetValue<string>(), fora.Corpo["reason"]!.GetValue<string>()));
        Assert.Equal(0, Objetos("segmidia4"));
        Assert.Empty(e.Graph.Subidas);

        // A cota de 2 arquivos (e mais 5 bytes): o do cliente (1) e o do atendente (2) cabem; o seguinte, de qualquer lado, não.
        clamd.Modo = ModoDoClamd.Ok;
        e.Graph.RegistrarMidia("8008005", Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.SEG0005", Cliente, "Mauro", "8008005", "image/png")));
        var recebido = await IdDoRecebidoAsync(e, token, itemId, 0);
        await EsperarEstadoAsync(e, token, itemId, recebido, "Available");
        var enviado = await e.EnviarArquivoAsync(token, itemId, Png, "exame.png", "image/png", "Segue");
        Assert.Equal((HttpStatusCode.OK, "Applied"), (enviado.Status, enviado.Corpo!["outcome"]!.GetValue<string>()));

        var recusado = await e.EnviarArquivoAsync(token, itemId, Png, "outro.png", "image/png", "Mais um");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, recusado.Status);
        Assert.Equal(("MediaRejected", "quota-exceeded"), (recusado.Corpo!["code"]!.GetValue<string>(), recusado.Corpo["reason"]!.GetValue<string>()));

        e.Graph.RegistrarMidia("8008006", Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(e.PayloadDeMidia("image", "wamid.SEG0006", Cliente, "Mauro", "8008006", "image/png")));
        var excedente = await IdDoRecebidoAsync(e, token, itemId, 1);
        await EsperarEstadoAsync(e, token, itemId, excedente, "Unavailable");
        Assert.Equal("quota-exceeded", (await AnexoAsync(e, token, itemId, excedente))!["failureCode"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await BaixarAsync(e, token, itemId, excedente)).Status);

        // Só os dois que couberam estão guardados (cifrados), e o arquivo do atendente chegou à Meta uma única vez.
        Assert.Equal(2, Objetos("segmidia4"));
        await e.EsperarAsync(async () => e.Graph.Subidas.Count == 1, "o arquivo do atendente deveria subir à Meta");
        Assert.Equal(Png, e.Graph.Subidas[0].Conteudo);
        Assert.Equal(Png, (await BaixarAsync(e, token, itemId, recebido)).Bytes);
    }

    // A configuração

    [Fact]
    public void As_chaves_de_configuracao_ligam_a_cota_o_verificador_e_a_cifra_e_a_configuracao_contraditoria_impede_a_partida()
    {
        var chave = AnexosDeTeste.NovaChave();
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conversations:Attachments:TenantQuotaBytes"] = "1048576",
            ["Conversations:Attachments:Scanner:Mode"] = "ClamAv",
            ["Conversations:Attachments:Scanner:Host"] = "10.0.0.5",
            ["Conversations:Attachments:Scanner:Port"] = "3311",
            ["Conversations:Attachments:Scanner:Timeout"] = "00:00:45",
            ["Conversations:Attachments:Scanner:Required"] = "true",
            ["Conversations:Attachments:Encryption:ActiveKeyId"] = "k2",
            ["Conversations:Attachments:Encryption:Keys:k1"] = AnexosDeTeste.NovaChave(),
            ["Conversations:Attachments:Encryption:Keys:k2"] = chave,
            ["Conversations:Attachments:Encryption:Required"] = "true",
        }).Build();

        var opcoes = new AttachmentOptions();
        configuracao.GetSection("Conversations:Attachments").Bind(opcoes);
        opcoes.Validate();

        Assert.Equal(1_048_576, opcoes.TenantQuotaBytes);
        Assert.Equal((AttachmentScannerMode.ClamAv, "10.0.0.5", 3311, TimeSpan.FromSeconds(45), true), (opcoes.Scanner.Mode, opcoes.Scanner.Host, opcoes.Scanner.Port, opcoes.Scanner.Timeout, opcoes.Scanner.Required));
        Assert.Equal(("k2", 2, true), (opcoes.Encryption.ActiveKeyId, opcoes.Encryption.Keys.Count, opcoes.Encryption.Required));

        // Exigir a verificação sem verificador, ou a cifra sem chave, não sobe: falha fechada, e a mensagem nunca repete uma chave.
        var semVerificador = new AttachmentOptions { Scanner = { Required = true } };
        Assert.Throws<InvalidOperationException>(semVerificador.Validate);
        var semChave = new AttachmentOptions { Encryption = { Required = true } };
        var erro = Assert.Throws<InvalidOperationException>(semChave.Validate);
        Assert.DoesNotContain(chave, erro.Message, StringComparison.Ordinal);
    }
}
