using System.Net;
using System.Net.Http.Json;
using IaBimeta.Api;
using IaBimeta.Tenancy.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A API de verdade, por HTTP: o tenant só é aceito pela fonte confiável (cabeçalho de desenvolvimento, isolado do
/// ambiente de produção), nunca por corpo ou rota, e sem contexto as operações de tenant respondem 401.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiTenantContextTests(PostgresFixture banco)
{
    private sealed record OrganizacaoDto(Guid Id, string Name, DateTimeOffset CreatedAt);

    private static JsonContent Corpo(object corpo) => JsonContent.Create(corpo);

    private static async Task<OrganizacaoDto> CriarOrganizacaoAsync(ApiServer api, string tenantSlug, string nome)
    {
        using var resposta = await api.Client.SendAsync(api.Requisicao(HttpMethod.Post, "/organizations", tenantSlug, Corpo(new { name = nome })));
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<OrganizacaoDto>())!;
    }

    private static async Task<IReadOnlyList<OrganizacaoDto>> ListarAsync(ApiServer api, string tenantSlug)
    {
        using var resposta = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations", tenantSlug));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<List<OrganizacaoDto>>())!;
    }

    [Fact]
    public async Task Health_responde_ok_em_producao_sem_banco_configurado()
    {
        await using var api = await ApiServer.StartAsync("Production", new Dictionary<string, string?>());

        using var resposta = await api.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("ok", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_responde_ok_em_desenvolvimento_mesmo_sem_string_de_conexao()
    {
        // Cabeçalho de desenvolvimento ligado, mas nenhuma string de conexão: o /health não pode depender do banco.
        var configuracao = new Dictionary<string, string?> { ["Tenancy:Development:HeaderEnabled"] = "true" };
        await using var api = await ApiServer.StartAsync("Development", configuracao);

        using var resposta = await api.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Health_continua_respondendo_em_desenvolvimento()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());

        using var resposta = await api.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Producao_nao_aceita_o_cabecalho_de_desenvolvimento_e_rejeita_a_operacao_de_tenant()
    {
        var tenant = await banco.RegistrarTenantAsync("prod");
        var configuracao = new Dictionary<string, string?> { ["ConnectionStrings:Tenancy"] = TestDatabase.ConnectionString };
        await using var api = await ApiServer.StartAsync("Production", configuracao);

        using var resposta = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations", tenant.Slug));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_partida_falha_se_o_recurso_de_desenvolvimento_for_ligado_fora_de_Development(string ambiente)
    {
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento();

        var falha = Assert.Throws<InvalidOperationException>(() => ApiHost.Build(
            new[] { "--environment=" + ambiente },
            builder => builder.Configuration.AddInMemoryCollection(configuracao)));

        Assert.Contains("Development", falha.Message);
    }

    [Fact]
    public async Task Em_desenvolvimento_sem_habilitar_o_cabecalho_ele_e_ignorado()
    {
        var tenant = await banco.RegistrarTenantAsync("desabilitado");
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));

        using var resposta = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations", tenant.Slug));

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Sem_contexto_confiavel_todas_as_rotas_de_tenant_respondem_401()
    {
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());

        using var lista = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations"));
        using var criacao = await api.Client.SendAsync(api.Requisicao(HttpMethod.Post, "/organizations", conteudo: Corpo(new { name = "Sem tenant" })));
        using var busca = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations/" + Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, lista.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, criacao.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, busca.StatusCode);
    }

    [Fact]
    public async Task Tenant_desconhecido_ou_suspenso_nao_recebe_contexto()
    {
        var suspenso = await banco.RegistrarTenantAsync("suspenso");
        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().SuspendAsync(suspenso.Slug);
        }

        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());

        using var desconhecido = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations", "nao-existe-" + Guid.NewGuid().ToString("N")[..8]));
        using var suspensa = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations", suspenso.Slug));

        Assert.Equal(HttpStatusCode.Unauthorized, desconhecido.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, suspensa.StatusCode);
    }

    [Fact]
    public async Task Cabecalho_com_dois_valores_e_rejeitado()
    {
        var tenant = await banco.RegistrarTenantAsync("duplo");
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());
        using var requisicao = api.Requisicao(HttpMethod.Get, "/organizations");
        requisicao.Headers.Add("X-Dev-Tenant", new[] { tenant.Slug, tenant.Slug });

        using var resposta = await api.Client.SendAsync(requisicao);

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Dois_tenants_pela_API_cada_um_so_ve_e_so_alcanca_o_que_e_seu()
    {
        var a = await banco.RegistrarTenantAsync("api-a");
        var b = await banco.RegistrarTenantAsync("api-b");
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());

        var deA = await CriarOrganizacaoAsync(api, a.Slug, "Matriz de A");
        var deB = await CriarOrganizacaoAsync(api, b.Slug, "Matriz de B");

        Assert.Equal(new[] { "Matriz de A" }, (await ListarAsync(api, a.Slug)).Select(o => o.Name));
        Assert.Equal(new[] { "Matriz de B" }, (await ListarAsync(api, b.Slug)).Select(o => o.Name));

        using var propriaA = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations/" + deA.Id, a.Slug));
        using var cruzadaDeBparaA = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations/" + deA.Id, b.Slug));
        using var cruzadaDeAparaB = await api.Client.SendAsync(api.Requisicao(HttpMethod.Get, "/organizations/" + deB.Id, a.Slug));
        Assert.Equal(HttpStatusCode.OK, propriaA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cruzadaDeBparaA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cruzadaDeAparaB.StatusCode);
    }

    [Fact]
    public async Task Tenant_informado_no_corpo_ou_na_rota_nao_e_aceito_como_identidade()
    {
        var a = await banco.RegistrarTenantAsync("corpo-a");
        var b = await banco.RegistrarTenantAsync("corpo-b");
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());

        // O corpo tenta empurrar a organização para o tenant A, mas a requisição é do tenant B.
        using var resposta = await api.Client.SendAsync(api.Requisicao(
            HttpMethod.Post,
            "/organizations?tenantId=" + a.TenantId.Value,
            b.Slug,
            Corpo(new { name = "Tentativa", tenantId = a.TenantId.Value })));

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(new[] { "Tentativa" }, (await ListarAsync(api, b.Slug)).Select(o => o.Name));
        Assert.Empty(await ListarAsync(api, a.Slug));
    }

    [Fact]
    public async Task Nome_invalido_responde_400_e_nome_repetido_no_tenant_responde_409()
    {
        var tenant = await banco.RegistrarTenantAsync("validacao");
        await using var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento());

        using var vazio = await api.Client.SendAsync(api.Requisicao(HttpMethod.Post, "/organizations", tenant.Slug, Corpo(new { name = "  " })));
        await CriarOrganizacaoAsync(api, tenant.Slug, "Unica");
        using var repetido = await api.Client.SendAsync(api.Requisicao(HttpMethod.Post, "/organizations", tenant.Slug, Corpo(new { name = "Unica" })));

        Assert.Equal(HttpStatusCode.BadRequest, vazio.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
    }
}
