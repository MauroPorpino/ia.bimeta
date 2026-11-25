using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.Api.Provisioning;
using IaBimeta.IntegrationTests.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A seguranca das rotas do conhecimento hibrido (ADR 0034) pela API REAL (PostgreSQL real, Meta e OpenAI simuladas por HTTP): sem sessao 401, sem a permissao de configurar o tenant 403, objeto de outra
/// empresa 404, configuracao semantica isolada por empresa, IA local desligada recusando com 503 sem gravar nada, e nenhuma sugestao ou configuracao publicando o conhecimento.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConhecimentoHibridoSegurancaTests(PostgresFixture banco)
{
    private const string SenhaDoAdministrador = "Senha-Do-Admin-2026!";
    private const string SenhaDaAtendente = "Senha-Da-Atendente-2026!";

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
          "ai": {
            "providerId": "openai",
            "modelId": "{{EnsaioDeIa.ModeloDoEnsaio}}",
            "instructions": "Você é a assistente do laboratório de teste.",
            "handoffNotice": "Vou chamar uma pessoa.",
            "faq": [ { "question": "Qual o horário de atendimento?", "answer": "De segunda a sexta, das 7h às 17h.", "autoReply": true } ],
            "apiKeyEnv": "IA_CHAVE"
          },
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
        var slug = $"hib-{Guid.NewGuid().ToString("N")[..10]}";
        var relatorio = await new TenantProvisioner(e.Ia.Api.Services, new Segredos()).ProvisionAsync(Especificacao(slug));
        Assert.True(relatorio.Succeeded, string.Join(" | ", relatorio.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        return new Empresa(slug, await LoginAsync(e, slug, $"admin@{slug}.local", SenhaDoAdministrador), await LoginAsync(e, slug, $"ana@{slug}.local", SenhaDaAtendente));
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Corpo)> ChamarAsync(EnsaioDeWhatsApp e, string? token, HttpMethod metodo, string caminho, object? corpo = null)
    {
        using var resposta = await e.Ia.Api.EnviarAsync(token, metodo, caminho, corpo);
        var texto = await resposta.Content.ReadAsStringAsync();
        return (resposta.StatusCode, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
    }

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo, Dictionary<string, string?>? configuracao = null)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, configuracao);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static readonly (HttpMethod Metodo, string Caminho, object? Corpo)[] Rotas =
    [
        (HttpMethod.Get, "/agents/semantic/settings", null),
        (HttpMethod.Put, "/agents/semantic/settings", new { mode = "Evaluate", minScore = 0.92, minMargin = 0.05 }),
        (HttpMethod.Get, "/agents/semantic/metrics", null),
        (HttpMethod.Get, "/agents/faq-suggestions", null),
        (HttpMethod.Get, "/agents/faq-suggestions/00000000-0000-4000-8000-000000000001", null),
        (HttpMethod.Post, "/agents/faq-suggestions/generate/gap/00000000-0000-4000-8000-000000000001", null),
        (HttpMethod.Post, "/agents/faq-suggestions/generate/cluster", null),
        (HttpMethod.Post, "/agents/faq-suggestions/generate/analysis", null),
        (HttpMethod.Put, "/agents/faq-suggestions/00000000-0000-4000-8000-000000000001", new { question = "x", answer = (string?)null, aliases = (string[]?)null }),
        (HttpMethod.Post, "/agents/faq-suggestions/00000000-0000-4000-8000-000000000001/approve", null),
        (HttpMethod.Post, "/agents/faq-suggestions/00000000-0000-4000-8000-000000000001/reject", null),
    ];

    [Fact]
    public async Task Toda_rota_nova_recusa_sem_sessao_com_401_e_sem_a_permissao_de_configurar_com_403()
    {
        await using var e = await IniciarAsync("hibperm");
        var a = await ProvisionarAsync(e);

        foreach (var (metodo, caminho, corpo) in Rotas)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChamarAsync(e, null, metodo, caminho, corpo)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await ChamarAsync(e, a.Atendente, metodo, caminho, corpo)).Status);
        }
    }

    [Fact]
    public async Task A_configuracao_semantica_de_uma_empresa_nunca_aparece_na_outra_e_o_tenant_nao_vem_do_corpo()
    {
        await using var e = await IniciarAsync("hibiso");
        var a = await ProvisionarAsync(e);
        var b = await ProvisionarAsync(e);

        var padrao = await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/semantic/settings");
        Assert.Equal(("Off", true), (padrao.Corpo!["mode"]!.GetValue<string>(), padrao.Corpo["isDefault"]!.GetValue<bool>()));

        // Um tenantId no corpo e ignorado: a configuracao vai para a empresa da sessao.
        var salva = await ChamarAsync(e, a.Admin, HttpMethod.Put, "/agents/semantic/settings", new { mode = "Evaluate", minScore = 0.93, minMargin = 0.06, tenantId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, salva.Status);

        var deB = await ChamarAsync(e, b.Admin, HttpMethod.Get, "/agents/semantic/settings");
        Assert.Equal(("Off", true), (deB.Corpo!["mode"]!.GetValue<string>(), deB.Corpo["isDefault"]!.GetValue<bool>()));
        var deA = await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/semantic/settings");
        Assert.Equal(("Evaluate", 0.93), (deA.Corpo!["mode"]!.GetValue<string>(), deA.Corpo["minScore"]!.GetValue<double>()));
    }

    [Fact]
    public async Task Configuracao_invalida_vira_400_e_nada_e_gravado()
    {
        await using var e = await IniciarAsync("hibval");
        var a = await ProvisionarAsync(e);

        Assert.Equal(HttpStatusCode.BadRequest, (await ChamarAsync(e, a.Admin, HttpMethod.Put, "/agents/semantic/settings", new { mode = "Active", minScore = 0.3, minMargin = 0.05 })).Status);
        var lida = await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/semantic/settings");
        Assert.True(lida.Corpo!["isDefault"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sugestao_de_outra_empresa_responde_404_em_todas_as_operacoes_e_nao_e_alterada()
    {
        await using var e = await IniciarAsync("hibobj");
        var a = await ProvisionarAsync(e);
        var b = await ProvisionarAsync(e);

        // Uma sugestao pendente da empresa A, criada direto no banco (a IA local nao roda aqui).
        var tenantA = await e.Ia.Api.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope().ServiceProvider.GetRequiredService<IaBimeta.Tenancy.Contracts.Queries.ITenantDirectory>().FindBySlugAsync(a.Slug, default);
        var sugestao = FaqSuggestion.Create(tenantA!.TenantId, FaqSuggestionKind.NewFaq, null, $"gap:{Guid.NewGuid()}", new SuggestionPayload("Pergunta?", "Resposta.", null, null, null, null, null), "teste", DateTimeOffset.UtcNow, null);
        await using (var conexao = new Npgsql.NpgsqlConnection(TestDatabase.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new Npgsql.NpgsqlCommand(
                "INSERT INTO agents.faq_suggestions (tenant_id, id, kind, status, dedupe_key, payload_json, model_id, created_at, edits, revision) VALUES (@t, @i, 1, 1, @k, @p::text, 'teste', now(), 0, 1)", conexao);
            comando.Parameters.AddWithValue("t", tenantA.TenantId.Value);
            comando.Parameters.AddWithValue("i", sugestao.Id);
            comando.Parameters.AddWithValue("k", sugestao.DedupeKey);
            comando.Parameters.AddWithValue("p", sugestao.PayloadJson);
            await comando.ExecuteNonQueryAsync();
        }

        var id = sugestao.Id;
        Assert.Equal(HttpStatusCode.OK, (await ChamarAsync(e, a.Admin, HttpMethod.Get, $"/agents/faq-suggestions/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Get, $"/agents/faq-suggestions/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Put, $"/agents/faq-suggestions/{id}", new { question = "invadida", answer = (string?)null, aliases = (string[]?)null })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Post, $"/agents/faq-suggestions/{id}/approve")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Post, $"/agents/faq-suggestions/{id}/reject")).Status);
        Assert.Equal(0, (await ChamarAsync(e, b.Admin, HttpMethod.Get, "/agents/faq-suggestions")).Corpo!["total"]!.GetValue<int>());

        var intacta = await ChamarAsync(e, a.Admin, HttpMethod.Get, $"/agents/faq-suggestions/{id}");
        Assert.Equal(("Pending", "Pergunta?"), (intacta.Corpo!["status"]!.GetValue<string>(), intacta.Corpo["payload"]!["question"]!.GetValue<string>()));
    }

    [Fact]
    public async Task IA_local_desligada_recusa_com_503_e_codigo_fixo_sem_gravar_sugestao_nem_chamar_modelo()
    {
        await using var e = await IniciarAsync("hibdesl");
        var a = await ProvisionarAsync(e);

        foreach (var caminho in new[] { "/agents/faq-suggestions/generate/cluster", "/agents/faq-suggestions/generate/analysis" })
        {
            var r = await ChamarAsync(e, a.Admin, HttpMethod.Post, caminho);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status);
            Assert.Equal("disabled", r.Corpo!["code"]!.GetValue<string>());
        }

        Assert.Equal(0, (await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/faq-suggestions")).Corpo!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Configurar_a_busca_semantica_nunca_publica_nem_altera_a_versao_publicada()
    {
        await using var e = await IniciarAsync("hibpub");
        var a = await ProvisionarAsync(e);
        var antes = (await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/definition/published")).Corpo!["number"]!.GetValue<int>();

        await ChamarAsync(e, a.Admin, HttpMethod.Put, "/agents/semantic/settings", new { mode = "Active", minScore = 0.95, minMargin = 0.05 });
        await ChamarAsync(e, a.Admin, HttpMethod.Post, "/agents/faq-suggestions/generate/analysis");

        var depois = (await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/definition/published")).Corpo!["number"]!.GetValue<int>();
        Assert.Equal(antes, depois);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/definition/draft")).Status); // nem rascunho nasceu
    }

    [Fact]
    public async Task Publicar_o_conhecimento_exige_a_permissao_e_a_publicacao_nao_quebra_com_o_indice_desligado()
    {
        await using var e = await IniciarAsync("hibpubperm");
        var a = await ProvisionarAsync(e);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChamarAsync(e, a.Atendente, HttpMethod.Post, "/agents/definition/publish")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChamarAsync(e, null, HttpMethod.Post, "/agents/definition/publish")).Status);
    }
}
