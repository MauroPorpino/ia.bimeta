using System.Net;
using System.Net.Http.Json;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Identity.Domain;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// Autorização pela API real (HTTP) com PostgreSQL real (TEN-2): os três papéis, concessão por organização, nega por
/// padrão, recurso sem leitura responde 404 e com leitura mas sem a ação responde 403, isolamento entre tenants e
/// trilha de auditoria sem segredos.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthorizationApiTests(PostgresFixture banco)
{
    private sealed record Ambiente(
        TenantInfo Tenant,
        OrganizationView Org1,
        OrganizationView Org2,
        UsuarioCriado AdminDoTenant,
        UsuarioCriado AdminDaOrg1,
        UsuarioCriado GestorDaOrg1,
        UsuarioCriado UsuarioDaOrg1,
        UsuarioCriado UsuarioDaOrg2);

    private async Task<Ambiente> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var org1 = await banco.CriarOrganizacaoAsync(tenant, $"Org 1 {prefixo}");
        var org2 = await banco.CriarOrganizacaoAsync(tenant, $"Org 2 {prefixo}");
        return new Ambiente(
            tenant,
            org1,
            org2,
            await banco.CriarAdministradorDoTenantAsync(tenant),
            await banco.CriarUsuarioAsync(tenant, org1.Id, Role.OrganizationAdmin),
            await banco.CriarUsuarioAsync(tenant, org1.Id, Role.Manager),
            await banco.CriarUsuarioAsync(tenant, org1.Id, Role.User),
            await banco.CriarUsuarioAsync(tenant, org2.Id, Role.User));
    }

    private static object NovaUnidade(string nome, string tipo = "Branch") => new { name = nome, kind = tipo };

    private static object NovoUsuario(string email, string papel, string senha = "Senha-De-Teste-2026!", string nome = "Fulana") =>
        new { email, displayName = nome, password = senha, role = papel };

    private static string EmailNovo() => $"n{Guid.NewGuid().ToString("N")[..10]}@teste.local";

    // Usuário comum

    [Fact]
    public async Task Usuario_comum_le_so_o_que_tem_concessao_e_nao_gere_nada()
    {
        var a = await MontarAsync("comum");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.UsuarioDaOrg1);
        var qualquer = Guid.NewGuid();

        Assert.Equal(new[] { a.Org1.Id }, (await api.LerAsync<List<OrganizacaoDto>>(token, "/organizations")).Select(o => o.Id));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org1.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org2.Id}"));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org1.Id}/business-units"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org2.Id}/business-units"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{a.Org2.Id}/business-units", NovaUnidade("X")));

        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Post, "/organizations", new { name = "Nova" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("X")));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org1.Id}/users"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{a.Org1.Id}/users", NovoUsuario(EmailNovo(), "User")));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Put, $"/organizations/{a.Org1.Id}/users/{a.UsuarioDaOrg1.Id}/role", new { role = "OrganizationAdmin" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Delete, $"/organizations/{a.Org1.Id}/users/{a.GestorDaOrg1.Id}/role"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Get, "/audit-events"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"/business-units/{qualquer}"));
    }

    [Fact]
    public async Task Usuario_nao_se_promove_nem_com_o_id_da_propria_concessao()
    {
        var a = await MontarAsync("promove");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.UsuarioDaOrg1);

        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Put, $"/organizations/{a.Org1.Id}/users/{a.UsuarioDaOrg1.Id}/role", new { role = "OrganizationAdmin" }));

        var eu = await api.LerAsync<EuDto>(token, "/auth/me");
        Assert.Equal("User", Assert.Single(eu.Grants).Role);
    }

    // Gestor

    [Fact]
    public async Task Gestor_cria_unidades_e_setores_na_sua_organizacao_mas_nao_gere_usuarios()
    {
        var a = await MontarAsync("gestor");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.GestorDaOrg1);

        var matriz = await api.CriarAsync<UnidadeDto>(token, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Matriz", "Headquarters"));
        var setor = await api.CriarAsync<SetorDto>(token, $"/business-units/{matriz.Id}/departments", new { name = "Coleta" });

        Assert.Equal(a.Org1.Id, matriz.OrganizationId);
        Assert.Equal("Headquarters", matriz.Kind);
        Assert.Equal(matriz.Id, setor.BusinessUnitId);
        Assert.Equal(setor.Id, (await api.LerAsync<SetorDto>(token, $"/departments/{setor.Id}")).Id);
        Assert.Equal(new[] { setor.Id }, (await api.LerAsync<List<SetorDto>>(token, $"/business-units/{matriz.Id}/departments")).Select(s => s.Id));
        Assert.Equal(matriz.Id, (await api.LerAsync<UnidadeDto>(token, $"/business-units/{matriz.Id}")).Id);

        // Fora da organização dele não existe; dentro dela, gerir usuários é do administrador.
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{a.Org2.Id}/business-units", NovaUnidade("Invasora")));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{a.Org1.Id}/users", NovoUsuario(EmailNovo(), "User")));
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org1.Id}/users"));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Post, "/organizations", new { name = "Nova" }));
    }

    [Fact]
    public async Task Setor_e_unidade_criados_na_organizacao_1_nao_aparecem_para_quem_so_tem_a_organizacao_2()
    {
        var a = await MontarAsync("estrutura");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var gestor = await api.LoginAsync(a.GestorDaOrg1);
        var deOutraOrg = await api.LoginAsync(a.UsuarioDaOrg2);
        var unidade = await api.CriarAsync<UnidadeDto>(gestor, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Centro"));
        var setor = await api.CriarAsync<SetorDto>(gestor, $"/business-units/{unidade.Id}/departments", new { name = "Triagem" });

        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(deOutraOrg, HttpMethod.Get, $"/business-units/{unidade.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(deOutraOrg, HttpMethod.Get, $"/business-units/{unidade.Id}/departments"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(deOutraOrg, HttpMethod.Get, $"/departments/{setor.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(deOutraOrg, HttpMethod.Post, $"/business-units/{unidade.Id}/departments", new { name = "Intruso" }));
    }

    // Administrador da organização

    [Fact]
    public async Task Administrador_da_organizacao_gere_usuarios_so_da_sua_organizacao()
    {
        var a = await MontarAsync("admorg");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.AdminDaOrg1);
        var email = EmailNovo();

        var criado = await api.CriarAsync<UsuarioDto>(token, $"/organizations/{a.Org1.Id}/users", NovoUsuario(email, "Manager"));

        Assert.Equal(email, criado.Email);
        var acessos = await api.LerAsync<List<AcessoDto>>(token, $"/organizations/{a.Org1.Id}/users");
        Assert.Equal("Manager", acessos.Single(u => u.UserId == criado.Id).Role);
        Assert.Contains(acessos, u => u.UserId == a.AdminDoTenant.Id && u.TenantWide);

        // O novo usuário já entra e enxerga só a organização 1.
        var tokenDoNovo = await api.LoginAsync(new UsuarioCriado(criado.Id, email, "Senha-De-Teste-2026!", a.Tenant.Slug));
        Assert.Equal(new[] { a.Org1.Id }, (await api.LerAsync<List<OrganizacaoDto>>(tokenDoNovo, "/organizations")).Select(o => o.Id));

        // Na organização 2 o administrador da 1 não enxerga nada: 404, nunca 403.
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Post, $"/organizations/{a.Org2.Id}/users", NovoUsuario(EmailNovo(), "User")));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Get, $"/organizations/{a.Org2.Id}/users"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(token, HttpMethod.Put, $"/organizations/{a.Org2.Id}/users/{a.UsuarioDaOrg2.Id}/role", new { role = "OrganizationAdmin" }));
    }

    [Fact]
    public async Task Administrador_de_uma_organizacao_nao_cria_organizacao_nem_le_a_auditoria_do_tenant()
    {
        var a = await MontarAsync("semtenant");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.AdminDaOrg1);

        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Post, "/organizations", new { name = "Outra empresa" }));
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(token, HttpMethod.Get, "/audit-events"));
        Assert.Equal(new[] { a.Org1.Id }, (await api.LerAsync<List<OrganizacaoDto>>(token, "/organizations")).Select(o => o.Id));
    }

    [Fact]
    public async Task Trocar_e_revogar_o_papel_muda_o_acesso_na_proxima_requisicao_do_mesmo_token()
    {
        var a = await MontarAsync("papel");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var admin = await api.LoginAsync(a.AdminDaOrg1);
        var tokenDoUsuario = await api.LoginAsync(a.UsuarioDaOrg1);
        var rota = $"/organizations/{a.Org1.Id}/users/{a.UsuarioDaOrg1.Id}/role";

        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(tokenDoUsuario, HttpMethod.Post, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Centro")));

        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(admin, HttpMethod.Put, rota, new { role = "Manager" }));
        Assert.Equal(HttpStatusCode.Created, await api.StatusAsync(tokenDoUsuario, HttpMethod.Post, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Centro")));

        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(admin, HttpMethod.Delete, rota));

        // Sem concessão alguma o usuário continua autenticado, mas não alcança organização nem recurso.
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(tokenDoUsuario, HttpMethod.Get, "/auth/me"));
        Assert.Empty(await api.LerAsync<List<OrganizacaoDto>>(tokenDoUsuario, "/organizations"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDoUsuario, HttpMethod.Get, $"/organizations/{a.Org1.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDoUsuario, HttpMethod.Get, $"/organizations/{a.Org1.Id}/business-units"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDoUsuario, HttpMethod.Post, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Outra")));
    }

    [Fact]
    public async Task Revogar_concessao_que_nao_existe_e_conceder_a_usuario_inexistente_respondem_404()
    {
        var a = await MontarAsync("inexistente");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var admin = await api.LoginAsync(a.AdminDaOrg1);

        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(admin, HttpMethod.Delete, $"/organizations/{a.Org1.Id}/users/{Guid.NewGuid()}/role"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(admin, HttpMethod.Put, $"/organizations/{a.Org1.Id}/users/{Guid.NewGuid()}/role", new { role = "User" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(admin, HttpMethod.Get, $"/organizations/{Guid.NewGuid()}/users"));
    }

    // Administrador do tenant

    [Fact]
    public async Task Administrador_do_tenant_cria_organizacoes_enxerga_todas_e_le_a_auditoria()
    {
        var a = await MontarAsync("admtenant");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.AdminDoTenant);

        var nova = await api.CriarAsync<OrganizacaoDto>(token, "/organizations", new { name = "Terceira" });
        var criado = await api.CriarAsync<UsuarioDto>(token, $"/organizations/{nova.Id}/users", NovoUsuario(EmailNovo(), "OrganizationAdmin"));

        var todas = await api.LerAsync<List<OrganizacaoDto>>(token, "/organizations");
        Assert.Equal(3, todas.Count);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(token, HttpMethod.Get, "/audit-events"));
        Assert.Contains(await api.LerAsync<List<AcessoDto>>(token, $"/organizations/{nova.Id}/users"), u => u.UserId == criado.Id);
    }

    // Isolamento entre tenants pela API

    [Fact]
    public async Task Tenant_nao_alcanca_nenhum_recurso_de_outro_tenant_pela_API()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenDeB = await api.LoginAsync(b.AdminDoTenant);
        var tokenDeA = await api.LoginAsync(a.AdminDoTenant);
        var unidadeDeA = await api.CriarAsync<UnidadeDto>(tokenDeA, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Centro de A"));
        var setorDeA = await api.CriarAsync<SetorDto>(tokenDeA, $"/business-units/{unidadeDeA.Id}/departments", new { name = "Setor de A" });

        // O administrador do tenant B, mesmo com concessão de tenant, responde 404 para tudo que é do tenant A.
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Get, $"/organizations/{a.Org1.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Get, $"/organizations/{a.Org1.Id}/business-units"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Post, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Intrusa")));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Get, $"/organizations/{a.Org1.Id}/users"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Post, $"/organizations/{a.Org1.Id}/users", NovoUsuario(EmailNovo(), "User")));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Put, $"/organizations/{b.Org1.Id}/users/{a.AdminDoTenant.Id}/role", new { role = "User" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Get, $"/business-units/{unidadeDeA.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Get, $"/business-units/{unidadeDeA.Id}/departments"));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Post, $"/business-units/{unidadeDeA.Id}/departments", new { name = "Intruso" }));
        Assert.Equal(HttpStatusCode.NotFound, await api.StatusAsync(tokenDeB, HttpMethod.Get, $"/departments/{setorDeA.Id}"));

        Assert.DoesNotContain(await api.LerAsync<List<OrganizacaoDto>>(tokenDeB, "/organizations"), o => o.Id == a.Org1.Id);
        Assert.DoesNotContain(await api.LerAsync<List<AcessoDto>>(tokenDeB, $"/organizations/{b.Org1.Id}/users"), u => u.UserId == a.AdminDoTenant.Id);
    }

    // Validação e conflitos

    [Fact]
    public async Task Dados_invalidos_respondem_400_e_conflitos_respondem_409()
    {
        var a = await MontarAsync("validacao");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.AdminDoTenant);
        var email = EmailNovo();
        await api.CriarAsync<UsuarioDto>(token, $"/organizations/{a.Org1.Id}/users", NovoUsuario(email, "User"));
        var rota = $"/organizations/{a.Org1.Id}/users";

        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, rota, NovoUsuario(EmailNovo(), "User", senha: "curta")));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, rota, NovoUsuario("nao-e-email", "User")));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, rota, NovoUsuario(EmailNovo(), "User", nome: " ")));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, rota, NovoUsuario(EmailNovo(), "SuperAdmin")));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, rota, NovoUsuario(EmailNovo(), "99")));
        Assert.Equal(HttpStatusCode.Conflict, await api.StatusAsync(token, HttpMethod.Post, rota, NovoUsuario(email.ToUpperInvariant(), "User")));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Put, $"{rota}/{a.UsuarioDaOrg1.Id}/role", new { role = "Chefe" }));

        var baseUnidades = $"/organizations/{a.Org1.Id}/business-units";
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, baseUnidades, NovaUnidade("X", "Matrizinha")));
        Assert.Equal(HttpStatusCode.BadRequest, await api.StatusAsync(token, HttpMethod.Post, baseUnidades, NovaUnidade("  ")));
        Assert.Equal(HttpStatusCode.Created, await api.StatusAsync(token, HttpMethod.Post, baseUnidades, NovaUnidade("Matriz", "Headquarters")));
        Assert.Equal(HttpStatusCode.Conflict, await api.StatusAsync(token, HttpMethod.Post, baseUnidades, NovaUnidade("Outra matriz", "Headquarters")));
        Assert.Equal(HttpStatusCode.Conflict, await api.StatusAsync(token, HttpMethod.Post, baseUnidades, NovaUnidade("Matriz", "Branch")));
    }

    [Fact]
    public async Task Respostas_de_usuario_nunca_expoem_hash_de_senha_nem_senha()
    {
        var a = await MontarAsync("segredos");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.AdminDoTenant);
        const string senha = "Uma-Senha-Muito-Secreta-99!";

        using var criacao = await api.EnviarAsync(token, HttpMethod.Post, $"/organizations/{a.Org1.Id}/users", NovoUsuario(EmailNovo(), "User", senha));
        using var lista = await api.EnviarAsync(token, HttpMethod.Get, $"/organizations/{a.Org1.Id}/users");

        foreach (var corpo in new[] { await criacao.Content.ReadAsStringAsync(), await lista.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain(senha, corpo);
            Assert.DoesNotContain("pbkdf2", corpo, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passwordHash", corpo, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Auditoria

    [Fact]
    public async Task Auditoria_registra_as_operacoes_relevantes_inclusive_negadas_sem_expor_segredos()
    {
        var a = await MontarAsync("auditoria");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        const string senhaDoNovo = "Senha-Secreta-Do-Novo-77!";
        const string senhaErrada = "Senha-Errada-Que-Nao-Pode-Vazar-1!";

        // Uma tentativa de login com senha errada, depois os logins e operações de verdade.
        using (var negado = await api.Client.PostAsJsonAsync("/auth/login", new { tenant = a.AdminDoTenant.TenantSlug, email = a.AdminDoTenant.Email, password = senhaErrada }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, negado.StatusCode);
        }

        var admin = await api.LoginAsync(a.AdminDoTenant);
        var gestor = await api.LoginAsync(a.GestorDaOrg1);
        var organizacao = await api.CriarAsync<OrganizacaoDto>(admin, "/organizations", new { name = "Auditada" });
        var email = EmailNovo();
        var novo = await api.CriarAsync<UsuarioDto>(admin, $"/organizations/{organizacao.Id}/users", NovoUsuario(email, "User", senhaDoNovo));
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(admin, HttpMethod.Put, $"/organizations/{organizacao.Id}/users/{novo.Id}/role", new { role = "Manager" }));
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(admin, HttpMethod.Delete, $"/organizations/{organizacao.Id}/users/{novo.Id}/role"));
        await api.CriarAsync<UnidadeDto>(admin, $"/organizations/{a.Org1.Id}/business-units", NovaUnidade("Centro"));
        // O gestor não gere usuários: a negação também entra na trilha.
        Assert.Equal(HttpStatusCode.Forbidden, await api.StatusAsync(gestor, HttpMethod.Post, $"/organizations/{a.Org1.Id}/users", NovoUsuario(EmailNovo(), "User")));
        Assert.Equal(HttpStatusCode.NoContent, await api.StatusAsync(gestor, HttpMethod.Post, "/auth/logout"));

        using var resposta = await api.EnviarAsync(admin, HttpMethod.Get, "/audit-events?take=200");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        var eventos = System.Text.Json.JsonSerializer.Deserialize<List<EventoDto>>(texto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        bool Existe(string operacao, string resultado) => eventos.Any(e => e.Operation == operacao && e.Outcome == resultado);
        Assert.True(Existe("identity.login", "Denied"), "login negado");
        Assert.True(Existe("identity.login", "Success"), "login");
        Assert.True(Existe("tenancy.organization.create", "Success"), "organização criada");
        Assert.True(Existe("identity.user.create", "Success"), "usuário criado");
        Assert.True(Existe("identity.grant.set", "Success"), "papel concedido");
        Assert.True(Existe("identity.grant.revoke", "Success"), "papel revogado");
        Assert.True(Existe("tenancy.business-unit.create", "Success"), "unidade criada");
        Assert.True(Existe("identity.user.create", "Denied"), "criação de usuário negada");
        Assert.True(Existe("identity.logout", "Success"), "logout");

        // O evento da organização criada pela API leva o usuário e a origem da sessão (as organizações do preparo do
        // teste foram criadas por contexto de sistema e, por isso, não têm ator de usuário).
        var daApi = Assert.Single(eventos, e => e.Operation == "tenancy.organization.create" && e.ResourceId == organizacao.Id.ToString());
        Assert.Equal(a.AdminDoTenant.Id, daApi.ActorUserId);
        Assert.Equal("session", daApi.ActorOrigin);
        Assert.Equal("Success", daApi.Outcome);
        Assert.Contains(eventos, e => e.Operation == "identity.user.create" && e.Outcome == "Denied" && e.ActorUserId == a.GestorDaOrg1.Id);

        // Nenhum segredo na trilha: nem na resposta da API, nem em nenhuma coluna de texto do banco.
        var segredos = new[] { senhaDoNovo, senhaErrada, a.AdminDoTenant.Senha, admin, gestor, email, "pbkdf2", "Bearer" };
        foreach (var segredo in segredos)
        {
            Assert.DoesNotContain(segredo, texto);
        }

        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();
        foreach (var segredo in segredos)
        {
            var ocorrencias = await db.Database
                .SqlQueryRaw<int>(
                    "select count(*)::int as \"Value\" from audit.audit_events where tenant_id = {0} " +
                    "and (operation || '|' || resource_type || '|' || coalesce(resource_id, '') || '|' || actor_origin) like '%' || {1} || '%'",
                    a.Tenant.TenantId.Value, segredo)
                .SingleAsync();
            Assert.Equal(0, ocorrencias);
        }
    }

    [Fact]
    public async Task Auditoria_de_um_tenant_nunca_mostra_eventos_de_outro()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var tokenDeA = await api.LoginAsync(a.AdminDoTenant);
        var tokenDeB = await api.LoginAsync(b.AdminDoTenant);
        var soDeA = await api.CriarAsync<OrganizacaoDto>(tokenDeA, "/organizations", new { name = "So de A" });

        var eventosDeB = await api.LerAsync<List<EventoDto>>(tokenDeB, "/audit-events?take=200");
        var eventosDeA = await api.LerAsync<List<EventoDto>>(tokenDeA, "/audit-events?take=200");

        Assert.DoesNotContain(eventosDeB, e => e.ActorUserId == a.AdminDoTenant.Id);
        Assert.DoesNotContain(eventosDeA, e => e.ActorUserId == b.AdminDoTenant.Id);
        Assert.Contains(eventosDeA, e => e.Operation == "tenancy.organization.create" && e.ResourceId == soDeA.Id.ToString());
        Assert.DoesNotContain(eventosDeB, e => e.ResourceId == soDeA.Id.ToString());
        Assert.DoesNotContain(eventosDeB, e => e.ResourceId == a.Org1.Id.ToString());
    }

    [Fact]
    public async Task A_API_nao_tem_rota_para_alterar_ou_excluir_eventos_de_auditoria()
    {
        var a = await MontarAsync("append");
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var token = await api.LoginAsync(a.AdminDoTenant);
        var eventos = await api.LerAsync<List<EventoDto>>(token, "/audit-events");
        var id = eventos.First().Id;

        foreach (var metodo in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Post })
        {
            var status = await api.StatusAsync(token, metodo, $"/audit-events/{id}", new { operation = "x" });
            Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{metodo} respondeu {(int)status}");
        }

        Assert.Contains(await api.LerAsync<List<EventoDto>>(token, "/audit-events"), e => e.Id == id);
    }
}
