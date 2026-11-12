using System.Net;
using System.Text.Json.Nodes;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using Npgsql;

namespace IaBimeta.IntegrationTests.Workflows;

/// <summary>
/// A administração dos fluxos do chatbot visual pela API REAL (HTTP) com PostgreSQL real (ADR 0037): papéis e permissões distintas, portão de função, isolamento entre tenants, publicação imutável (inclusive
/// pelo gatilho do banco), índices únicos sob concorrência, validação e simulação sem chamada real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowsApiTests(PostgresFixture banco)
{
    private const string Simples = """
        {"schemaVersion":1,"start":"oi","nodes":[
          {"id":"oi","type":"message","config":{"text":"Olá! Posso ajudar?"}},
          {"id":"fim","type":"end","config":{"text":"Até logo."}}],
         "edges":[{"from":"oi","port":"next","to":"fim"}],"variables":[]}
        """;

    private sealed class Cenario : IAsyncDisposable
    {
        public required ApiServer Api { get; init; }

        public required string Admin { get; init; }

        public required IaBimeta.Tenancy.Contracts.TenantInfo Tenant { get; init; }

        public required IaBimeta.Tenancy.Application.OrganizationView Org { get; init; }

        public async ValueTask DisposeAsync() => await Api.DisposeAsync();

        public async Task<(HttpStatusCode Status, JsonNode? Corpo)> Chamar(string? token, HttpMethod metodo, string caminho, object? corpo = null)
        {
            using var resposta = await Api.EnviarAsync(token, metodo, caminho, corpo);
            var texto = await resposta.Content.ReadAsStringAsync();
            return (resposta.StatusCode, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
        }

        public Task<(HttpStatusCode Status, JsonNode? Corpo)> ComoAdmin(HttpMethod metodo, string caminho, object? corpo = null) => Chamar(Admin, metodo, caminho, corpo);

        public object Rascunho(string chave, string grafo = Simples, int? revisao = null) => new { organizationId = Org.Id, key = chave, name = "Fluxo de teste", graph = JsonNode.Parse(grafo), expectedRevision = revisao };
    }

    private async Task<Cenario> IniciarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var org = await banco.CriarOrganizacaoAsync(tenant, $"Org {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        return new Cenario { Api = api, Admin = await api.LoginAsync(admin), Tenant = tenant, Org = org };
    }

    [Fact]
    public async Task Admin_runs_the_whole_cycle_draft_validate_simulate_publish_new_version()
    {
        await using var c = await IniciarAsync("wf-ciclo");

        var (st, salvo) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("boas-vindas"));
        Assert.Equal(HttpStatusCode.OK, st);
        var id = salvo!["workflow"]!["id"]!.GetValue<Guid>();
        Assert.Equal("Draft", salvo["workflow"]!["status"]!.GetValue<string>());
        Assert.Empty(salvo["problems"]!.AsArray());

        var (stVal, val) = await c.ComoAdmin(HttpMethod.Post, "/workflows/validate", new { organizationId = c.Org.Id, graph = JsonNode.Parse(Simples) });
        Assert.Equal(HttpStatusCode.OK, stVal);
        Assert.True(val!["valid"]!.GetValue<bool>());

        var (stSim, sim) = await c.ComoAdmin(HttpMethod.Post, "/workflows/simulate", new { organizationId = c.Org.Id, graph = JsonNode.Parse(Simples), scenario = new { inputs = new[] { new { kind = "Say", text = "oi" } } } });
        Assert.Equal(HttpStatusCode.OK, stSim);
        Assert.Equal("completed", sim!["simulation"]!["outcome"]!.GetValue<string>());

        var (stPub, pub) = await c.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id}/publish");
        Assert.Equal(HttpStatusCode.OK, stPub);
        Assert.Equal("Published", pub!["status"]!.GetValue<string>());

        // Editar depois de publicar cria o rascunho da versão 2.
        var alterado = Simples.Replace("Posso ajudar?", "Como posso ajudar?");
        var (_, v2) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("boas-vindas", alterado));
        Assert.Equal(2, v2!["workflow"]!["version"]!.GetValue<int>());
        var id2 = v2["workflow"]!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.OK, (await c.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id2}/publish")).Status);

        var (_, versoes) = await c.ComoAdmin(HttpMethod.Get, "/workflows/definitions/by-key/boas-vindas/versions");
        Assert.Equal(["Published", "Retired"], versoes!.AsArray().Select(v => v!["status"]!.GetValue<string>()));

        var (_, detalhe) = await c.ComoAdmin(HttpMethod.Get, $"/workflows/definitions/{id}");
        Assert.Contains("Posso ajudar?", detalhe!["graphJson"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_graph_with_unknown_fields_or_problems_is_refused_or_reported()
    {
        await using var c = await IniciarAsync("wf-estrito");

        var (st, corpo) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("estrito", Simples.Replace("\"variables\":[]", "\"variables\":[],\"script\":\"x\"")));
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.DoesNotContain("script", corpo!.ToJsonString());

        var incompleto = """{"schemaVersion":1,"start":"oi","nodes":[{"id":"oi","type":"message","config":{"text":"x"}}],"edges":[]}""";
        var (stOk, salvo) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("incompleto", incompleto));
        Assert.Equal(HttpStatusCode.OK, stOk);
        Assert.Equal("port-unconnected", salvo!["problems"]![0]!["code"]!.GetValue<string>());
        var id = salvo["workflow"]!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id}/publish")).Status);
    }

    [Fact]
    public async Task Roles_and_the_function_gate_are_enforced()
    {
        await using var c = await IniciarAsync("wf-papeis");
        var (_, salvo) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("papeis"));
        var id = salvo!["workflow"]!["id"]!.GetValue<Guid>();

        // O gerente lê e simula, mas não edita nem publica.
        var gerente = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.Manager));
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Get, "/workflows/definitions")).Status);
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Get, $"/workflows/definitions/{id}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await c.Chamar(gerente, HttpMethod.Post, "/workflows/simulate", new { organizationId = c.Org.Id, graph = JsonNode.Parse(Simples), scenario = new { inputs = Array.Empty<object>() } })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(gerente, HttpMethod.Post, "/workflows/definitions", c.Rascunho("do-gerente"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(gerente, HttpMethod.Post, $"/workflows/definitions/{id}/publish")).Status);

        // Portão de função: quem não tem permissão nenhuma recebe 403 antes de qualquer consulta (nem 404 que sonde ids).
        var comum = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.User));
        foreach (var caminho in new[] { "/workflows/definitions", $"/workflows/definitions/{id}", $"/workflows/definitions/{Guid.NewGuid()}", "/workflows/definitions/by-key/papeis/versions" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(comum, HttpMethod.Get, caminho)).Status);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Chamar(null, HttpMethod.Get, "/workflows/definitions")).Status);
    }

    [Fact]
    public async Task Tenants_and_organizations_are_isolated()
    {
        await using var a = await IniciarAsync("wf-iso-a");
        var (_, salvo) = await a.ComoAdmin(HttpMethod.Post, "/workflows/definitions", a.Rascunho("isolado"));
        var id = salvo!["workflow"]!["id"]!.GetValue<Guid>();

        await using var b = await IniciarAsync("wf-iso-b");
        Assert.Equal(HttpStatusCode.NotFound, (await b.ComoAdmin(HttpMethod.Get, $"/workflows/definitions/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await b.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id}/publish")).Status);
        var (_, lista) = await b.ComoAdmin(HttpMethod.Get, "/workflows/definitions");
        Assert.Empty(lista!.AsArray());

        // Um administrador de OUTRA organização do mesmo tenant não enxerga nem altera o fluxo.
        var outraOrg = await banco.CriarOrganizacaoAsync(a.Tenant, "Outra org");
        var adminOutra = await a.Api.LoginAsync(await banco.CriarUsuarioAsync(a.Tenant, outraOrg.Id, Role.OrganizationAdmin));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Chamar(adminOutra, HttpMethod.Get, $"/workflows/definitions/{id}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Chamar(adminOutra, HttpMethod.Post, "/workflows/definitions", a.Rascunho("isolado", revisao: 1))).Status);
    }

    [Fact]
    public async Task A_published_version_is_immutable_in_the_database_itself()
    {
        await using var c = await IniciarAsync("wf-imutavel");
        var (_, salvo) = await c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("congelado"));
        var id = salvo!["workflow"]!["id"]!.GetValue<Guid>();

        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();

        // O rascunho pode mudar (é o que o editor faz).
        await using (var ok = new NpgsqlCommand("UPDATE workflows.workflow_versions SET name = 'Outro nome' WHERE id = @id", conexao))
        {
            ok.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await ok.ExecuteNonQueryAsync());
        }

        Assert.Equal(HttpStatusCode.OK, (await c.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id}/publish")).Status);

        foreach (var sql in new[]
        {
            "UPDATE workflows.workflow_versions SET graph_json = '{}' WHERE id = @id",
            "UPDATE workflows.workflow_versions SET name = 'adulterado' WHERE id = @id",
            "UPDATE workflows.workflow_versions SET status = 1, published_at = NULL WHERE id = @id",
            "DELETE FROM workflows.workflow_versions WHERE id = @id",
        })
        {
            await using var comando = new NpgsqlCommand(sql, conexao);
            comando.Parameters.AddWithValue("id", id);
            var erro = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal("23514", erro.SqlState);
        }

        // Aposentar continua permitido (muda só situação e data).
        Assert.Equal(HttpStatusCode.OK, (await c.ComoAdmin(HttpMethod.Post, $"/workflows/definitions/{id}/retire")).Status);
    }

    [Fact]
    public async Task Concurrent_first_saves_of_the_same_key_leave_one_draft_and_conflicts_for_the_rest()
    {
        await using var c = await IniciarAsync("wf-corrida");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.ComoAdmin(HttpMethod.Post, "/workflows/definitions", c.Rascunho("corrida"))));

        Assert.Equal(1, resultados.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(resultados.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        var (_, versoes) = await c.ComoAdmin(HttpMethod.Get, "/workflows/definitions/by-key/corrida/versions");
        Assert.Single(versoes!.AsArray());
    }

    [Fact]
    public async Task The_operations_list_is_empty_without_integrations_and_requires_read_permission()
    {
        await using var c = await IniciarAsync("wf-operacoes");
        var (st, corpo) = await c.ComoAdmin(HttpMethod.Get, $"/workflows/operations?organizationId={c.Org.Id}");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Empty(corpo!.AsArray());

        var comum = await c.Api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.Org.Id, Role.User));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Chamar(comum, HttpMethod.Get, $"/workflows/operations?organizationId={c.Org.Id}")).Status);
    }
}
