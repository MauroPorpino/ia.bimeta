using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// O envio de ARQUIVO (imagem, áudio e documento; ADR 0031): a subida (<c>POST .../media</c>, multipart) e a mensagem que a usa (<c>POST .../messages</c>), a forma exata dos dois pedidos, o token só nos cabeçalhos, o que o
/// adaptador recusa antes de chamar a Meta e, acima de tudo, a regra de efeito: a falha da SUBIDA nunca é ambígua (nenhuma mensagem saiu), e só a etapa da MENSAGEM pode terminar em dúvida (<c>Unknown</c>), sem reenvio.
/// HTTP simulado: nenhuma rede e nenhum token real.
/// </summary>
public sealed class MediaSendTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];

    private const string MediaId = "1166846181421424";

    private static ProviderMediaSendRequest Arquivo(
        ProviderMessageKind kind = ProviderMessageKind.Image,
        string mime = "image/png",
        string nome = "exame.png",
        byte[]? conteudo = null,
        string? legenda = "Segue a imagem",
        string token = Sends.Canary,
        string para = Sends.Recipient,
        string conta = Sends.PhoneNumberId,
        string? callback = Sends.Callback) =>
        new(token, conta, para, kind, mime, nome, conteudo ?? Png, legenda, callback);

    private static string UploadOk(string id = MediaId) => new JsonObject { ["id"] = id }.ToJsonString();

    /// <summary>A "Meta": a subida devolve o identificador; a mensagem, o wamid. Cada pedido pode ser trocado pelo teste.</summary>
    private static (WhatsAppCloudAdapter Adapter, FakeHandler Handler) Montar(
        Func<Captured, HttpResponseMessage>? aSubida = null, Func<Captured, HttpResponseMessage>? aMensagem = null, Action<WhatsAppOptions>? opcoes = null)
    {
        var (adaptador, handler, _) = Sends.Adapter(opcoes);
        handler.Behavior = (pedido, _) => Task.FromResult(
            pedido.Uri.AbsolutePath.EndsWith("/media", StringComparison.Ordinal)
                ? (aSubida ?? (_ => FakeHandler.Json(HttpStatusCode.OK, UploadOk())))(pedido)
                : (aMensagem ?? (_ => FakeHandler.Json(HttpStatusCode.OK, SendBodies.Ok())))(pedido));
        return (adaptador, handler);
    }

    // A forma dos dois pedidos

    [Fact]
    public async Task A_imagem_sobe_pelo_formulario_e_a_mensagem_usa_o_identificador_devolvido()
    {
        var (adaptador, handler) = Montar();

        var resultado = await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.Equal(2, handler.Requests.Count);
        var subida = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, subida.Method);
        Assert.Equal($"https://graph.facebook.com/v26.0/{Sends.PhoneNumberId}/media", subida.Uri.ToString());
        Assert.StartsWith("multipart/form-data", subida.ContentType);
        Assert.Contains("name=messaging_product", subida.Body);
        Assert.Contains("whatsapp", subida.Body);
        Assert.Contains("name=type", subida.Body);
        Assert.Contains("image/png", subida.Body);
        Assert.Contains("name=file", subida.Body);
        Assert.Contains("filename=upload.png", subida.Body);
        Assert.True(Contem(subida.Raw!, Png), "O conteúdo do arquivo deveria ir inteiro na parte do arquivo.");

        var mensagem = handler.Requests[1];
        Assert.Equal($"https://graph.facebook.com/v26.0/{Sends.PhoneNumberId}/messages", mensagem.Uri.ToString());
        var esperado = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Sends.Recipient,
            ["type"] = "image",
            ["image"] = new JsonObject { ["id"] = MediaId, ["caption"] = "Segue a imagem" },
            ["biz_opaque_callback_data"] = Sends.Callback,
        };
        Assert.True(JsonNode.DeepEquals(esperado, mensagem.Json), $"Corpo inesperado: {mensagem.Body}");
        Assert.Equal(ProviderSendOutcome.Accepted, resultado.Outcome);
        Assert.Equal(SendBodies.MessageId, resultado.ProviderMessageId);
    }

    [Fact]
    public async Task O_documento_leva_o_nome_e_a_legenda_e_o_nome_com_acento_nao_vai_no_cabecalho_da_subida()
    {
        var (adaptador, handler) = Montar();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");

        var resultado = await adaptador.SendMediaAsync(Arquivo(ProviderMessageKind.Document, "application/pdf", "Relatório de março.pdf", pdf, "Segue o laudo"), default);

        Assert.Equal(ProviderSendOutcome.Accepted, resultado.Outcome);
        var subida = handler.Requests[0];
        Assert.Contains("filename=upload.pdf", subida.Body);
        Assert.DoesNotContain("Relat", subida.Body); // o nome do cliente nunca vai ao cabeçalho do formulário

        var esperado = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Sends.Recipient,
            ["type"] = "document",
            ["document"] = new JsonObject { ["id"] = MediaId, ["filename"] = "Relatório de março.pdf", ["caption"] = "Segue o laudo" },
            ["biz_opaque_callback_data"] = Sends.Callback,
        };
        Assert.True(JsonNode.DeepEquals(esperado, handler.Requests[1].Json), $"Corpo inesperado: {handler.Requests[1].Body}");
    }

    [Fact]
    public async Task O_audio_vai_so_com_o_identificador_e_sem_legenda_e_o_documento_sem_legenda_nao_leva_o_campo()
    {
        var (adaptador, handler) = Montar();
        var ogg = new byte[] { 0x4F, 0x67, 0x67, 0x53, 0, 2, 0, 0 };

        await adaptador.SendMediaAsync(Arquivo(ProviderMessageKind.Audio, "audio/ogg", "nota.ogg", ogg, legenda: null), default);
        await adaptador.SendMediaAsync(Arquivo(ProviderMessageKind.Document, "text/plain", "aviso.txt", Encoding.UTF8.GetBytes("oi"), legenda: null), default);

        var audio = handler.Requests[1].Json;
        Assert.Equal("audio", audio["type"]!.GetValue<string>());
        Assert.Equal(new[] { "id" }, audio["audio"]!.AsObject().Select(p => p.Key).ToArray());

        var documento = handler.Requests[3].Json["document"]!.AsObject();
        Assert.Equal(new[] { "filename", "id" }, documento.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task O_token_vai_so_no_cabecalho_dos_dois_pedidos_nunca_no_corpo_na_url_nem_no_formulario()
    {
        var (adaptador, handler) = Montar();

        await adaptador.SendMediaAsync(Arquivo(token: "TOKEN-DO-TENANT-A"), default);

        Assert.All(handler.Requests, pedido =>
        {
            Assert.Equal("Bearer TOKEN-DO-TENANT-A", pedido.Authorization);
            Assert.DoesNotContain("TOKEN-DO-TENANT-A", pedido.Body);
            Assert.DoesNotContain("TOKEN-DO-TENANT-A", pedido.Uri.ToString());
            Assert.Equal("application/json", pedido.Accept);
        });
    }

    [Fact]
    public async Task O_endereco_e_a_versao_sao_configuracao_nas_duas_etapas()
    {
        var (adaptador, handler) = Montar(opcoes: o =>
        {
            o.BaseAddress = "http://127.0.0.1:5099/proxy/";
            o.GraphApiVersion = "v27.0";
        });

        await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.Equal(
            new[] { $"http://127.0.0.1:5099/proxy/v27.0/{Sends.PhoneNumberId}/media", $"http://127.0.0.1:5099/proxy/v27.0/{Sends.PhoneNumberId}/messages" },
            handler.Requests.Select(r => r.Uri.ToString()).ToArray());
    }

    // A subida nunca é ambígua

    [Theory]
    [InlineData(500, "upload-http-500")]
    [InlineData(502, "upload-http-502")]
    [InlineData(503, "upload-http-503")]
    public async Task Falha_do_servidor_na_subida_e_recusa_transitoria_porque_nenhuma_mensagem_saiu(int estado, string codigo)
    {
        var (adaptador, handler) = Montar(aSubida: _ => FakeHandler.Json((HttpStatusCode)estado, "{}"));

        var resultado = await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.Equal(ProviderSendOutcome.RejectedTransient, resultado.Outcome);
        Assert.Equal(codigo, resultado.ErrorCode);
        Assert.Single(handler.Requests); // a mensagem nem foi tentada
    }

    [Fact]
    public async Task Tempo_esgotado_ou_queda_de_rede_na_subida_e_recusa_transitoria_nunca_desconhecido()
    {
        var (adaptadorLento, handlerLento, _) = Sends.Adapter(o => o.MediaTimeout = TimeSpan.FromMilliseconds(100));
        handlerLento.Behavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return FakeHandler.Json(HttpStatusCode.OK, UploadOk());
        };

        var tempo = await adaptadorLento.SendMediaAsync(Arquivo(), default);

        var (adaptadorQueda, handlerQueda, _) = Sends.Adapter();
        handlerQueda.Behavior = (_, _) => throw new HttpRequestException("conexão recusada com o token EAAG-segredo");
        var queda = await adaptadorQueda.SendMediaAsync(Arquivo(), default);

        Assert.Equal((ProviderSendOutcome.RejectedTransient, "upload-timeout"), (tempo.Outcome, tempo.ErrorCode));
        Assert.Equal((ProviderSendOutcome.RejectedTransient, "upload-network-error"), (queda.Outcome, queda.ErrorCode));
        Assert.DoesNotContain("EAAG-segredo", queda.ErrorCode);
        Assert.Single(handlerLento.Requests);
        Assert.Single(handlerQueda.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("isto nao e json")]
    [InlineData("{\"id\": 123}")]
    [InlineData("{\"id\": \"com espaco\"}")]
    [InlineData("{\"id\": \"../../escapa\"}")]
    [InlineData("")]
    public async Task Sucesso_sem_um_identificador_valido_e_recusa_transitoria_e_nenhuma_mensagem_e_enviada(string corpo)
    {
        var (adaptador, handler) = Montar(aSubida: _ => FakeHandler.Json(HttpStatusCode.OK, corpo));

        var resultado = await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.Equal((ProviderSendOutcome.RejectedTransient, "upload-invalid-response"), (resultado.Outcome, resultado.ErrorCode));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_recusa_do_provedor_na_subida_mantem_o_desfecho_do_envio_e_prefixa_o_codigo()
    {
        // Tipo que não confere com o conteúdo (131053, a falha mais comum segundo a documentação): definitiva.
        var (a1, h1) = Montar(aSubida: _ => FakeHandler.Json(HttpStatusCode.BadRequest, SendBodies.Error(131053)));
        var tipoErrado = await a1.SendMediaAsync(Arquivo(), default);
        Assert.Equal((ProviderSendOutcome.RejectedPermanent, "upload-131053"), (tipoErrado.Outcome, tipoErrado.ErrorCode));
        Assert.Single(h1.Requests);

        // Limite de taxa da conta: transitória, com o escopo e a espera do provedor.
        var (a2, _) = Montar(aSubida: _ => FakeHandler.Json((HttpStatusCode)429, SendBodies.Error(130429), ("Retry-After", "7")));
        var taxa = await a2.SendMediaAsync(Arquivo(), default);
        Assert.Equal(ProviderSendOutcome.RejectedTransient, taxa.Outcome);
        Assert.Equal(ProviderRateLimitScope.Account, taxa.RateLimitScope);
        Assert.Equal(TimeSpan.FromSeconds(7), taxa.RetryAfter);

        // Credencial recusada: erro da CONTA (sem prefixo), que pausa a conta.
        var (a3, _) = Montar(aSubida: _ => FakeHandler.Json(HttpStatusCode.Unauthorized, SendBodies.Error(190)));
        var conta = await a3.SendMediaAsync(Arquivo(), default);
        Assert.Equal((ProviderSendOutcome.AccountError, "190"), (conta.Outcome, conta.ErrorCode));
    }

    [Fact]
    public async Task O_texto_do_provedor_nunca_aparece_no_resultado()
    {
        var (adaptador, _) = Montar(aSubida: _ => FakeHandler.Json(HttpStatusCode.BadRequest, SendBodies.Error(131053, "SEGREDO do cliente 5511999990001", "DETALHE com dados")));

        var resultado = await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.DoesNotContain("SEGREDO", resultado.ErrorCode);
        Assert.DoesNotContain("DETALHE", resultado.ErrorCode);
    }

    // Só a etapa da mensagem pode deixar dúvida, e a dúvida nunca é reenviada

    [Fact]
    public async Task Resposta_de_erro_do_servidor_na_mensagem_depois_da_subida_e_desconhecida_e_so_ha_dois_pedidos()
    {
        var (adaptador, handler) = Montar(aMensagem: _ => FakeHandler.Json(HttpStatusCode.InternalServerError, "{}"));

        var resultado = await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.Equal(ProviderSendOutcome.Unknown, resultado.Outcome);
        Assert.Equal(2, handler.Requests.Count); // um upload e uma tentativa de mensagem: o adaptador nunca repete
    }

    [Fact]
    public async Task Tempo_esgotado_na_mensagem_depois_da_subida_e_desconhecido()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.RequestTimeout = TimeSpan.FromMilliseconds(100));
        handler.Behavior = async (pedido, ct) =>
        {
            if (pedido.Uri.AbsolutePath.EndsWith("/media", StringComparison.Ordinal))
            {
                return FakeHandler.Json(HttpStatusCode.OK, UploadOk());
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return FakeHandler.Json(HttpStatusCode.OK, SendBodies.Ok());
        };

        var resultado = await adaptador.SendMediaAsync(Arquivo(), default);

        Assert.Equal((ProviderSendOutcome.Unknown, "timeout"), (resultado.Outcome, resultado.ErrorCode));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_recusa_da_mensagem_tem_o_mesmo_desfecho_do_texto_livre()
    {
        var (janela, _) = Montar(aMensagem: _ => FakeHandler.Json(HttpStatusCode.BadRequest, SendBodies.Error(131047)));
        Assert.Equal(ProviderSendOutcome.WindowClosed, (await janela.SendMediaAsync(Arquivo(), default)).Outcome);

        var (permanente, _) = Montar(aMensagem: _ => FakeHandler.Json(HttpStatusCode.BadRequest, SendBodies.Error(131026)));
        var recusa = await permanente.SendMediaAsync(Arquivo(), default);
        Assert.Equal((ProviderSendOutcome.RejectedPermanent, "131026"), (recusa.Outcome, recusa.ErrorCode));
    }

    [Fact]
    public async Task O_cancelamento_do_chamador_propaga_na_subida_e_na_mensagem()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        using var origem = new CancellationTokenSource();
        handler.Behavior = async (_, ct) =>
        {
            await origem.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return FakeHandler.Json(HttpStatusCode.OK, UploadOk());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adaptador.SendMediaAsync(Arquivo(), origem.Token));
    }

    // O que é recusado antes de chamar a Meta

    public static TheoryData<string, ProviderMediaSendRequest, ProviderSendOutcome, string> Recusas => new()
    {
        { "token", Arquivo(token: "token com espaco"), ProviderSendOutcome.AccountError, "credential-invalid-format" },
        { "conta", Arquivo(conta: "../x"), ProviderSendOutcome.AccountError, "invalid-account-id" },
        { "destinatario", Arquivo(para: "abc"), ProviderSendOutcome.RejectedPermanent, "invalid-recipient" },
        { "tipo-de-midia", Arquivo(kind: ProviderMessageKind.Video), ProviderSendOutcome.RejectedPermanent, "media-kind-invalid" },
        { "texto", Arquivo(kind: ProviderMessageKind.Text), ProviderSendOutcome.RejectedPermanent, "media-kind-invalid" },
        { "mime", Arquivo(mime: "image/png; x=1"), ProviderSendOutcome.RejectedPermanent, "media-type-invalid" },
        { "vazio", Arquivo(conteudo: []), ProviderSendOutcome.RejectedPermanent, "media-size-invalid" },
        { "nome-vazio", Arquivo(nome: "  "), ProviderSendOutcome.RejectedPermanent, "media-name-invalid" },
        { "nome-com-controle", Arquivo(nome: "a\nb.png"), ProviderSendOutcome.RejectedPermanent, "media-name-invalid" },
        { "audio-com-legenda", Arquivo(ProviderMessageKind.Audio, "audio/ogg", "a.ogg", legenda: "oi"), ProviderSendOutcome.RejectedPermanent, "caption-not-supported" },
        { "legenda-longa", Arquivo(legenda: new string('a', 1025)), ProviderSendOutcome.RejectedPermanent, "caption-too-long" },
        { "dado-opaco-longo", Arquivo(callback: new string('c', 513)), ProviderSendOutcome.RejectedPermanent, "callback-data-too-long" },
    };

    [Theory]
    [MemberData(nameof(Recusas))]
    public async Task O_que_o_adaptador_prova_errado_e_recusado_sem_chamar_a_Meta_e_sem_repetir_o_valor(string caso, ProviderMediaSendRequest pedido, ProviderSendOutcome desfecho, string codigo)
    {
        var (adaptador, handler) = Montar();

        var resultado = await adaptador.SendMediaAsync(pedido, default);

        Assert.Equal((desfecho, codigo), (resultado.Outcome, resultado.ErrorCode));
        Assert.Empty(handler.Requests);
        Assert.False(caso.Length == 0);
    }

    private static bool Contem(byte[] palheiro, byte[] agulha) => palheiro.AsSpan().IndexOf(agulha) >= 0;
}
