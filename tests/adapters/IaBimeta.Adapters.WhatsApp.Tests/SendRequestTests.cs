using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// O PEDIDO de envio de texto livre (ADR 0011, B.2 e E): endereço, forma exata do corpo, o token só no cabeçalho DESTA requisição, o que é recusado antes de chamar o provedor e o sucesso com o identificador da
/// mensagem. HTTP simulado: nenhuma rede e nenhum token real.
/// </summary>
public sealed class SendRequestTests
{
    [Fact]
    public async Task O_pedido_vai_para_a_Graph_API_na_versao_configurada_com_a_forma_exata_do_corpo()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        var pedido = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, pedido.Method);
        Assert.Equal($"https://graph.facebook.com/v26.0/{Sends.PhoneNumberId}/messages", pedido.Uri.ToString());
        Assert.Equal("application/json", pedido.Accept);
        Assert.StartsWith("application/json", pedido.ContentType);

        var esperado = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Sends.Recipient,
            ["type"] = "text",
            ["text"] = new JsonObject { ["preview_url"] = false, ["body"] = "Olá! Você precisa de jejum de 8 horas." },
            ["biz_opaque_callback_data"] = Sends.Callback,
        };
        Assert.True(JsonNode.DeepEquals(esperado, pedido.Json), $"Corpo inesperado: {pedido.Body}");
        Assert.Equal(ProviderSendOutcome.Accepted, resultado.Outcome);
    }

    [Fact]
    public async Task O_corpo_so_tem_os_campos_do_contrato_e_sem_dado_opaco_o_campo_nao_vai()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        await adaptador.SendTextAsync(Sends.Request(callback: null), default);
        await adaptador.SendTextAsync(Sends.Request(callback: string.Empty), default);

        Assert.All(handler.Requests, pedido =>
        {
            var corpo = pedido.Json.AsObject();
            Assert.Equal(new[] { "messaging_product", "recipient_type", "text", "to", "type" }, corpo.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Null(corpo["biz_opaque_callback_data"]);
        });
    }

    [Fact]
    public async Task A_versao_e_o_endereco_sao_configuracao()
    {
        var (adaptador, handler, _) = Sends.Adapter(o =>
        {
            o.BaseAddress = "http://127.0.0.1:5099/proxy/";
            o.GraphApiVersion = "v27.0";
        });

        await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal($"http://127.0.0.1:5099/proxy/v27.0/{Sends.PhoneNumberId}/messages", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task O_token_vai_so_no_cabecalho_desta_requisicao_nunca_no_corpo_na_url_nem_no_cliente_compartilhado()
    {
        var (adaptador, handler, http) = Sends.Adapter();

        await adaptador.SendTextAsync(Sends.Request(token: "TOKEN-DO-TENANT-A"), default);
        await adaptador.SendTextAsync(Sends.Request(token: "TOKEN-DO-TENANT-B"), default);

        Assert.Equal(new[] { "Bearer TOKEN-DO-TENANT-A", "Bearer TOKEN-DO-TENANT-B" }, handler.Requests.Select(r => r.Authorization).ToArray());
        Assert.All(handler.Requests, pedido =>
        {
            Assert.DoesNotContain("TOKEN-DO-TENANT", pedido.Body);
            Assert.DoesNotContain("TOKEN-DO-TENANT", pedido.Uri.ToString());
        });

        // O cliente HTTP compartilhado não guarda credencial alguma: duas contas nunca se misturam.
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Texto_acentuado_e_emoji_chegam_intactos_ao_provedor()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        const string Texto = "Atenção: coração, ação, “aspas”, ç e 😀 — fim.\nSegunda linha";

        await adaptador.SendTextAsync(Sends.Request(text: Texto), default);

        Assert.Equal(Texto, (string?)Assert.Single(handler.Requests).Json["text"]!["body"]);
    }

    [Fact]
    public async Task Substituto_sem_par_no_texto_vira_o_caractere_de_substituicao_e_o_envio_segue()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(text: "oi \ud800 tchau \udc00!"), default);

        Assert.Equal(ProviderSendOutcome.Accepted, resultado.Outcome);
        Assert.Equal("oi � tchau �!", (string?)Assert.Single(handler.Requests).Json["text"]!["body"]);
    }

    // Sucesso

    [Fact]
    public async Task Sucesso_devolve_o_identificador_da_mensagem_do_provedor_e_nenhum_codigo_de_erro()
    {
        var (adaptador, _, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Accepted, SendBodies.MessageId, null), resultado);
        Assert.Null(resultado.RetryAfter);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("não é JSON")]
    [InlineData("")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"messages\":null}")]
    [InlineData("{\"messages\":[{}]}")]
    [InlineData("{\"messages\":[{\"id\":null}]}")]
    [InlineData("{\"messages\":[{\"id\":42}]}")]
    [InlineData("{\"messages\":[{\"id\":\"\"}]}")]
    [InlineData("{\"messages\":[{\"id\":\"com espaco\"}]}")]
    [InlineData("{\"messages\":[\"wamid.X\"]}")]
    [InlineData("{\"error\":{\"message\":\"x\",\"code\":131047}}")]
    public async Task Resposta_2xx_sem_identificador_legivel_e_desconhecida_porque_a_mensagem_pode_ter_sido_enviada(string corpo)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.OK, corpo);

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "invalid-response"), resultado);
    }

    [Fact]
    public async Task Identificador_gigante_no_sucesso_e_resposta_ilegivel()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.OK, SendBodies.Ok(new string('x', 513)));

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(ProviderSendOutcome.Unknown, resultado.Outcome);
    }

    // Recusa antes de chamar o provedor

    [Theory]
    [InlineData("")]
    [InlineData("token com espaco")]
    [InlineData("token\ncom\nquebra")]
    [InlineData("token\r\nX-Injetado: 1")]
    [InlineData("tokém-não-ascii")]
    [InlineData("\t")]
    public async Task Token_fora_do_formato_e_erro_de_conta_recusado_sem_enviar_e_sem_repetir_o_valor(string token)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(token: token), default);

        Assert.Empty(handler.Requests);
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.AccountError, null, "credential-invalid-format"), resultado);

        // O valor recusado nunca volta no resultado.
        if (token.Trim().Length > 2)
        {
            Assert.DoesNotContain(token, resultado.ToString());
        }
    }

    [Fact]
    public async Task Token_gigante_e_recusado_sem_enviar()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(token: new string('a', 4097)), default);

        Assert.Empty(handler.Requests);
        Assert.Equal(ProviderSendOutcome.AccountError, resultado.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../123")]
    [InlineData("123/../456")]
    [InlineData("123?x=1")]
    [InlineData("12 3")]
    [InlineData("123#frag")]
    [InlineData("%31%32")]
    public async Task Phone_Number_ID_que_nao_e_numerico_e_recusado_para_nao_injetar_caminho(string phone)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(phone: phone), default);

        Assert.Empty(handler.Requests);
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.AccountError, null, "invalid-account-id"), resultado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("+5511999990001")]
    [InlineData("55 11 99999")]
    [InlineData("abc5511999")]
    [InlineData("551199999000112345678901")]
    public async Task Destinatario_fora_do_formato_wa_id_e_recusado_sem_enviar(string para)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(to: para), default);

        Assert.Empty(handler.Requests);
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "invalid-recipient"), resultado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task Texto_vazio_e_recusado_sem_enviar(string texto)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTextAsync(Sends.Request(text: texto), default);

        Assert.Empty(handler.Requests);
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "text-empty"), resultado);
    }

    [Fact]
    public async Task O_limite_de_4096_caracteres_conta_pontos_de_codigo()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        // 4096 caracteres: enviado. 4097: recusado. Emojis (dois chars UTF-16 cada) contam UM caractere.
        Assert.Equal(ProviderSendOutcome.Accepted, (await adaptador.SendTextAsync(Sends.Request(text: new string('a', 4096)), default)).Outcome);
        Assert.Equal(ProviderSendOutcome.Accepted, (await adaptador.SendTextAsync(Sends.Request(text: string.Concat(Enumerable.Repeat("😀", 4096))), default)).Outcome);
        Assert.Equal(2, handler.Requests.Count);

        var longo = await adaptador.SendTextAsync(Sends.Request(text: new string('a', 4097)), default);
        var longoDeEmojis = await adaptador.SendTextAsync(Sends.Request(text: string.Concat(Enumerable.Repeat("😀", 4097))), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "text-too-long"), longo);
        Assert.Equal(ProviderSendOutcome.RejectedPermanent, longoDeEmojis.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task O_dado_opaco_tem_ate_512_caracteres()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        Assert.Equal(ProviderSendOutcome.Accepted, (await adaptador.SendTextAsync(Sends.Request(callback: new string('c', 512)), default)).Outcome);
        var longo = await adaptador.SendTextAsync(Sends.Request(callback: new string('c', 513)), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "callback-data-too-long"), longo);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Pedido_nulo_e_erro_de_programacao()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sends.Plain().SendTextAsync(null!, default));
    }
}
