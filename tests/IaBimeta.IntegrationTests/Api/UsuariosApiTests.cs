using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A administração de usuários (Etapa 01) pela API REAL (HTTP, PostgreSQL real): o contrato que a tela React consome, as proteções de elevação de privilégio, a impossibilidade de o tenant alterar o próprio limite e a
/// habilitação, e o cenário de um atendente desativado com atendimentos em andamento. Complementa os testes do serviço (<c>UserAdmissionTests</c>), que provam a concorrência.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UsuariosApiTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ApiServer Api, UsuarioCriado AdminCriado, string Admin);

    private async Task<Cena> MontarAsync(string prefixo, int? limite = null, bool cadastro = true)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().SetAsync(c.Tenant.Slug, limite, cadastro);
        }

        var api = await ApiServer.StartAsync("Development", ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false));
        var adminCriado = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        return new Cena(c, api, adminCriado, await api.LoginAsync(adminCriado));
    }

    private static async Task<JsonElement> CorpoAsync(HttpResponseMessage resposta) => await resposta.Content.ReadFromJsonAsync<JsonElement>();

    private static string Email(string prefixo) => $"{prefixo}{Guid.NewGuid().ToString("N")[..8]}@teste.local";

    // Contrato consumido pela tela

    [Fact]
    public async Task O_contrato_da_API_traz_permissoes_efetivas_uso_catalogo_listagem_e_token_uma_unica_vez()
    {
        var s = await MontarAsync("apiusr", limite: 5);
        await using var servidor = s.Api;

        var eu = await s.Api.LerAsync<JsonElement>(s.Admin, "/auth/me");
        var permissoes = eu.GetProperty("permissions").EnumerateArray().Select(p => (p.GetProperty("permission").GetString(), p.GetProperty("organizationId").ValueKind)).ToList();
        Assert.Contains(("UserPermissionManage", JsonValueKind.Null), permissoes);
        Assert.Contains(("UserStatusManage", JsonValueKind.Null), permissoes);

        var uso = await s.Api.LerAsync<JsonElement>(s.Admin, "/users/usage");
        Assert.Equal(5, uso.GetProperty("limit").GetInt32());
        Assert.True(uso.GetProperty("registrationEnabled").GetBoolean());
        Assert.Equal((1, 4, false, 0), (uso.GetProperty("seatsUsed").GetInt32(), uso.GetProperty("available").GetInt32(), uso.GetProperty("overLimit").GetBoolean(), uso.GetProperty("excess").GetInt32()));

        var catalogo = await s.Api.LerAsync<JsonElement>(s.Admin, "/users/permission-catalog");
        Assert.Contains(catalogo.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("permission").GetString() == "UserStatusManage" && p.GetProperty("group").GetString() == "Usuários");
        Assert.DoesNotContain(catalogo.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("permission").GetString() == "SystemOperation");
        Assert.Equal(new[] { "Administrador", "Supervisor", "Atendente", "Usuário" }, catalogo.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("profile").GetString()).ToArray());

        var email = Email("nova");
        using var admitida = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, $"/organizations/{s.C.OrganizacaoId}/users/admissions", new { email, displayName = "Nova Pessoa", role = "Attendant" });
        Assert.Equal(HttpStatusCode.Created, admitida.StatusCode);
        Assert.Contains("no-store", admitida.Headers.CacheControl?.ToString() ?? string.Empty);
        var corpo = await CorpoAsync(admitida);
        Assert.StartsWith("ibr_", corpo.GetProperty("token").GetString());
        Assert.Equal("Pending", corpo.GetProperty("user").GetProperty("status").GetString());
        var id = corpo.GetProperty("user").GetProperty("id").GetGuid();

        // O token nunca volta numa leitura posterior.
        var lista = await s.Api.LerAsync<JsonElement>(s.Admin, "/users?search=nova&status=pending&page=1&pageSize=20");
        Assert.Equal(1, lista.GetProperty("total").GetInt32());
        var item = lista.GetProperty("items")[0];
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.False(item.TryGetProperty("token", out _));
        Assert.Equal("Attendant", item.GetProperty("grants")[0].GetProperty("role").GetString());
        var detalhe = await s.Api.LerAsync<JsonElement>(s.Admin, $"/users/{id}");
        Assert.True(detalhe.GetProperty("hasLiveFirstAccessToken").GetBoolean());
        Assert.Contains(detalhe.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("permission").GetString() == "ConversationManage" && p.GetProperty("inProfile").GetBoolean() && p.GetProperty("effective").GetBoolean());
        Assert.Equal(2, (await s.Api.LerAsync<JsonElement>(s.Admin, "/users/usage")).GetProperty("seatsUsed").GetInt32());

        // Primeiro acesso pela rota pública existente: a pessoa define a PRÓPRIA senha e entra.
        using var resgate = await s.Api.Client.PostAsJsonAsync("/auth/password-reset/redeem", new { tenant = s.C.Tenant.Slug, email, token = corpo.GetProperty("token").GetString(), newPassword = PostgresFixture.PasswordPadrao });
        Assert.Equal(HttpStatusCode.NoContent, resgate.StatusCode);
        Assert.Equal("Active", (await s.Api.LerAsync<JsonElement>(s.Admin, $"/users/{id}")).GetProperty("status").GetString());
        using var segundoUso = await s.Api.Client.PostAsJsonAsync("/auth/password-reset/redeem", new { tenant = s.C.Tenant.Slug, email, token = corpo.GetProperty("token").GetString(), newPassword = PostgresFixture.PasswordPadrao + "x" });
        Assert.Equal(HttpStatusCode.BadRequest, segundoUso.StatusCode);
    }

    [Fact]
    public async Task Limite_e_cadastro_desabilitado_respondem_409_com_o_codigo_que_a_tela_trata()
    {
        var s = await MontarAsync("apilim", limite: 2);
        await using var _ = s.Api;
        Assert.Equal(HttpStatusCode.Created, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/organizations/{s.C.OrganizacaoId}/users/admissions", new { email = Email("a"), displayName = "A", role = "User" }));

        using var cheio = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, $"/organizations/{s.C.OrganizacaoId}/users/admissions", new { email = Email("b"), displayName = "B", role = "User" });
        Assert.Equal(HttpStatusCode.Conflict, cheio.StatusCode);
        var corpo = await CorpoAsync(cheio);
        Assert.Equal("user-limit-reached", corpo.GetProperty("code").GetString());
        Assert.Equal((2, 2), (corpo.GetProperty("limit").GetInt32(), corpo.GetProperty("used").GetInt32()));

        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().SetAsync(s.C.Tenant.Slug, 10, false);
        }

        using var desabilitado = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, $"/organizations/{s.C.OrganizacaoId}/users/admissions", new { email = Email("c"), displayName = "C", role = "User" });
        Assert.Equal(HttpStatusCode.Conflict, desabilitado.StatusCode);
        Assert.Equal("registration-disabled", (await CorpoAsync(desabilitado)).GetProperty("code").GetString());
        var uso = await s.Api.LerAsync<JsonElement>(s.Admin, "/users/usage");
        Assert.False(uso.GetProperty("registrationEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(null, HttpMethod.Get, "/users/usage"));
    }

    // O tenant nunca altera o próprio limite nem a habilitação

    [Fact]
    public async Task Nenhuma_rota_do_tenant_altera_limite_ou_habilitacao_e_nenhuma_permissao_existe_para_isso()
    {
        var s = await MontarAsync("apipolitica", limite: 5);
        await using var _ = s.Api;

        // Nenhum endpoint que muda estado menciona limite, política ou habilitação do tenant.
        var fonte = s.Api.Services.GetRequiredService<EndpointDataSource>();
        var mutantes = fonte.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m is "POST" or "PUT" or "PATCH" or "DELETE") == true)
            .Select(e => e.RoutePattern.RawText ?? string.Empty);
        Assert.DoesNotContain(mutantes, r => Regex.IsMatch(r, "limit|registration|user-?polic|tenant-?polic|max-?users|habilita", RegexOptions.IgnoreCase));

        // Tentativas diretas pelo administrador do tenant: sem rota (404/405), e o limite continua o contratado.
        foreach (var caminho in new[] { "/users/usage", "/users/policy", "/tenant/user-policy", "/tenants/limit", "/tenant/settings/user-limit" })
        {
            foreach (var metodo in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Patch })
            {
                var status = await s.Api.StatusAsync(s.Admin, metodo, caminho, new { maxUsers = 999, registrationEnabled = true, limit = 999 });
                Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{metodo} {caminho} respondeu {(int)status}");
            }
        }

        var uso = await s.Api.LerAsync<JsonElement>(s.Admin, "/users/usage");
        Assert.Equal(5, uso.GetProperty("limit").GetInt32());

        // Nenhuma permissão do catálogo (nem o enum) trata de política de usuários da plataforma.
        Assert.DoesNotContain(Enum.GetNames<Permission>(), n => Regex.IsMatch(n, "Limit|Registration|UserPolicy", RegexOptions.IgnoreCase));
    }

    // Elevação de privilégio pela API real

    [Fact]
    public async Task Supervisor_com_cadastro_delegado_cadastra_atendente_e_nao_compromete_a_conta_do_administrador()
    {
        var s = await MontarAsync("apisup");
        await using var _ = s.Api;
        var supervisor = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.Manager);
        foreach (var permissao in new[] { "UserRead", "UserManage" })
        {
            Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/users/{supervisor.Id}/permissions", new { organizationId = s.C.OrganizacaoId, permission = permissao, effect = "Grant" }));
        }

        var token = await s.Api.LoginAsync(supervisor);
        var atendente = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.Attendant);
        var admissao = $"/organizations/{s.C.OrganizacaoId}/users/admissions";

        // Pode o que a delegação cobre...
        Assert.Equal(HttpStatusCode.Created, await s.Api.StatusAsync(token, HttpMethod.Post, admissao, new { email = Email("at"), displayName = "Novo Atendente", role = "Attendant" }));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(token, HttpMethod.Put, $"/users/{atendente.Id}", new { email = Email("troca"), displayName = "Atendente Editado" }));
        // ...e nada além: perfis superiores, personalização, status e a conta do administrador.
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Post, admissao, new { email = Email("adm"), displayName = "Adm", role = "OrganizationAdmin" }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Post, admissao, new { email = Email("sup"), displayName = "Sup", role = "Manager" }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Put, $"/organizations/{s.C.OrganizacaoId}/users/{atendente.Id}/role", new { role = "OrganizationAdmin" }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Put, $"/users/{atendente.Id}/permissions", new { organizationId = s.C.OrganizacaoId, permission = "QueueRead", effect = "Grant" }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Post, $"/users/{atendente.Id}/deactivate"));

        var id = s.AdminCriado.Id;
        Assert.Contains(await s.Api.StatusAsync(token, HttpMethod.Put, $"/users/{id}", new { email = Email("tomada"), displayName = "Tomada" }), new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains(await s.Api.StatusAsync(token, HttpMethod.Post, $"/organizations/{s.C.OrganizacaoId}/users/{id}/password-reset"), new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains(await s.Api.StatusAsync(token, HttpMethod.Post, $"/organizations/{s.C.OrganizacaoId}/users/{id}/sessions/revoke"), new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Contains(await s.Api.StatusAsync(token, HttpMethod.Post, $"/users/{id}/first-access"), new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // A conta do administrador continua intacta e entrando com a mesma senha.
        Assert.NotNull(await s.Api.LoginAsync(s.AdminCriado));
        Assert.Equal(s.AdminCriado.Email, (await s.Api.LerAsync<JsonElement>(s.Admin, $"/users/{id}")).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Administrador_de_uma_organizacao_nao_adota_usuario_sem_vinculo_visivel_e_o_do_tenant_pode()
    {
        var s = await MontarAsync("apiadota");
        await using var _ = s.Api;
        var outra = await banco.CriarOrganizacaoAsync(s.C.Tenant, "Segunda organização");
        var adminDaOrg = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.OrganizationAdmin);
        var token = await s.Api.LoginAsync(adminDaOrg);
        var deOutraOrg = await banco.CriarUsuarioAsync(s.C.Tenant, outra.Id, Role.Attendant);

        // Conhecer o identificador não concede nada: o usuário da outra organização responde como inexistente.
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(token, HttpMethod.Get, $"/users/{deOutraOrg.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(token, HttpMethod.Put, $"/organizations/{s.C.OrganizacaoId}/users/{deOutraOrg.Id}/role", new { role = "Attendant" }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(token, HttpMethod.Post, $"/users/{deOutraOrg.Id}/deactivate"));
        Assert.DoesNotContain((await s.Api.LerAsync<JsonElement>(token, "/users?pageSize=100")).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == deOutraOrg.Id);

        // O administrador do tenant faz a operação legítima.
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/organizations/{s.C.OrganizacaoId}/users/{deOutraOrg.Id}/role", new { role = "Attendant" }));

        // Outro tenant nunca enxerga nem alcança usuários deste.
        var outroTenant = await CenarioDeConversas.CriarAsync(banco, "apiadotaoutro");
        var adminOutro = await s.Api.LoginAsync(await banco.CriarAdministradorDoTenantAsync(outroTenant.Tenant));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(adminOutro, HttpMethod.Get, $"/users/{adminDaOrg.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(adminOutro, HttpMethod.Post, $"/users/{adminDaOrg.Id}/deactivate"));
    }

    [Fact]
    public async Task Ninguem_se_desativa_a_si_mesmo_nem_sem_token_e_outro_administrador_pode_ser_desativado_havendo_mais_de_um()
    {
        var s = await MontarAsync("apiultimo");
        await using var _ = s.Api;
        var segundo = await banco.CriarAdministradorDoTenantAsync(s.C.Tenant);

        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/users/{s.AdminCriado.Id}/deactivate")); // autodesativação
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/users/{segundo.Id}/deactivate")); // há outro
        // O segundo, agora inativo, não conta; o último administrador (o próprio) não é desativado por ninguém que possa fazê-lo.
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/users/{s.AdminCriado.Id}/deactivate"));
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(null, HttpMethod.Post, $"/users/{s.AdminCriado.Id}/deactivate"));
    }

    // Atendente desativado com atendimentos em andamento

    [Fact]
    public async Task Atendente_desativado_perde_o_acesso_nao_recebe_novos_atendimentos_e_o_atendimento_em_andamento_continua_acessivel_e_liberavel()
    {
        var s = await MontarAsync("apidesativa");
        await using var _ = s.Api;
        var fila = (await s.Api.CriarAsync<JsonElement>(s.Admin, "/contact-center/queues", new { organizationId = s.C.OrganizacaoId, name = "Atendimento" })).GetProperty("id").GetGuid();
        var ana = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.Attendant);
        var beto = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.Attendant);
        foreach (var atendente in new[] { ana, beto })
        {
            Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/contact-center/queues/{fila}/members/{atendente.Id}", new { role = "Agent" }));
        }

        var tokenAna = await s.Api.LoginAsync(ana);
        var tokenBeto = await s.Api.LoginAsync(beto);
        var sessao = (await s.C.EntrarAsync("5511999990042", texto: "Preciso ficar em jejum?")).SessionId;
        using var entrou = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = sessao, queueId = fila, requestId = Guid.NewGuid(), reason = "customer-asked" });
        var item = (await CorpoAsync(entrou)).GetProperty("item").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(tokenAna, HttpMethod.Post, $"/contact-center/items/{item}/claim", new { requestId = Guid.NewGuid() }));

        // A Ana é desativada com o atendimento em andamento.
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/users/{ana.Id}/deactivate"));

        // Sem acesso: a sessão cai e o login é recusado.
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(tokenAna, HttpMethod.Get, "/auth/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(tokenAna, HttpMethod.Get, "/contact-center/items"));
        using var login = await s.Api.Client.PostAsJsonAsync("/auth/login", new { tenant = ana.TenantSlug, email = ana.Email, password = ana.Senha });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // Sem novos atendimentos: ninguém consegue atribuir um item a ela.
        var outraSessao = (await s.C.EntrarAsync("5511999990043", texto: "Outra dúvida")).SessionId;
        using var outro = await s.Api.EnviarAsync(s.Admin, HttpMethod.Post, "/contact-center/handoffs", new { sessionId = outraSessao, queueId = fila, requestId = Guid.NewGuid() });
        var outroItem = (await CorpoAsync(outro)).GetProperty("item").GetProperty("id").GetGuid();
        var atribuicao = await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/contact-center/items/{outroItem}/assign", new { userId = ana.Id, requestId = Guid.NewGuid() });
        Assert.NotEqual(HttpStatusCode.OK, atribuicao);
        Assert.True((int)atribuicao is >= 400 and < 500, $"atribuir a usuário desativado respondeu {(int)atribuicao}");

        // O atendimento dela continua acessível aos responsáveis autorizados (o item e a conversa) e nenhum histórico some.
        var visto = await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/items/{item}");
        Assert.Equal(ana.Id, visto.GetProperty("assignedUserId").GetGuid());
        Assert.Equal(item, Assert.Single((await s.Api.LerAsync<JsonElement>(s.Admin, "/contact-center/items?status=Assigned")).EnumerateArray().ToList(), i => i.GetProperty("id").GetGuid() == item).GetProperty("id").GetGuid());
        var mensagens = await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/items/{item}/messages?take=10&order=OldestFirst");
        Assert.Equal("Preciso ficar em jejum?", Assert.Single(mensagens.GetProperty("items").EnumerateArray().ToList()).GetProperty("text").GetString());

        // Os mecanismos existentes liberam e transferem o atendimento; o Beto o assume e o histórico preserva cada passo.
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/contact-center/items/{item}/release", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(tokenBeto, HttpMethod.Post, $"/contact-center/items/{item}/claim", new { requestId = Guid.NewGuid() }));
        var tipos = (await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/items/{item}/history")).EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "Entered", "Assigned", "Released", "Assigned" }, tipos);
        Assert.Equal(beto.Id, (await s.Api.LerAsync<JsonElement>(s.Admin, $"/contact-center/items/{item}")).GetProperty("assignedUserId").GetGuid());
    }


    [Fact]
    public async Task Detalhe_lista_a_permissao_fora_do_perfil_e_a_concessao_inedita_a_torna_efetiva_e_a_restauracao_a_retira()
    {
        var s = await MontarAsync("apiinedita");
        await using var servidor = s.Api;
        var supervisor = await banco.CriarUsuarioAsync(s.C.Tenant, s.C.OrganizacaoId, Role.Manager);

        JsonElement Entrada(JsonElement detalhe, string permissao) =>
            detalhe.GetProperty("permissions").EnumerateArray().Single(p => p.GetProperty("permission").GetString() == permissao && p.GetProperty("organizationId").GetGuid() == s.C.OrganizacaoId);

        // Sem personalização: UserManage aparece, NÃO pertence ao perfil e não é efetiva; nenhuma permissão proibida entra.
        var antes = await s.Api.LerAsync<JsonElement>(s.Admin, $"/users/{supervisor.Id}");
        var antesEntrada = Entrada(antes, "UserManage");
        Assert.False(antesEntrada.GetProperty("inProfile").GetBoolean());
        Assert.False(antesEntrada.GetProperty("effective").GetBoolean());
        Assert.Equal(JsonValueKind.Null, antesEntrada.GetProperty("override").ValueKind);
        Assert.DoesNotContain(antes.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("permission").GetString() is "SystemOperation" or "AuditRead" or "TenantSettingsManage");
        Assert.True(Entrada(antes, "QueueRead").GetProperty("inProfile").GetBoolean());

        // Concessão inédita (fora do perfil): vira Grant efetiva.
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/users/{supervisor.Id}/permissions", new { organizationId = s.C.OrganizacaoId, permission = "UserManage", effect = "Grant" }));
        var depois = Entrada(await s.Api.LerAsync<JsonElement>(s.Admin, $"/users/{supervisor.Id}"), "UserManage");
        Assert.Equal(("Grant", false, true), (depois.GetProperty("override").GetString(), depois.GetProperty("inProfile").GetBoolean(), depois.GetProperty("effective").GetBoolean()));

        // Proibida ou incompatível com o escopo: recusada pelo backend (permissão só de tenant em organização; permissão de sistema).
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/users/{supervisor.Id}/permissions", new { organizationId = s.C.OrganizacaoId, permission = "AuditRead", effect = "Grant" }));
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Admin, HttpMethod.Put, $"/users/{supervisor.Id}/permissions", new { organizationId = s.C.OrganizacaoId, permission = "SystemOperation", effect = "Grant" }));

        // Restaurar o padrão retira a permissão adicional.
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Admin, HttpMethod.Post, $"/users/{supervisor.Id}/permissions/reset", new { allScopes = true }));
        var restaurada = Entrada(await s.Api.LerAsync<JsonElement>(s.Admin, $"/users/{supervisor.Id}"), "UserManage");
        Assert.False(restaurada.GetProperty("effective").GetBoolean());
        Assert.Equal(JsonValueKind.Null, restaurada.GetProperty("override").ValueKind);
    }
    // Pool de UMA conexão: a admissão não precisa de segunda conexão durante a transação, e a saturação real é controlada (503).

    [Fact]
    public async Task Admissao_funciona_com_pool_de_uma_conexao_e_a_saturacao_real_responde_503_controlado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "apipool");
        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().SetAsync(c.Tenant.Slug, 3, true);
        }

        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        var pool = new Npgsql.NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString) { MaxPoolSize = 1, Timeout = 2 }.ConnectionString;
        var configuracao = ApiServer.ConfiguracaoDeDesenvolvimento(cabecalhoHabilitado: false);
        configuracao["ConnectionStrings:Tenancy"] = pool;
        await using var api = await ApiServer.StartAsync("Development", configuracao);
        var token = await api.LoginAsync(admin);
        var rota = $"/organizations/{c.OrganizacaoId}/users/admissions";

        // Toda a admissão (trava, política, contagem e gravação) cabe numa única conexão: com o pool de UMA conexão ela funciona; e o limite continua valendo (409, nunca 503).
        Assert.Equal(HttpStatusCode.Created, await api.StatusAsync(token, HttpMethod.Post, rota, new { email = Email("p1"), displayName = "P1", role = "User" }));
        Assert.Equal(HttpStatusCode.Created, await api.StatusAsync(token, HttpMethod.Post, rota, new { email = Email("p2"), displayName = "P2", role = "User" }));
        using var cheio = await api.EnviarAsync(token, HttpMethod.Post, rota, new { email = Email("p3"), displayName = "P3", role = "User" });
        Assert.Equal(HttpStatusCode.Conflict, cheio.StatusCode);
        Assert.Equal("user-limit-reached", (await CorpoAsync(cheio)).GetProperty("code").GetString());

        // Saturação REAL: alguém segura a trava de admissão do tenant; a admissão espera segurando a ÚNICA conexão; outra requisição não consegue conexão e recebe 503 controlado, com Retry-After.
        await using var segurando = new Npgsql.NpgsqlConnection(TestDatabase.ConnectionString);
        await segurando.OpenAsync();
        await using (var trava = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@k, 0))", segurando))
        {
            trava.Parameters.AddWithValue("k", UserAdmissionLock.KeyFor(c.Tenant.TenantId));
            await trava.ExecuteNonQueryAsync();
        }

        await DefinirLimiteSemTravarAsync(c.Tenant.Slug);
        var esperando = Task.Run(() => api.StatusAsync(token, HttpMethod.Post, rota, new { email = Email("p4"), displayName = "P4", role = "User" }));
        await Task.Delay(700);
        using var ocupado = await api.EnviarAsync(token, HttpMethod.Get, "/users/usage");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ocupado.StatusCode);
        Assert.NotNull(ocupado.Headers.RetryAfter);
        Assert.Equal("service-busy", (await CorpoAsync(ocupado)).GetProperty("code").GetString());

        await using (var solta = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@k, 0))", segurando))
        {
            solta.Parameters.AddWithValue("k", UserAdmissionLock.KeyFor(c.Tenant.TenantId));
            await solta.ExecuteNonQueryAsync();
        }

        // Liberada a trava, a admissão que esperava termina de forma definida (aceita ou limite), nunca 500.
        var final = await esperando.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(final is HttpStatusCode.Created or HttpStatusCode.Conflict, $"admissão pós-saturação respondeu {(int)final}");
    }

    private async Task DefinirLimiteSemTravarAsync(string slug)
    {
        // O limite sobe ANTES de a trava ser tomada pelo teste (sem esperar por ela): só garante vaga para a admissão que vai esperar.
        await using var conexao = new Npgsql.NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new Npgsql.NpgsqlCommand("UPDATE identity.tenant_user_policies SET max_users = 10 WHERE tenant_id = (SELECT id FROM tenancy.tenants WHERE slug = @s)", conexao);
        comando.Parameters.AddWithValue("s", slug);
        await comando.ExecuteNonQueryAsync();
    }
}
