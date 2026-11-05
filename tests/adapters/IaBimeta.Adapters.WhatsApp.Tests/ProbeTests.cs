using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// A sonda do número (teste de prontidão): UMA leitura (<c>GET</c>) no provedor para provar que o token é aceito e que o número existe, sem enviar mensagem. O token vai só no cabeçalho, o desfecho vem do
/// <c>error.code</c>, e o que deixa dúvida nunca vira <c>Ok</c>. HTTP simulado: nenhuma rede e nenhum token real.
/// </summary>
public sealed class ProbeTests
{
    private static ProviderProbeRequest Request(string token = Sends.Canary, string phone = Sends.PhoneNumberId) => new(token, phone);

    private static string Number(string id = Sends.PhoneNumberId, string name = "Laboratório Exemplo", string display = "+55 11 3000-0000", string quality = "GREEN") =>
        new JsonObject { ["id"] = id, ["display_phone_number"] = display, ["verified_name"] = name, ["quality_rating"] = quality }.ToJsonString();

    [Fact]
    public async Task A_sonda_e_uma_leitura_GET_do_numero_com_o_token_so_no_cabecalho_e_nenhuma_mensagem_sai()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.OK, Number());

        var resultado = await adaptador.ProbeAsync(Request(), default);

        var pedido = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, pedido.Method);
        Assert.Equal($"/v26.0/{Sends.PhoneNumberId}", pedido.Uri.AbsolutePath);
        Assert.Equal("?fields=id,display_phone_number,verified_name,quality_rating", pedido.Uri.Query);
        Assert.DoesNotContain("/messages", pedido.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal($"Bearer {Sends.Canary}", pedido.Authorization);
        Assert.DoesNotContain(Sends.Canary, pedido.Uri.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, pedido.Body);
        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.Ok, "Laboratório Exemplo", "+55 11 3000-0000", "GREEN", null), resultado);
    }

    [Fact]
    public async Task Resposta_sobre_outro_numero_nao_prova_a_combinacao_e_e_NotFound()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.OK, Number(id: "999999999999999"));

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.NotFound, null, null, null, "id-mismatch"), resultado);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("<html>ok</html>")]
    [InlineData("")]
    public async Task Sucesso_sem_o_numero_no_corpo_nunca_e_Ok(string corpo)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.OK, corpo);

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(ProviderProbeOutcome.Unavailable, resultado.Outcome);
    }

    [Theory]
    [InlineData(190, HttpStatusCode.Unauthorized)]
    [InlineData(190, HttpStatusCode.BadRequest)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(3, HttpStatusCode.BadRequest)]
    [InlineData(10, HttpStatusCode.BadRequest)]
    [InlineData(200, HttpStatusCode.Forbidden)]
    [InlineData(299, HttpStatusCode.BadRequest)]
    [InlineData(368, HttpStatusCode.Forbidden)]
    [InlineData(131031, HttpStatusCode.BadRequest)]
    public async Task Token_invalido_permissao_ausente_ou_conta_restrita_e_CredentialRejected_com_o_codigo(int codigo, HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(http, SendBodies.Error(codigo));

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.CredentialRejected, null, null, null, codigo.ToString()), resultado);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Estado_401_ou_403_sem_codigo_conhecido_tambem_e_CredentialRejected(HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(http, "{}");

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(ProviderProbeOutcome.CredentialRejected, resultado.Outcome);
    }

    [Theory]
    [InlineData(803, HttpStatusCode.NotFound)]
    [InlineData(100, HttpStatusCode.BadRequest)]
    public async Task Numero_inexistente_ou_inacessivel_e_NotFound(int codigo, HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(http, SendBodies.Error(codigo));

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.NotFound, null, null, null, codigo.ToString()), resultado);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Erro_do_servidor_ou_limite_de_taxa_sem_corpo_de_erro_deixa_a_sonda_inconclusiva(HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(http, "<html>erro</html>");

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.Unavailable, null, null, null, $"http-{(int)http}"), resultado);
    }

    [Fact]
    public async Task Rede_caida_e_tempo_esgotado_deixam_a_sonda_inconclusiva_sem_repetir_nada_do_pedido()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.RequestTimeout = TimeSpan.FromMilliseconds(200));

        handler.Behavior = (_, _) => throw new HttpRequestException("falha com o token " + Sends.Canary);
        var rede = await adaptador.ProbeAsync(Request(), default);
        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.Unavailable, null, null, null, "network-error"), rede);

        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return FakeHandler.Json(HttpStatusCode.OK, Number());
        };
        var tempo = await adaptador.ProbeAsync(Request(), default);
        Assert.Equal(new ProviderProbeResult(ProviderProbeOutcome.Unavailable, null, null, null, "timeout"), tempo);
    }

    [Fact]
    public async Task O_cancelamento_do_chamador_propaga()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return FakeHandler.Json(HttpStatusCode.OK, Number());
        };
        using var origem = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adaptador.ProbeAsync(Request(), origem.Token));
    }

    [Theory]
    [InlineData("tok en", "106540352242922")]
    [InlineData("tok\nen", "106540352242922")]
    [InlineData("", "106540352242922")]
    [InlineData(Sends.Canary, "10654/../messages")]
    [InlineData(Sends.Canary, "abc")]
    [InlineData(Sends.Canary, "")]
    public async Task Token_ou_numero_fora_do_formato_nunca_chegam_ao_provedor(string token, string numero)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await adaptador.ProbeAsync(new ProviderProbeRequest(token, numero), default);

        Assert.Empty(handler.Requests);
        Assert.Equal(ProviderProbeOutcome.CredentialRejected, resultado.Outcome);
        Assert.Equal("credential-or-account-invalid-format", resultado.ErrorCode);
    }

    [Fact]
    public async Task Os_textos_do_provedor_exibidos_ao_administrador_saem_sem_controle_e_limitados()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.OK, Number(name: "Lab\u0007 Exemplo\n" + new string('x', 400), display: "+55\u0000 11", quality: "GREEN"));

        var resultado = await adaptador.ProbeAsync(Request(), default);

        Assert.Equal(ProviderProbeOutcome.Ok, resultado.Outcome);
        Assert.DoesNotContain(resultado.VerifiedName!, c => char.IsControl(c));
        Assert.Equal(120, resultado.VerifiedName!.Length);
        Assert.Equal("+55 11", resultado.DisplayPhoneNumber);
    }

    [Fact]
    public async Task O_resultado_da_sonda_nunca_carrega_o_token_nem_texto_livre_do_erro()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.Unauthorized, SendBodies.Error(190, "token " + Sends.Canary + " expirado", "detalhe livre"));

        var resultado = await adaptador.ProbeAsync(Request(), default);

        var texto = resultado.ToString();
        Assert.DoesNotContain(Sends.Canary, texto, StringComparison.Ordinal);
        Assert.DoesNotContain("detalhe livre", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("expirado", texto, StringComparison.Ordinal);
    }
}
