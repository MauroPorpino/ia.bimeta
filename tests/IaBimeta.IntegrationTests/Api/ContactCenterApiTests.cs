using System.Net;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// As filas de atendimento pela API real (HTTP, PostgreSQL real): fluxo do administrador, 401/403/404 conforme TEN-2, conflito de
/// nome, dados inválidos, e o tenant nunca vindo da requisição.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactCenterApiTests(PostgresFixture banco)
{
    private async Task<(CenarioDeConversas Cenario, ApiServer Api, string Admin)> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var admin = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.OrganizationAdmin);
        var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        return (c, api, await api.LoginAsync(admin));
    }

    private static Task<JsonElement> CriarFilaAsync(ApiServer api, string token, Guid organizacao, string nome, Guid? unidade = null, Guid? setor = null) =>
        api.CriarAsync<JsonElement>(token, "/contact-center/queues", new { organizationId = organizacao, businessUnitId = unidade, departmentId = setor, name = nome });

    [Fact]
    public async Task O_administrador_cria_a_fila_coloca_e_tira_atendentes_pausa_e_retoma()
    {
        var (c, api, token) = await MontarAsync("apifila");
        await using var _ = api;
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var setor = await c.CriarSetorAsync(matriz, "Recepção");
        var atendente = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);

        var fila = await CriarFilaAsync(api, token, c.OrganizacaoId, "Recepção da matriz", matriz, setor);
        var id = fila.GetProperty("id").GetGuid();
        Assert.Equal(("Active", matriz, setor, 1L), (fila.GetProperty("status").GetString(), fila.GetProperty("businessUnitId").GetGuid(), fila.GetProperty("departmentId").GetGuid(), fila.GetProperty("version").GetInt64()));

        using var entrou = await api.EnviarAsync(token, HttpMethod.Put, $"/contact-center/queues/{id}/members/{atendente.Id}", new { role = "Agent" });
        using var promovido = await api.EnviarAsync(token, HttpMethod.Put, $"/contact-center/queues/{id}/members/{atendente.Id}", new { role = "Supervisor" });
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (entrou.StatusCode, promovido.StatusCode));
        var membros = await api.LerAsync<JsonElement>(token, $"/contact-center/queues/{id}/members");
        var membro = Assert.Single(membros.EnumerateArray().ToList());
        Assert.Equal((atendente.Id, "Supervisor"), (membro.GetProperty("userId").GetGuid(), membro.GetProperty("role").GetString()));

        using var pausada = await api.EnviarAsync(token, HttpMethod.Post, $"/contact-center/queues/{id}/pause");
        Assert.Equal("Paused", (await api.LerAsync<JsonElement>(token, $"/contact-center/queues/{id}")).GetProperty("status").GetString());
        using var retomada = await api.EnviarAsync(token, HttpMethod.Post, $"/contact-center/queues/{id}/resume");
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (pausada.StatusCode, retomada.StatusCode));

        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(token, HttpMethod.Delete, $"/contact-center/queues/{id}/members/{atendente.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(token, HttpMethod.Delete, $"/contact-center/queues/{id}/members/{atendente.Id}")); // repetir é inofensivo
        Assert.Empty((await api.LerAsync<JsonElement>(token, $"/contact-center/queues/{id}/members")).EnumerateArray());
        var lista = await api.LerAsync<JsonElement>(token, "/contact-center/queues");
        Assert.Equal(id, Assert.Single(lista.EnumerateArray().ToList()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Dados_invalidos_respondem_400_e_nome_repetido_responde_409()
    {
        var (c, api, token) = await MontarAsync("apifilaerros");
        await using var _ = api;
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        var fila = await CriarFilaAsync(api, token, c.OrganizacaoId, "Triagem");
        var id = fila.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/contact-center/queues", new { organizationId = c.OrganizacaoId, name = "   " }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/contact-center/queues", new { organizationId = c.OrganizacaoId, businessUnitId = Guid.NewGuid(), name = "Unidade inexistente" }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/contact-center/queues", new { organizationId = c.OrganizacaoId, departmentId = Guid.NewGuid(), name = "Setor sem unidade" }));
        Assert.Equal(HttpStatusCode.Conflict, await api.StatusAsync(token, HttpMethod.Post, "/contact-center/queues", new { organizationId = c.OrganizacaoId, name = "TRIAGEM" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Put, $"/contact-center/queues/{id}/members/{Guid.NewGuid()}", new { role = "Agent" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Put, $"/contact-center/queues/{Guid.NewGuid()}/members/{usuario.Id}", new { role = "Agent" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Post, "/contact-center/queues", new { organizationId = Guid.NewGuid(), name = "Organização inexistente" }));
        Assert.NotEqual(Guid.Empty, matriz);
    }

    [Fact]
    public async Task Sem_token_toda_rota_de_filas_responde_401()
    {
        var (c, api, _) = await MontarAsync("apifila401");
        await using var _ = api;
        var id = Guid.NewGuid();

        var status = new[]
        {
            await api.StatusAsync(null, HttpMethod.Get, "/contact-center/queues"),
            await api.StatusAsync(null, HttpMethod.Get, $"/contact-center/queues/{id}"),
            await api.StatusAsync(null, HttpMethod.Post, "/contact-center/queues", new { organizationId = c.OrganizacaoId, name = "x" }),
            await api.StatusAsync(null, HttpMethod.Post, $"/contact-center/queues/{id}/pause"),
            await api.StatusAsync(null, HttpMethod.Post, $"/contact-center/queues/{id}/resume"),
            await api.StatusAsync(null, HttpMethod.Get, $"/contact-center/queues/{id}/members"),
            await api.StatusAsync(null, HttpMethod.Put, $"/contact-center/queues/{id}/members/{id}", new { role = "Agent" }),
            await api.StatusAsync(null, HttpMethod.Delete, $"/contact-center/queues/{id}/members/{id}"),
        };

        Assert.All(status, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task O_gestor_le_mas_nao_gere_e_o_usuario_comum_nao_le_nem_gere()
    {
        var (c, api, token) = await MontarAsync("apifilaperfis");
        await using var _ = api;
        var fila = await CriarFilaAsync(api, token, c.OrganizacaoId, "Perfis");
        var id = fila.GetProperty("id").GetGuid();
        var atendente = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        var gestor = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager));
        var comum = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User));

        // O gestor lê.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestor, HttpMethod.Get, "/contact-center/queues"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestor, HttpMethod.Get, $"/contact-center/queues/{id}"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestor, HttpMethod.Get, $"/contact-center/queues/{id}/members"));
        // ...mas não gere: 403 em toda escrita.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Post, "/contact-center/queues", new { organizationId = c.OrganizacaoId, name = "Do gestor" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Post, $"/contact-center/queues/{id}/pause"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Put, $"/contact-center/queues/{id}/members/{atendente.Id}", new { role = "Agent" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Delete, $"/contact-center/queues/{id}/members/{atendente.Id}"));
        // O usuário comum não lê filas: a lista é negada e o recurso responde como inexistente (TEN-2).
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comum, HttpMethod.Get, "/contact-center/queues"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(comum, HttpMethod.Get, $"/contact-center/queues/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(comum, HttpMethod.Get, $"/contact-center/queues/{id}/members"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(comum, HttpMethod.Post, $"/contact-center/queues/{id}/pause"));
    }

    [Fact]
    public async Task Outro_tenant_ve_404_e_lista_vazia_e_o_tenant_nunca_vem_do_corpo()
    {
        var (c, api, token) = await MontarAsync("apifilatenanta");
        await using var _ = api;
        var outro = await CenarioDeConversas.CriarAsync(banco, "apifilatenantb");
        var adminDoOutro = await api.LoginAsync(await banco.CriarAdministradorDoTenantAsync(outro.Tenant));
        var fila = await CriarFilaAsync(api, token, c.OrganizacaoId, "Da primeira");
        var id = fila.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Get, $"/contact-center/queues/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Post, $"/contact-center/queues/{id}/pause"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Get, $"/contact-center/queues/{id}/members"));
        Assert.Empty((await api.LerAsync<JsonElement>(adminDoOutro, "/contact-center/queues")).EnumerateArray());

        // Um tenantId no corpo é ignorado: a fila é criada no tenant da sessão, e a organização do outro tenant nem é vista.
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Post, "/contact-center/queues", new { tenantId = c.Tenant.TenantId.Value, organizationId = c.OrganizacaoId, name = "Intrusa" }));
        Assert.Single((await api.LerAsync<JsonElement>(token, "/contact-center/queues")).EnumerateArray().ToList());
    }
}
