using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.Identity.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O TESTE DE PRONTIDÃO de uma conta do WhatsApp, por HTTP de verdade (Api e Worker reais, PostgreSQL real, Graph API simulada): confere a configuração SEM enviar mensagem alguma. Sem a leitura no provedor
/// (<c>live</c>), nenhuma chamada sai do Api; com ela, só uma LEITURA do número (<c>GET</c>), nunca um envio. O relatório nunca traz valor de credencial. Nenhuma credencial é real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProntidaoWhatsAppTests(PostgresFixture banco)
{
    private const string UrlPublica = "https://atendimento.exemplo.com.br";

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, bool comUrlPublica)
    {
        var configuracao = comUrlPublica ? new Dictionary<string, string?> { ["Channels:Webhook:PublicBaseUrl"] = UrlPublica } : null;
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static string Caminho(EnsaioDeWhatsApp e, string final) => $"/channels/accounts/{e.ContaId}/{final}";

    private static string StatusDe(JsonNode relatorio, string codigo) =>
        relatorio["checks"]!.AsArray().Single(c => c!["code"]!.GetValue<string>() == codigo)!["status"]!.GetValue<string>();

    private static string DetalheDe(JsonNode relatorio, string codigo) =>
        relatorio["checks"]!.AsArray().Single(c => c!["code"]!.GetValue<string>() == codigo)!["detail"]!.GetValue<string>();

    [Fact]
    public async Task Conta_completa_com_endereco_publico_esta_pronta_sem_chamar_o_provedor_e_o_relatorio_nunca_traz_credencial()
    {
        await using var e = await IniciarAsync("prontok", comUrlPublica: true);
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        var (status, relatorio) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "readiness"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(relatorio!["ready"]!.GetValue<bool>(), relatorio.ToJsonString());
        Assert.False(relatorio["liveProbe"]!.GetValue<bool>());
        Assert.Equal($"{UrlPublica}/webhooks/whatsapp/{e.WebhookId:D}", relatorio["webhookUrl"]!.GetValue<string>());
        foreach (var codigo in new[] { "secret-store", "provider.adapter", "credential.access-token", "credential.app-secret", "credential.webhook-verify-token", "credential.distinct", "webhook.id", "webhook.public-url", "sending.state" })
        {
            Assert.Equal("Pass", StatusDe(relatorio, codigo));
        }

        Assert.Equal("Pass", StatusDe(relatorio, $"instance.{e.NumeroDoCanal}"));
        Assert.Equal("Skipped", StatusDe(relatorio, "provider.live"));

        // Sem a leitura no provedor, NADA saiu do Api: nem leitura, nem envio.
        Assert.Empty(e.Graph.Leituras);
        Assert.Empty(e.Graph.Capturas);

        // Nenhum valor de credencial no relatório.
        var texto = relatorio.ToJsonString();
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeAcesso, texto, StringComparison.Ordinal);
        Assert.DoesNotContain(EnsaioDeWhatsApp.SegredoDoApp, texto, StringComparison.Ordinal);
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeVerificacao, texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leitura_no_provedor_e_so_um_GET_do_numero_com_o_token_da_conta_e_nunca_um_envio()
    {
        await using var e = await IniciarAsync("prontolive", comUrlPublica: true);
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        e.Graph.RoteiroDeLeitura = _ => ServidorGraphLoopback.NumeroValido(e.NumeroDoCanal, "Laboratório Exemplo Ltda", "+55 (11) 3000-0000", "GREEN");

        var (status, relatorio) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "readiness?live=true"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(relatorio!["ready"]!.GetValue<bool>(), relatorio.ToJsonString());
        Assert.True(relatorio["liveProbe"]!.GetValue<bool>());
        Assert.Equal("Pass", StatusDe(relatorio, $"provider.live.{e.NumeroDoCanal}"));
        Assert.Contains("Laboratório Exemplo Ltda", DetalheDe(relatorio, $"provider.live.{e.NumeroDoCanal}"), StringComparison.Ordinal);

        var leitura = Assert.Single(e.Graph.Leituras);
        Assert.Equal("GET", leitura.Metodo);
        Assert.Equal(EnsaioDeWhatsApp.TokenDeAcesso, leitura.Credencial);
        Assert.EndsWith("/" + e.NumeroDoCanal, leitura.Caminho, StringComparison.Ordinal);
        Assert.Empty(e.Graph.Capturas); // nenhuma mensagem saiu
        Assert.DoesNotContain(EnsaioDeWhatsApp.TokenDeAcesso, relatorio.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, 190, "CredentialRejected")]
    [InlineData(400, 100, "NotFound")]
    [InlineData(500, 1, "Unavailable")]
    public async Task Se_o_provedor_recusa_ou_nao_responde_a_conta_nao_esta_pronta_e_nada_foi_enviado(int http, int codigo, string porque)
    {
        await using var e = await IniciarAsync("prontofalha" + porque.ToLowerInvariant(), comUrlPublica: true);
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);
        e.Graph.RoteiroDeLeitura = _ => ServidorGraphLoopback.Erro(http, codigo);

        var (_, relatorio) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "readiness?live=true"));

        Assert.False(relatorio!["ready"]!.GetValue<bool>(), porque);
        Assert.Equal("Fail", StatusDe(relatorio, $"provider.live.{e.NumeroDoCanal}"));
        Assert.Empty(e.Graph.Capturas);
    }

    [Fact]
    public async Task Sem_endereco_publico_HTTPS_a_conta_nao_esta_pronta_e_o_relatorio_diz_o_que_falta()
    {
        await using var e = await IniciarAsync("prontosemurl", comUrlPublica: false);
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        var (_, relatorio) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "readiness"));

        Assert.False(relatorio!["ready"]!.GetValue<bool>());
        Assert.Equal("Fail", StatusDe(relatorio, "webhook.public-url"));
        Assert.Contains("Channels:Webhook:PublicBaseUrl", DetalheDe(relatorio, "webhook.public-url"), StringComparison.Ordinal);
        Assert.Null(relatorio["webhookUrl"]);
        Assert.Equal("Pass", StatusDe(relatorio, "credential.access-token")); // o resto continua certo: o relatório aponta só o que falta
    }

    [Fact]
    public async Task Conta_pausada_ou_sem_credenciais_numero_ou_webhook_nao_esta_pronta()
    {
        await using var e = await IniciarAsync("prontoincompleto", comUrlPublica: true);
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        // Pausada: o envio não sai até a retomada.
        Assert.Equal(HttpStatusCode.OK, (await e.ChamarAsync(admin, HttpMethod.Post, Caminho(e, "sending/pause"))).Status);
        var (_, pausada) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "readiness"));
        Assert.False(pausada!["ready"]!.GetValue<bool>());
        Assert.Equal("Fail", StatusDe(pausada, "sending.state"));
        Assert.Contains("PAUSADO", DetalheDe(pausada, "sending.state"), StringComparison.Ordinal);

        // Uma conta recém-criada, sem número, sem credenciais e sem webhook, na mesma organização.
        var contaVazia = await e.Ia.ComoSistemaAsync(sp => sp.GetRequiredService<ChannelAccountService>().CreateAsync(
            e.OrganizacaoId, ChannelProvider.WhatsAppCloud, "waba-vazia-" + Guid.NewGuid().ToString("N")[..8], "Conta vazia"));
        var (status, vazia) = await e.ChamarAsync(admin, HttpMethod.Get, $"/channels/accounts/{contaVazia.Id}/readiness");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(vazia!["ready"]!.GetValue<bool>());
        Assert.Equal("Fail", StatusDe(vazia, "credential.access-token"));
        Assert.Equal("Fail", StatusDe(vazia, "credential.app-secret"));
        Assert.Equal("Fail", StatusDe(vazia, "credential.webhook-verify-token"));
        Assert.Equal("Fail", StatusDe(vazia, "instance.registered"));
        Assert.Equal("Fail", StatusDe(vazia, "webhook.id"));
        Assert.Null(vazia["webhookUrl"]);
        Assert.Empty(e.Graph.Capturas);
    }

    [Fact]
    public async Task Valores_repetidos_entre_as_credenciais_reprovam_a_prontidao_sem_mostrar_o_valor()
    {
        await using var e = await IniciarAsync("prontoigual", comUrlPublica: true);
        var admin = await e.TokenDeAsync(Role.OrganizationAdmin);

        // O erro comum: o mesmo valor colado em dois campos.
        await e.Ia.ComoSistemaAsync(async sp =>
        {
            await sp.GetRequiredService<ChannelAccountService>().SetSecretAsync(e.ContaId, ChannelSecretKind.WebhookVerifyToken, EnsaioDeWhatsApp.SegredoDoApp);
            return true;
        });

        var (_, relatorio) = await e.ChamarAsync(admin, HttpMethod.Get, Caminho(e, "readiness"));

        Assert.False(relatorio!["ready"]!.GetValue<bool>());
        Assert.Equal("Fail", StatusDe(relatorio, "credential.distinct"));
        Assert.DoesNotContain(EnsaioDeWhatsApp.SegredoDoApp, relatorio.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quem_so_le_canais_nao_pede_o_teste_de_prontidao_e_outro_tenant_nao_enxerga_a_conta()
    {
        await using var e = await IniciarAsync("prontoacesso", comUrlPublica: true);
        var gestor = await e.TokenDeAsync(Role.Manager);

        // O relatório traz o endereço do webhook (segredo operacional): só quem GERE os canais o recebe.
        Assert.Equal(HttpStatusCode.Forbidden, (await e.ChamarAsync(gestor, HttpMethod.Get, Caminho(e, "readiness"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.ChamarAsync(string.Empty, HttpMethod.Get, Caminho(e, "readiness"))).Status);
        Assert.Empty(e.Graph.Leituras);
    }
}
