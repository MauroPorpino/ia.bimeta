using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// O envio de um MODELO de mensagem aprovado (ADR 0027): a forma exata do corpo <c>type: "template"</c> (cabeçalho, corpo e botão de link, parâmetros posicionais e nomeados), o que o adaptador recusa antes de chamar
/// a Meta e a leitura da resposta (a mesma do texto livre). HTTP simulado: nenhuma rede e nenhum token real.
/// </summary>
public sealed class TemplateSendTests
{
    private static ProviderTemplateSendRequest Template(
        IReadOnlyList<ProviderTemplateParameter>? parametros = null,
        string nome = "resultado_pronto",
        string idioma = "pt_BR",
        string token = Sends.Canary,
        string para = Sends.Recipient,
        string? callback = Sends.Callback) =>
        new(token, Sends.PhoneNumberId, para, nome, idioma, parametros ?? Array.Empty<ProviderTemplateParameter>(), callback);

    [Fact]
    public void A_janela_de_resposta_livre_do_WhatsApp_e_de_24_horas()
    {
        Assert.Equal(TimeSpan.FromHours(24), Sends.Plain().ReplyWindow);
    }

    [Fact]
    public async Task O_modelo_sem_parametros_vai_com_nome_e_idioma_e_sem_componentes()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTemplateAsync(Template(), default);

