using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IaBimeta.Api.Provisioning;
using IaBimeta.IntegrationTests.Agents;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A API administrativa dos provedores de IA (Etapa 05) pelo Api REAL: sem sessão 401, sem permissão 403, o tenant nunca vem do corpo, nenhuma resposta nem log traz a chave, e a validação é do servidor. Nenhuma rede nem chave real
/// (credenciais canário; o "servidor da OpenAI" é local).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvedoresDeIaApiTests(PostgresFixture banco)
{
    private const string SenhaDoAdministrador = "Senha-Do-Admin-2026!";
    private const string SenhaDaAtendente = "Senha-Da-Atendente-2026!";
    private const string ChaveDoTeste = "sk-ant-CANARIO-DA-ETAPA-05-0123456789";

    private sealed class Segredos : IProvisioningSecrets
    {
        public string? Get(string name) => name switch
        {
            "ADM_SENHA" => SenhaDoAdministrador,
            "ATD_SENHA" => SenhaDaAtendente,
            "IA_CHAVE" => EnsaioDeIa.ChaveCanario,
            _ => null,
        };
    }

    private sealed record Empresa(string Slug, string Admin, string Atendente);

    private static ProvisioningSpec Especificacao(string slug) => ProvisioningSpec.Parse($$"""
        {
          "tenant": { "slug": "{{slug}}", "name": "Laboratório {{slug}}", "unlimitedUsers": true },
          "administrator": { "email": "admin@{{slug}}.local", "displayName": "Administradora", "passwordEnv": "ADM_SENHA" },
          "organization": { "name": "Laboratório {{slug}}", "unitName": "Matriz" },
          "queue": { "name": "Atendimento humano" },
          "ai": { "providerId": "openai", "modelId": "{{EnsaioDeIa.ModeloDoEnsaio}}", "instructions": "Você é a assistente.", "handoffNotice": "Vou chamar uma pessoa.", "faq": [], "apiKeyEnv": "IA_CHAVE" },
          "whatsApp": { "mode": "simulated" },
          "attendants": [ { "email": "ana@{{slug}}.local", "displayName": "Ana Atendente", "passwordEnv": "ATD_SENHA" } ]
        }
        """);

    private static async Task<string> LoginAsync(EnsaioDeWhatsApp e, string tenant, string email, string senha)
    {
        using var resposta = await e.Ia.Api.Client.PostAsJsonAsync("/auth/login", new { tenant, email, password = senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<LoginDto>())!.Token;
    }

    private static async Task<Empresa> ProvisionarAsync(EnsaioDeWhatsApp e)
    {
        var slug = $"pia-{Guid.NewGuid().ToString("N")[..10]}";
        var relatorio = await new TenantProvisioner(e.Ia.Api.Services, new Segredos()).ProvisionAsync(Especificacao(slug));
        Assert.True(relatorio.Succeeded, string.Join(" | ", relatorio.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        return new Empresa(slug, await LoginAsync(e, slug, $"admin@{slug}.local", SenhaDoAdministrador), await LoginAsync(e, slug, $"ana@{slug}.local", SenhaDaAtendente));
    }

    private static async Task<(HttpStatusCode Status, string Texto, JsonNode? Corpo)> ChamarAsync(EnsaioDeWhatsApp e, string? token, HttpMethod metodo, string caminho, object? corpo = null)
    {
        using var resposta = await e.Ia.Api.EnviarAsync(token, metodo, caminho, corpo);
        var texto = await resposta.Content.ReadAsStringAsync();
        return (resposta.StatusCode, texto, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
    }

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, null);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static readonly (HttpMethod Metodo, string Caminho, object? Corpo)[] Rotas =
    [
        (HttpMethod.Get, "/ai/settings", null),
        (HttpMethod.Put, "/ai/settings/default", new { providerId = "ollama", modelId = "qwen3:4b" }),
        (HttpMethod.Put, "/ai/providers/anthropic/credential", new { apiKey = "x" }),
        (HttpMethod.Delete, "/ai/providers/anthropic/credential", null),
        (HttpMethod.Post, "/ai/providers/anthropic/connectivity-test", new { modelId = "claude-haiku-5-5" }),
        (HttpMethod.Post, "/ai/providers/anthropic/generation-test", new { modelId = "claude-haiku-5-5", confirmed = true }),
    ];

    [Fact]
    public async Task Toda_rota_recusa_sem_sessao_com_401_e_sem_a_permissao_de_IA_com_403()
    {
        await using var e = await IniciarAsync("piaperm");
        var a = await ProvisionarAsync(e);

        foreach (var (metodo, caminho, corpo) in Rotas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChamarAsync(e, null, metodo, caminho, corpo)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await ChamarAsync(e, a.Atendente, metodo, caminho, corpo)).Status);
        }
    }

    [Fact]
    public async Task O_administrador_do_tenant_consulta_cadastra_e_remove_a_credencial_sem_que_a_chave_apareca_em_resposta_ou_log()
    {
        await using var e = await IniciarAsync("piaadm");
        var a = await ProvisionarAsync(e);

        var inicial = await ChamarAsync(e, a.Admin, HttpMethod.Get, "/ai/settings");
        Assert.Equal(HttpStatusCode.OK, inicial.Status);
        Assert.Equal("openai", (string?)inicial.Corpo!["defaultProviderId"]);
        var anthropic = inicial.Corpo["providers"]!.AsArray().Single(p => (string?)p!["providerId"] == "anthropic")!;
        Assert.False((bool)anthropic["credential"]!["present"]!);

        var cadastro = await ChamarAsync(e, a.Admin, HttpMethod.Put, "/ai/providers/anthropic/credential", new { apiKey = ChaveDoTeste });
        Assert.Equal(HttpStatusCode.NoContent, cadastro.Status);
        Assert.Equal(string.Empty, cadastro.Texto);

        var depois = await ChamarAsync(e, a.Admin, HttpMethod.Get, "/ai/settings");
        var presente = depois.Corpo!["providers"]!.AsArray().Single(p => (string?)p!["providerId"] == "anthropic")!["credential"]!;
        Assert.True((bool)presente["present"]!);
        Assert.Matches("^[0-9a-f]{12}$", (string)presente["fingerprint"]!);
        Assert.DoesNotContain(ChaveDoTeste, depois.Texto);
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, depois.Texto);

        var remocao = await ChamarAsync(e, a.Admin, HttpMethod.Delete, "/ai/providers/anthropic/credential");
        Assert.Equal(HttpStatusCode.OK, remocao.Status);
        Assert.True((bool)remocao.Corpo!["revoked"]!);
        Assert.False((bool)remocao.Corpo["wasDefaultProvider"]!);

        // Nenhum log do Api nem do Worker traz a chave.
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(ChaveDoTeste, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_validacao_e_do_servidor_com_codigo_estavel_o_tenant_nunca_vem_do_corpo_e_nao_ha_rota_para_url_modelo_livre_ou_preco()
    {
        await using var e = await IniciarAsync("piaval");
        var a = await ProvisionarAsync(e);

        var semCredencial = await ChamarAsync(e, a.Admin, HttpMethod.Put, "/ai/settings/default", new { providerId = "anthropic", modelId = "claude-haiku-5-5" });
        Assert.Equal(HttpStatusCode.Conflict, semCredencial.Status);
        Assert.Equal("credential-required", (string?)semCredencial.Corpo!["code"]);

        var modeloLivre = await ChamarAsync(e, a.Admin, HttpMethod.Put, "/ai/settings/default", new { providerId = "openai", modelId = "modelo-inventado" });
        Assert.Equal(HttpStatusCode.BadRequest, modeloLivre.Status);
        Assert.Equal("model-not-authorized", (string?)modeloLivre.Corpo!["code"]);

        var ollama = await ChamarAsync(e, a.Admin, HttpMethod.Put, "/ai/providers/ollama/credential", new { apiKey = "qualquer" });
        Assert.Equal("credential-not-used", (string?)ollama.Corpo!["code"]);

        var semConfirmacao = await ChamarAsync(e, a.Admin, HttpMethod.Post, "/ai/providers/openai/generation-test", new { modelId = EnsaioDeIa.ModeloDoEnsaio, confirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, semConfirmacao.Status);
        Assert.Equal("confirmation-required", (string?)semConfirmacao.Corpo!["code"]);

        // Um tenantId no corpo é ignorado: o tenant é o da sessão (a configuração da empresa NÃO muda para outro).
        var comTenant = await ChamarAsync(e, a.Admin, HttpMethod.Put, "/ai/settings/default", new { providerId = "ollama", modelId = "qwen3:4b", tenantId = Guid.NewGuid(), baseAddress = "http://10.0.0.1:11434" });
        Assert.NotEqual(HttpStatusCode.Unauthorized, comTenant.Status);
        Assert.DoesNotContain("10.0.0.1", comTenant.Texto);

        foreach (var caminho in new[] { "/ai/providers/openai/endpoint", "/ai/models", "/ai/prices", "/ai/limits" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, a.Admin, HttpMethod.Put, caminho, new { value = "x" })).Status);
        }
    }

    [Fact]
    public async Task O_teste_de_conectividade_pela_API_devolve_o_resultado_com_data_sem_virar_verificado_quando_o_servidor_nao_confirma()
    {
        await using var e = await IniciarAsync("piacon");
        var a = await ProvisionarAsync(e);

        var r = await ChamarAsync(e, a.Admin, HttpMethod.Post, "/ai/providers/openai/connectivity-test", new { modelId = EnsaioDeIa.ModeloDoEnsaio });

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True((bool)r.Corpo!["supported"]!);
        Assert.NotEqual("Verified", (string?)r.Corpo["result"]!["state"]); // o "servidor da OpenAI" do ensaio não tem a rota de modelos: nada é confirmado
        Assert.NotNull(r.Corpo["result"]!["checkedAt"]);
        Assert.DoesNotContain(EnsaioDeIa.ChaveCanario, r.Texto);
    }
}
