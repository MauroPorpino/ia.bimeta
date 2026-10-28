using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O cadastro de canais pela API real (HTTP, PostgreSQL real): fluxo do administrador, 401/403/404 conforme TEN-2, o tenant
/// nunca vindo da requisição, conflito de identificador, e a credencial só de escrita (nenhuma resposta traz o valor).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelsApiTests(PostgresFixture banco)
{
    private const string ValorSintetico = "EAAG-VALOR-SINTETICO-API-0123456789";

    private static Dictionary<string, string?> Configuracao(bool comChaveDeCifra)
    {
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false);
        if (comChaveDeCifra)
        {
            configuracao["Channels:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeTeste;
            configuracao[$"Channels:Secrets:Keys:{PostgresFixture.IdDaChaveDeTeste}"] = PostgresFixture.ChaveDeCifraDeTeste;
        }

        return configuracao;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resposta)
    {
        using var documento = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return documento.RootElement.Clone();
    }

    private async Task<(CenarioDeConversas Cenario, ApiServer Api, string Admin)> MontarAsync(string prefixo, bool comChaveDeCifra = true)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var admin = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.OrganizationAdmin);
        var api = await ApiServer.StartAsync("Development", Configuracao(comChaveDeCifra));
        return (c, api, await api.LoginAsync(admin));
    }

    private static async Task<JsonElement> CriarContaAsync(ApiServer api, string token, Guid organizacao, string? waba = null) =>
        await api.CriarAsync<JsonElement>(token, "/channels/accounts", new { organizationId = organizacao, provider = "WhatsAppCloud", providerAccountId = waba ?? ChannelsTestSupportNome("waba"), name = "Conta da API" });

    private static string ChannelsTestSupportNome(string prefixo) => $"{prefixo}-{Guid.NewGuid():N}";

    private static async Task<JsonElement> CriarNumeroAsync(ApiServer api, string token, Guid conta, string? numero = null) =>
        await api.CriarAsync<JsonElement>(token, "/channels/instances", new { accountId = conta, name = "Número da API", phoneNumberId = numero ?? ChannelsTestSupportNome("num"), displayPhoneNumber = "+55 11 99999-0000", defaultLocale = "pt-BR" });

    [Fact]
    public async Task O_administrador_cadastra_conta_e_numero_autoriza_unidades_define_padroes_e_ativa()
    {
        var (c, api, token) = await MontarAsync("apicanais");
        await using var _ = api;
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var setor = await c.CriarSetorAsync(matriz, "Recepção");
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");

        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var numero = await CriarNumeroAsync(api, token, conta.GetProperty("id").GetGuid());
        var id = numero.GetProperty("id").GetGuid();
        Assert.Equal(("Inactive", "pt-BR", c.OrganizacaoId), (numero.GetProperty("status").GetString(), numero.GetProperty("defaultLocale").GetString(), numero.GetProperty("organizationId").GetGuid()));

        using var a1 = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/instances/{id}/authorized-units/{matriz}");
        using var a2 = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/instances/{id}/authorized-units/{filial}");
        using var repetida = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/instances/{id}/authorized-units/{filial}");
        using var padroes = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/instances/{id}/defaults", new { defaultBusinessUnitId = matriz, defaultDepartmentId = setor });
        using var ativado = await api.EnviarAsync(token, HttpMethod.Post, $"/channels/instances/{id}/activate");

        Assert.All(new[] { a1, a2, repetida, padroes, ativado }, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var final = await JsonAsync(ativado);
        Assert.Equal(("Active", matriz, setor), (final.GetProperty("status").GetString(), final.GetProperty("defaultBusinessUnitId").GetGuid(), final.GetProperty("defaultDepartmentId").GetGuid()));
        Assert.Equal(new[] { matriz, filial }.Order(), final.GetProperty("authorizedBusinessUnitIds").EnumerateArray().Select(e => e.GetGuid()).Order());

        var lido = await api.LerAsync<JsonElement>(token, $"/channels/instances/{id}");
        var lista = await api.LerAsync<JsonElement>(token, "/channels/instances");
        Assert.Equal(id, lido.GetProperty("id").GetGuid());
        Assert.Equal(id, Assert.Single(lista.EnumerateArray().ToList()).GetProperty("id").GetGuid());
        var contas = await api.LerAsync<JsonElement>(token, "/channels/accounts");
        Assert.Equal(conta.GetProperty("id").GetGuid(), Assert.Single(contas.EnumerateArray().ToList()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Retirar_a_unidade_padrao_responde_400_e_trocar_o_padrao_antes_permite()
    {
        var (c, api, token) = await MontarAsync("apicanaisretirar");
        await using var _ = api;
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial }, unidadePadrao: matriz);

        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Delete, $"/channels/instances/{canal.Value}/authorized-units/{matriz}"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Put, $"/channels/instances/{canal.Value}/defaults", new { defaultBusinessUnitId = filial }));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Delete, $"/channels/instances/{canal.Value}/authorized-units/{matriz}"));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Put, $"/channels/instances/{canal.Value}/defaults", new { defaultBusinessUnitId = matriz }));
    }

    [Fact]
    public async Task Sem_token_toda_rota_de_canais_responde_401()
    {
        var (c, api, _) = await MontarAsync("apicanais401");
        await using var _ = api;
        var id = Guid.NewGuid();

        var status = new[]
        {
            await api.StatusAsync(null, HttpMethod.Get, "/channels/accounts"),
            await api.StatusAsync(null, HttpMethod.Get, "/channels/instances"),
            await api.StatusAsync(null, HttpMethod.Get, $"/channels/instances/{id}"),
            await api.StatusAsync(null, HttpMethod.Post, "/channels/accounts", new { organizationId = c.OrganizacaoId, provider = "WhatsAppCloud", providerAccountId = "x", name = "x" }),
            await api.StatusAsync(null, HttpMethod.Put, $"/channels/accounts/{id}/secrets/AccessToken", new { value = ValorSintetico }),
            await api.StatusAsync(null, HttpMethod.Put, $"/channels/instances/{id}/authorized-units/{id}"),
            await api.StatusAsync(null, HttpMethod.Post, $"/channels/instances/{id}/activate"),
        };

        Assert.All(status, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    [Fact]
    public async Task O_gestor_le_mas_nao_cadastra_e_o_usuario_comum_nao_le_nem_gere()
    {
        var (c, api, token) = await MontarAsync("apicanaisperfis");
        await using var _ = api;
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var numero = await CriarNumeroAsync(api, token, conta.GetProperty("id").GetGuid());
        var id = numero.GetProperty("id").GetGuid();
        var gestor = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.Manager));
        var comum = await api.LoginAsync(await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User));

        // O gestor lê.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestor, HttpMethod.Get, $"/channels/instances/{id}"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestor, HttpMethod.Get, "/channels/instances"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(gestor, HttpMethod.Get, "/channels/accounts"));
        // ...mas não gere: 403 em toda escrita.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Put, $"/channels/instances/{id}/authorized-units/{unidade}"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Post, $"/channels/instances/{id}/activate"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Put, $"/channels/accounts/{conta.GetProperty("id").GetGuid()}/secrets/AccessToken", new { value = ValorSintetico }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Post, "/channels/accounts", new { organizationId = c.OrganizacaoId, provider = "WhatsAppCloud", providerAccountId = "x-gestor", name = "x" }));
        // O usuário comum não lê organização... nem canais: a lista é negada e o recurso responde como inexistente (TEN-2).
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comum, HttpMethod.Get, "/channels/instances"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(comum, HttpMethod.Get, "/channels/accounts"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(comum, HttpMethod.Get, $"/channels/instances/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(comum, HttpMethod.Put, $"/channels/instances/{id}/authorized-units/{unidade}"));
    }

    [Fact]
    public async Task Outro_tenant_ve_404_e_lista_vazia_e_o_tenant_nunca_vem_do_corpo()
    {
        var (c, api, token) = await MontarAsync("apicanaistenanta");
        await using var _ = api;
        var outro = await CenarioDeConversas.CriarAsync(banco, "apicanaistenantb");
        var adminDoOutro = await api.LoginAsync(await banco.CriarAdministradorDoTenantAsync(outro.Tenant));
        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var numero = await CriarNumeroAsync(api, token, conta.GetProperty("id").GetGuid());
        var id = numero.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Get, $"/channels/instances/{id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Post, $"/channels/instances/{id}/activate"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(adminDoOutro, HttpMethod.Put, $"/channels/accounts/{conta.GetProperty("id").GetGuid()}/secrets/AccessToken", new { value = ValorSintetico }));
        Assert.Empty((await api.LerAsync<JsonElement>(adminDoOutro, "/channels/instances")).EnumerateArray());

        // Um tenantId no corpo é ignorado: a conta é criada no tenant da sessão, e o outro tenant não a vê.
        using var comTenantNoCorpo = await api.EnviarAsync(adminDoOutro, HttpMethod.Post, "/channels/accounts", new
        {
            tenantId = c.Tenant.TenantId.Value,
            organizationId = outro.OrganizacaoId,
            provider = "WhatsAppCloud",
            providerAccountId = ChannelsTestSupportNome("waba"),
            name = "Conta do outro",
        });
        Assert.Equal(HttpStatusCode.Created, comTenantNoCorpo.StatusCode);
        var criada = await JsonAsync(comTenantNoCorpo);
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"/channels/accounts/{criada.GetProperty("id").GetGuid()}"));
        Assert.Equal(outro.OrganizacaoId, criada.GetProperty("organizationId").GetGuid());
    }

    [Fact]
    public async Task Dados_invalidos_respondem_400_e_identificador_repetido_responde_409_sem_revelar_o_dono()
    {
        var (c, api, token) = await MontarAsync("apicanaisvalida");
        await using var _ = api;
        var outro = await CenarioDeConversas.CriarAsync(banco, "apicanaisvalidaoutro");
        var adminDoOutro = await api.LoginAsync(await banco.CriarAdministradorDoTenantAsync(outro.Tenant));
        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var contaId = conta.GetProperty("id").GetGuid();
        var contaDoOutro = await CriarContaAsync(api, adminDoOutro, outro.OrganizacaoId);
        var numero = ChannelsTestSupportNome("num");
        await CriarNumeroAsync(api, token, contaId, numero);
        var unidadeDeOutraOrganizacao = await c.CriarUnidadeAsync((await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização")).Id, "Filial da outra", matriz: true);
        var instancia = await CriarNumeroAsync(api, token, contaId);
        var instanciaId = instancia.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/channels/instances", new { accountId = contaId, name = "x", phoneNumberId = "com espaco" }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/channels/instances", new { accountId = contaId, name = "  ", phoneNumberId = "valido-1" }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/channels/accounts", new { organizationId = c.OrganizacaoId, provider = "Outro", providerAccountId = "x", name = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, "/channels/instances", new { accountId = contaId, name = "x", phoneNumberId = "valido-2", defaultLocale = "pt_BR" }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Put, $"/channels/instances/{instanciaId}/authorized-units/{unidadeDeOutraOrganizacao}"));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Put, $"/channels/instances/{instanciaId}/defaults", new { defaultBusinessUnitId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, $"/channels/instances/{instanciaId}/activate")); // sem unidade autorizada
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Post, "/channels/instances", new { accountId = Guid.NewGuid(), name = "x", phoneNumberId = "valido-3" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Post, "/channels/accounts", new { organizationId = Guid.NewGuid(), provider = "WhatsAppCloud", providerAccountId = "x", name = "x" }));

        using var repetidoNoMesmo = await api.EnviarAsync(token, HttpMethod.Post, "/channels/instances", new { accountId = contaId, name = "x", phoneNumberId = numero });
        using var repetidoNoOutro = await api.EnviarAsync(adminDoOutro, HttpMethod.Post, "/channels/instances", new { accountId = contaDoOutro.GetProperty("id").GetGuid(), name = "x", phoneNumberId = numero });
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict), (repetidoNoMesmo.StatusCode, repetidoNoOutro.StatusCode));
        var corpoDoOutro = await repetidoNoOutro.Content.ReadAsStringAsync();
        Assert.Equal((await repetidoNoMesmo.Content.ReadAsStringAsync()).Length, corpoDoOutro.Length);
        Assert.DoesNotContain(c.Tenant.Slug, corpoDoOutro, StringComparison.Ordinal);
        Assert.DoesNotContain(numero, corpoDoOutro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credencial_e_so_de_escrita_nenhuma_resposta_traz_o_valor()
    {
        var (c, api, token) = await MontarAsync("apisegredo");
        await using var _ = api;
        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var contaId = conta.GetProperty("id").GetGuid();

        using var definida = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/accounts/{contaId}/secrets/AccessToken", new { value = ValorSintetico });
        using var girada = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/accounts/{contaId}/secrets/accesstoken", new { value = ValorSintetico + "-novo" });
        using var lida = await api.EnviarAsync(token, HttpMethod.Get, $"/channels/accounts/{contaId}");
        using var lista = await api.EnviarAsync(token, HttpMethod.Get, "/channels/accounts");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (definida.StatusCode, girada.StatusCode, lida.StatusCode, lista.StatusCode));
        foreach (var resposta in new[] { definida, girada, lida, lista })
        {
            var texto = await resposta.Content.ReadAsStringAsync();
            Assert.DoesNotContain("SINTETICO", texto, StringComparison.Ordinal);
            Assert.DoesNotContain("ciphertext", texto, StringComparison.OrdinalIgnoreCase);
        }

        var primeira = await JsonAsync(definida);
        var segunda = await JsonAsync(girada);
        Assert.Equal(("AccessToken", 1, 2), (primeira.GetProperty("kind").GetString(), primeira.GetProperty("version").GetInt32(), segunda.GetProperty("version").GetInt32()));
        Assert.Equal(primeira.GetProperty("reference").GetGuid(), segunda.GetProperty("reference").GetGuid());
        var segredos = (await JsonAsync(lida)).GetProperty("secrets").EnumerateArray().ToList();
        Assert.Equal(primeira.GetProperty("reference").GetGuid(), Assert.Single(segredos).GetProperty("reference").GetGuid());
    }

    [Fact]
    public async Task Papel_ou_valor_invalido_responde_400_sem_ecoar_o_valor()
    {
        var (c, api, token) = await MontarAsync("apisegredoinvalido");
        await using var _ = api;
        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var contaId = conta.GetProperty("id").GetGuid();

        using var papel = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/accounts/{contaId}/secrets/Outro", new { value = ValorSintetico });
        using var espaco = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/accounts/{contaId}/secrets/AppSecret", new { value = " " + ValorSintetico });
        using var vazio = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/accounts/{contaId}/secrets/AppSecret", new { value = "" });

        Assert.All(new[] { papel, espaco, vazio }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.DoesNotContain("SINTETICO", await espaco.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty((await api.LerAsync<JsonElement>(token, $"/channels/accounts/{contaId}")).GetProperty("secrets").EnumerateArray());
    }

    [Fact]
    public async Task Sem_chave_de_cifra_configurada_a_gravacao_da_credencial_responde_503_e_nada_e_gravado()
    {
        var (c, api, token) = await MontarAsync("apisegredosemchave", comChaveDeCifra: false);
        await using var _ = api;
        var conta = await CriarContaAsync(api, token, c.OrganizacaoId);
        var contaId = conta.GetProperty("id").GetGuid();

        using var resposta = await api.EnviarAsync(token, HttpMethod.Put, $"/channels/accounts/{contaId}/secrets/AccessToken", new { value = ValorSintetico });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SINTETICO", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveKeyId", texto, StringComparison.Ordinal);
        Assert.Empty((await api.LerAsync<JsonElement>(token, $"/channels/accounts/{contaId}")).GetProperty("secrets").EnumerateArray());
        // O resto do cadastro continua funcionando sem a chave.
        Assert.Equal(HttpStatusCode.Created, (await api.EnviarAsync(token, HttpMethod.Post, "/channels/instances", new { accountId = contaId, name = "x", phoneNumberId = ChannelsTestSupportNome("num") })).StatusCode);
    }
}