        var pedido = Assert.Single(handler.Requests);
        Assert.Equal($"https://graph.facebook.com/v26.0/{Sends.PhoneNumberId}/messages", pedido.Uri.ToString());
        var esperado = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Sends.Recipient,
            ["type"] = "template",
            ["template"] = new JsonObject { ["name"] = "resultado_pronto", ["language"] = new JsonObject { ["code"] = "pt_BR" } },
            ["biz_opaque_callback_data"] = Sends.Callback,
        };
        Assert.True(JsonNode.DeepEquals(esperado, pedido.Json), $"Corpo inesperado: {pedido.Body}");
        Assert.Equal(ProviderSendOutcome.Accepted, resultado.Outcome);
        Assert.Equal(SendBodies.MessageId, resultado.ProviderMessageId);
    }

    [Fact]
    public async Task Cabecalho_corpo_e_botao_de_link_vao_em_componentes_na_ordem_dos_indices()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        var parametros = new[]
        {
            // Fora de ordem de propósito: o adaptador ordena pelo índice.
            new ProviderTemplateParameter("body", 2, null, "hemograma"),
            new ProviderTemplateParameter("button", 0, null, "abc123"),
            new ProviderTemplateParameter("body", 1, null, "Mauro"),
            new ProviderTemplateParameter("header", 1, null, "Resultado"),
        };

        await adaptador.SendTemplateAsync(Template(parametros), default);

        var componentes = Assert.Single(handler.Requests).Json["template"]!["components"]!.AsArray();
        var esperado = new JsonArray(
            new JsonObject { ["type"] = "header", ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Resultado" }) },
            new JsonObject
            {
                ["type"] = "body",
                ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Mauro" }, new JsonObject { ["type"] = "text", ["text"] = "hemograma" }),
            },
            new JsonObject { ["type"] = "button", ["sub_type"] = "url", ["index"] = "0", ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "abc123" }) });
        Assert.True(JsonNode.DeepEquals(esperado, componentes), $"Componentes inesperados: {componentes.ToJsonString()}");
    }

    [Fact]
    public async Task Parametro_nomeado_leva_o_parameter_name()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        await adaptador.SendTemplateAsync(Template(new[] { new ProviderTemplateParameter("body", 1, "nome", "Mauro") }), default);

        var parametro = Assert.Single(handler.Requests).Json["template"]!["components"]![0]!["parameters"]![0]!;
        Assert.Equal("nome", parametro["parameter_name"]!.GetValue<string>());
        Assert.Equal("Mauro", parametro["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task O_token_vai_so_no_cabecalho_e_o_valor_do_parametro_nao_vira_url()
    {
        var (adaptador, handler, http) = Sends.Adapter();

        await adaptador.SendTemplateAsync(Template(new[] { new ProviderTemplateParameter("body", 1, null, "Mauro") }), default);

        var pedido = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {Sends.Canary}", pedido.Authorization);
        Assert.DoesNotContain(Sends.Canary, pedido.Body);
        Assert.DoesNotContain("Mauro", pedido.Uri.ToString());
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("Resultado\nsegunda linha")]
    [InlineData("com\ttabulacao")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Parametro_vazio_ou_com_quebra_de_linha_e_recusado_sem_chamar_a_Meta(string valor)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTemplateAsync(Template(new[] { new ProviderTemplateParameter("body", 1, null, valor) }), default);

        Assert.Equal((ProviderSendOutcome.RejectedPermanent, "template-parameter-invalid"), (resultado.Outcome, resultado.ErrorCode));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Parametro_grande_demais_ou_componente_desconhecido_e_recusado_sem_chamar_a_Meta()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var longo = await adaptador.SendTemplateAsync(Template(new[] { new ProviderTemplateParameter("body", 1, null, new string('a', 1025)) }), default);
        var desconhecido = await adaptador.SendTemplateAsync(Template(new[] { new ProviderTemplateParameter("footer", 1, null, "x") }), default);
        var demais = await adaptador.SendTemplateAsync(Template(Enumerable.Range(1, 29).Select(i => new ProviderTemplateParameter("body", i, null, "x")).ToList()), default);

        Assert.All(new[] { longo, desconhecido, demais }, r => Assert.Equal((ProviderSendOutcome.RejectedPermanent, "template-parameter-invalid"), (r.Outcome, r.ErrorCode)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Nome Com Maiuscula", "pt_BR")]
    [InlineData("nome com espaco", "pt_BR")]
    [InlineData("nome", "portugues")]
    [InlineData("nome", "")]
    [InlineData("", "pt_BR")]
    public async Task Nome_ou_idioma_fora_do_formato_e_recusado_sem_chamar_a_Meta(string nome, string idioma)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.SendTemplateAsync(Template(nome: nome, idioma: idioma), default);

        Assert.Equal((ProviderSendOutcome.RejectedPermanent, "template-invalid"), (resultado.Outcome, resultado.ErrorCode));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Token_e_destinatario_invalidos_sao_recusados_como_no_texto_livre()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var token = await adaptador.SendTemplateAsync(Template(token: "token com espaco"), default);
        var destinatario = await adaptador.SendTemplateAsync(Template(para: "nao-e-numero"), default);

        Assert.Equal((ProviderSendOutcome.AccountError, "credential-invalid-format"), (token.Outcome, token.ErrorCode));
        Assert.Equal((ProviderSendOutcome.RejectedPermanent, "invalid-recipient"), (destinatario.Outcome, destinatario.ErrorCode));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(132015)] // modelo pausado
    [InlineData(132016)] // modelo desativado
    [InlineData(132001)] // modelo inexistente
    [InlineData(132000)] // número de parâmetros diferente
    [InlineData(132012)] // formato de parâmetro incorreto
    public async Task A_recusa_do_modelo_pela_Meta_e_definitiva_e_leva_o_codigo(int codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.BadRequest, SendBodies.Error(codigo));

        var resultado = await adaptador.SendTemplateAsync(Template(), default);

        Assert.Equal((ProviderSendOutcome.RejectedPermanent, codigo.ToString()), (resultado.Outcome, resultado.ErrorCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 132001)]
    [InlineData(HttpStatusCode.InternalServerError, 132015)] // a página oficial não fixa o estado HTTP: o código decide
    [InlineData(HttpStatusCode.Forbidden, 132016)]
    public async Task O_codigo_da_recusa_do_modelo_decide_o_desfecho_qualquer_que_seja_o_estado_HTTP(HttpStatusCode status, int codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(status, SendBodies.Error(codigo));

        var resultado = await adaptador.SendTemplateAsync(Template(), default);

        Assert.Equal((ProviderSendOutcome.RejectedPermanent, codigo.ToString()), (resultado.Outcome, resultado.ErrorCode));
    }

    [Fact]
    public async Task Resposta_ambigua_do_envio_de_modelo_e_desconhecida_e_nunca_um_sucesso()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.InternalServerError, "erro do servidor, sem corpo de erro da Graph");

        var resultado = await adaptador.SendTemplateAsync(Template(), default);

        Assert.Equal(ProviderSendOutcome.Unknown, resultado.Outcome);
        Assert.Null(resultado.ProviderMessageId);
    }
}
