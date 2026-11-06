using IaBimeta.Api.Channels;
using Microsoft.AspNetCore.Http;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O guarda do ingresso PÚBLICO (túnel HTTPS): com a porta pública configurada, o que chega por ela só alcança o webhook do provedor; nada de login, simulador nem cabeçalho de desenvolvimento pela internet. Também
/// vale para a requisição com rastro de túnel em qualquer porta (o túnel apontado por engano para a porta principal). Sem configuração, o guarda não age.
/// </summary>
public sealed class PublicIngressGuardTests
{
    private const int PortaPublica = 5156;
    private const int PortaPrincipal = 5155;

    private static async Task<(bool Passou, int Status)> ChamarAsync(int? portaPublica, int portaLocal, string caminho, string metodo = "GET", params string[] cabecalhos)
    {
        var passou = false;
        var guarda = new PublicIngressGuard(_ =>
        {
            passou = true;
            return Task.CompletedTask;
        }, new PublicIngressOptions { PublicListenerPort = portaPublica });

        var http = new DefaultHttpContext();
        http.Connection.LocalPort = portaLocal;
        http.Request.Path = caminho;
        http.Request.Method = metodo;
        foreach (var cabecalho in cabecalhos)
        {
            http.Request.Headers[cabecalho] = "x";
        }

        await guarda.InvokeAsync(http);
        return (passou, http.Response.StatusCode);
    }

    [Theory]
    [InlineData("/webhooks/whatsapp/6f61d83d-f8f8-4362-9738-c20e844674bc", "GET")]
    [InlineData("/webhooks/whatsapp/6f61d83d-f8f8-4362-9738-c20e844674bc", "POST")]
    public async Task Pela_porta_publica_o_webhook_do_provedor_passa(string caminho, string metodo)
    {
        var (passou, _) = await ChamarAsync(PortaPublica, PortaPublica, caminho, metodo);

        Assert.True(passou);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/auth/login")]
    [InlineData("/simulator/ai/credential")]
    [InlineData("/simulator/whatsapp/status")]
    [InlineData("/channels/accounts")]
    [InlineData("/contact-center/items")]
    [InlineData("/organizations")]
    [InlineData("/")]
    [InlineData("/webhooks/whatsappx/abc")]
    [InlineData("/webhooks")]
    [InlineData("/webhooks/other/abc")]
    public async Task Pela_porta_publica_qualquer_outro_caminho_responde_404_sem_chegar_ao_Api(string caminho)
    {
        var (passou, status) = await ChamarAsync(PortaPublica, PortaPublica, caminho);

        Assert.False(passou);
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Theory]
    [InlineData("Cf-Ray")]
    [InlineData("Cf-Connecting-Ip")]
    [InlineData("X-Forwarded-For")]
    public async Task Requisicao_com_rastro_de_tunel_na_porta_principal_so_alcanca_o_webhook(string cabecalho)
    {
        // O túnel apontado por engano para a porta principal (5155): o simulador, o login e o cabeçalho de desenvolvimento NÃO ficam expostos.
        var (passouLogin, statusLogin) = await ChamarAsync(PortaPublica, PortaPrincipal, "/auth/login", "POST", cabecalho);
        var (passouSimulador, statusSimulador) = await ChamarAsync(PortaPublica, PortaPrincipal, "/simulator/ai/credential", "PUT", cabecalho);
        var (passouWebhook, _) = await ChamarAsync(PortaPublica, PortaPrincipal, "/webhooks/whatsapp/6f61d83d-f8f8-4362-9738-c20e844674bc", "POST", cabecalho);

        Assert.False(passouLogin);
        Assert.Equal(StatusCodes.Status404NotFound, statusLogin);
        Assert.False(passouSimulador);
        Assert.Equal(StatusCodes.Status404NotFound, statusSimulador);
        Assert.True(passouWebhook);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/auth/login")]
    [InlineData("/simulator/ai/credential")]
    [InlineData("/channels/accounts")]
    public async Task Pela_porta_principal_sem_rastro_de_tunel_o_uso_local_segue_intacto(string caminho)
    {
        var (passou, _) = await ChamarAsync(PortaPublica, PortaPrincipal, caminho);

        Assert.True(passou);
    }

    [Theory]
    [InlineData("/health", PortaPrincipal)]
    [InlineData("/auth/login", PortaPublica)]
    [InlineData("/simulator/ai/credential", PortaPublica)]
    public async Task Sem_porta_publica_configurada_o_guarda_nao_age(string caminho, int portaLocal)
    {
        var (passou, _) = await ChamarAsync(null, portaLocal, caminho, "GET", "Cf-Ray");

        Assert.True(passou);
    }
}
