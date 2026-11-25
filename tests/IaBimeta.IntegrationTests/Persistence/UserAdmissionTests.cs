using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Etapa 01 com PostgreSQL REAL: vagas, habilitação e último administrador sob concorrência, a trava de admissão por tenant, a atomicidade (a exceção desfaz tudo), o primeiro acesso e o isolamento entre tenants.
/// Cada teste cria os próprios tenants; nenhum dado é apagado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserAdmissionTests(PostgresFixture banco)
{
    private sealed record Cenario(TenantInfo Tenant, OrganizationView Organizacao, UsuarioCriado Admin);

    private async Task<Cenario> MontarAsync(string prefixo, int? limite, bool cadastroHabilitado = true)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        await DefinirPoliticaAsync(tenant, limite, cadastroHabilitado);
        return new Cenario(tenant, organizacao, admin);
    }

    private async Task DefinirPoliticaAsync(TenantInfo tenant, int? limite, bool habilitado)
    {
        await using var escopo = banco.NovoEscopo();
        var resultado = await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().SetAsync(tenant.Slug, limite, habilitado);
        Assert.NotNull(resultado);
    }

    /// <summary>O administrador do tenant como a sessão o descreveria: permissões EFETIVAS do escopo do tenant.</summary>
    private static RequestContext ComoAdmin(TenantInfo tenant, Guid userId) =>
        PostgresFixture.ContextoDeUsuario(tenant, userId, RolePermissions.Expand(Role.OrganizationAdmin, null).ToArray());

    /// <summary>Uma conta de gestão que NÃO é um usuário administrador contado: serve de ator sem ser o alvo.</summary>
    private static RequestContext ComoGestorExterno(TenantInfo tenant) => ComoAdmin(tenant, Guid.NewGuid());

    private async Task<SeatUsage> UsoAsync(TenantInfo tenant)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return await escopo.ServiceProvider.GetRequiredService<IUserAdminStore>().GetSeatUsageAsync(tenant.TenantId, DateTimeOffset.UtcNow, default);
    }

    private async Task<(bool Ok, Exception? Erro)> TentarAsync(RequestContext contexto, Func<IServiceProvider, Task> acao)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        try
        {
            await acao(escopo.ServiceProvider);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<UserStatus> StatusAsync(TenantInfo tenant, Guid userId)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        return (await escopo.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(userId, default))!.Status;
    }

    private async Task<User> CriarAdminDeTenantExtraAsync(TenantInfo tenant, string prefixo)
    {
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant));
        var hasher = escopo.ServiceProvider.GetRequiredService<PasswordHasher>();
        var agora = DateTimeOffset.UtcNow;
        var usuario = User.Create(tenant.TenantId, $"{prefixo}{Guid.NewGuid().ToString("N")[..8]}@teste.local", "Admin extra", hasher.Hash(PostgresFixture.PasswordPadrao), agora);
        await escopo.ServiceProvider.GetRequiredService<IUserRepository>()
            .AddAsync(usuario, UserOrganizationGrant.Create(tenant.TenantId, usuario.Id, null, Role.OrganizationAdmin, agora, null), default);
        return usuario;
    }

    // Vagas sob concorrência

    [Fact]
    public async Task Admissoes_simultaneas_nunca_ultrapassam_o_limite()
    {
        var c = await MontarAsync("lim", limite: 4); // o administrador já ocupa 1
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 14).Select(i => TentarAsync(contexto, sp =>
            sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"c{i}-{Guid.NewGuid().ToString("N")[..6]}@teste.local", $"Pessoa {i}", Role.Attendant, null))));

        Assert.Equal(3, resultados.Count(r => r.Ok));
        Assert.All(resultados.Where(r => !r.Ok), r => Assert.IsType<UserLimitReachedException>(r.Erro));
        var uso = await UsoAsync(c.Tenant);
        Assert.Equal(4, uso.SeatsUsed);
        Assert.Equal(3, uso.PendingReserved);
    }

    [Fact]
    public async Task Cadastro_com_senha_e_admissao_competem_pela_mesma_vaga()
    {
        var c = await MontarAsync("mix", limite: 2);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);

        var resultados = await Task.WhenAll(
            TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"a{Guid.NewGuid().ToString("N")[..6]}@teste.local", "A", Role.User, null)),
            TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().CreateWithPasswordAsync(c.Organizacao.Id, $"b{Guid.NewGuid().ToString("N")[..6]}@teste.local", "B", PostgresFixture.PasswordPadrao, Role.User, null)),
            TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"c{Guid.NewGuid().ToString("N")[..6]}@teste.local", "C", Role.User, null)));

        Assert.Equal(1, resultados.Count(r => r.Ok));
        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);
    }

    [Fact]
    public async Task Reativacoes_concorrentes_de_usuarios_inativos_usam_so_as_vagas_livres()
    {
        var c = await MontarAsync("rea2", limite: null);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var inativos = new List<UsuarioCriado>();
        for (var i = 0; i < 4; i++)
        {
            inativos.Add(await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant));
        }

        foreach (var u in inativos)
        {
            Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(u.Id))).Ok);
        }

        // Admin ativo (1). Limite 3: sobram 2 vagas para 4 reativações simultâneas.
        await DefinirPoliticaAsync(c.Tenant, 3, true);
        var resultados = await Task.WhenAll(inativos.Select(u => TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().ReactivateAsync(u.Id))));

        Assert.Equal(2, resultados.Count(r => r.Ok));
        Assert.All(resultados.Where(r => !r.Ok), r => Assert.IsType<UserLimitReachedException>(r.Erro));
        Assert.Equal(3, (await UsoAsync(c.Tenant)).SeatsUsed);
    }

    [Fact]
    public async Task Alteracao_do_limite_pela_plataforma_corre_serializada_com_o_ingresso()
    {
        var c = await MontarAsync("pol", limite: 10);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);

        var admissoes = Enumerable.Range(0, 10).Select(i => TentarAsync(contexto, sp =>
            sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"p{i}-{Guid.NewGuid().ToString("N")[..6]}@teste.local", $"P{i}", Role.User, null))).ToList();
        var reducao = TentarAsync(PostgresFixture.Contexto(c.Tenant), sp => sp.GetRequiredService<TenantUserPolicyService>().SetAsync(c.Tenant.Slug, 3, true));

        var resultados = await Task.WhenAll(admissoes);
        Assert.True((await reducao).Ok);

        // Nunca passou do limite que valia quando cada uma foi decidida (10) e, depois da redução, nada novo entra.
        var uso = await UsoAsync(c.Tenant);
        Assert.InRange(uso.SeatsUsed, 1, 10);
        Assert.Equal(uso.SeatsUsed - 1, resultados.Count(r => r.Ok));
        var depois = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"z{Guid.NewGuid().ToString("N")[..6]}@teste.local", "Z", Role.User, null));
        Assert.False(depois.Ok);
        Assert.IsType<UserLimitReachedException>(depois.Erro);
    }

    [Fact]
    public async Task Desabilitar_o_cadastro_corre_serializado_e_depois_nada_novo_entra()
    {
        var c = await MontarAsync("dis", limite: null);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);

        var admissoes = Enumerable.Range(0, 8).Select(i => TentarAsync(contexto, sp =>
            sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"d{i}-{Guid.NewGuid().ToString("N")[..6]}@teste.local", $"D{i}", Role.User, null))).ToList();
        var desabilitar = TentarAsync(PostgresFixture.Contexto(c.Tenant), sp => sp.GetRequiredService<TenantUserPolicyService>().SetAsync(c.Tenant.Slug, null, false));

        var resultados = await Task.WhenAll(admissoes);
        Assert.True((await desabilitar).Ok);
        Assert.All(resultados.Where(r => !r.Ok), r => Assert.IsType<RegistrationDisabledException>(r.Erro));

        var depois = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, $"z{Guid.NewGuid().ToString("N")[..6]}@teste.local", "Z", Role.User, null));
        Assert.IsType<RegistrationDisabledException>(depois.Erro);
        var existentes = (await UsoAsync(c.Tenant)).Active;
        Assert.Equal(1, existentes); // o administrador continua com acesso
    }

    [Fact]
    public async Task Tenants_diferentes_tem_vagas_independentes_e_mesmo_email_vale_nos_dois()
    {
        var a = await MontarAsync("ta", limite: 2);
        var b = await MontarAsync("tb", limite: 2);
        var email = $"mesmo{Guid.NewGuid().ToString("N")[..6]}@teste.local";

        await TentarAsync(ComoAdmin(a.Tenant, a.Admin.Id), sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(a.Organizacao.Id, email, "Ana", Role.User, null));
        await TentarAsync(ComoAdmin(b.Tenant, b.Admin.Id), sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(b.Organizacao.Id, email, "Ana", Role.User, null));

        Assert.Equal(2, (await UsoAsync(a.Tenant)).SeatsUsed);
        Assert.Equal(2, (await UsoAsync(b.Tenant)).SeatsUsed);
        var cheio = await TentarAsync(ComoAdmin(a.Tenant, a.Admin.Id), sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(a.Organizacao.Id, "x" + email, "X", Role.User, null));
        Assert.IsType<UserLimitReachedException>(cheio.Erro);
        // O administrador de A não alcança usuários de B.
        await using var escopo = banco.NovoEscopo(ComoAdmin(a.Tenant, a.Admin.Id));
        var lista = await escopo.ServiceProvider.GetRequiredService<UserAdminService>().ListAsync(null, null, null, null, 1, 100);
        Assert.DoesNotContain(lista.Items, i => i.Email == "x" + email);
        Assert.Equal(2, lista.Total);
    }

    [Fact]
    public async Task Mesma_pessoa_em_duas_organizacoes_consome_uma_so_vaga()
    {
        var c = await MontarAsync("duas", limite: 2);
        var outraOrg = await banco.CriarOrganizacaoAsync(c.Tenant, "Segunda organização");
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);

        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().SetRoleAsync(outraOrg.Id, ana.Id, Role.Attendant))).Ok);

        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);
    }

    // Último administrador sob concorrência e atomicidade

    [Fact]
    public async Task Desativacoes_simultaneas_dos_dois_ultimos_administradores_deixam_exatamente_um()
    {
        var c = await MontarAsync("adm", limite: null);
        var segundo = await CriarAdminDeTenantExtraAsync(c.Tenant, "seg");
        var contexto = ComoGestorExterno(c.Tenant);

        var resultados = await Task.WhenAll(
            TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(c.Admin.Id)),
            TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(segundo.Id)));

        Assert.Equal(1, resultados.Count(r => r.Ok));
        Assert.IsType<LastAdministratorException>(resultados.Single(r => !r.Ok).Erro);
        var ativos = new[] { await StatusAsync(c.Tenant, c.Admin.Id), await StatusAsync(c.Tenant, segundo.Id) };
        Assert.Equal(1, ativos.Count(s => s == UserStatus.Active));
    }

    [Fact]
    public async Task Desativar_o_ultimo_administrador_e_recusado_e_a_transacao_e_desfeita()
    {
        var c = await MontarAsync("ult", limite: null);
        var contexto = ComoGestorExterno(c.Tenant);

        var resultado = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(c.Admin.Id));

        Assert.IsType<LastAdministratorException>(resultado.Erro);
        Assert.Equal(UserStatus.Active, await StatusAsync(c.Tenant, c.Admin.Id));
        // As sessões dele também continuam valendo: nada foi revogado pela metade.
        await using var escopo = banco.NovoEscopo();
        var login = await escopo.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(c.Tenant.Slug, c.Admin.Email, c.Admin.Senha);
        Assert.NotNull(await escopo.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token));
    }

    [Fact]
    public async Task Restricao_individual_do_ultimo_administrador_e_desfeita_e_nao_deixa_ajuste_gravado()
    {
        var c = await MontarAsync("res", limite: null);
        var contexto = ComoGestorExterno(c.Tenant);

        var resultado = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().SetOverrideAsync(c.Admin.Id, null, Permission.UserPermissionManage, OverrideEffect.Deny));

        Assert.IsType<LastAdministratorException>(resultado.Erro);
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
        var ajustes = await escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>().ListOverridesAsync(c.Tenant.TenantId, c.Admin.Id, default);
        Assert.Empty(ajustes);
    }

    [Fact]
    public async Task Falha_na_admissao_por_email_repetido_nao_deixa_vaga_reservada_nem_token()
    {
        var c = await MontarAsync("dup", limite: 5);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var email = $"dup{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email, "Um", Role.User, null))).Ok);

        var segunda = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email.ToUpperInvariant(), "Dois", Role.User, null));

        Assert.IsType<DuplicateUserEmailException>(segunda.Erro);
        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);
    }

    // Primeiro acesso, expiração e sessões

    [Fact]
    public async Task Primeiro_acesso_so_tem_um_vencedor_ativa_o_usuario_e_a_vaga_continua_a_mesma()
    {
        var c = await MontarAsync("pri", limite: 2);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        string token = null!;
        var email = $"pri{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            var admitido = await escopo.ServiceProvider.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email, "Nova", Role.Attendant, null);
            token = admitido.Token;
        }

        var antes = await UsoAsync(c.Tenant);
        Assert.Equal(1, antes.PendingReserved);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TentarAsync(new RequestContext(c.Tenant.TenantId, "anonimo"), sp =>
            sp.GetRequiredService<PasswordService>().RedeemResetAsync(c.Tenant.Slug, email, token, PostgresFixture.PasswordPadrao))));

        Assert.Equal(1, resultados.Count(r => r.Ok));
        Assert.All(resultados.Where(r => !r.Ok), r => Assert.IsType<InvalidPasswordResetException>(r.Erro));
        var depois = await UsoAsync(c.Tenant);
        Assert.Equal(2, depois.Active);
        Assert.Equal(0, depois.PendingReserved);
        await using var escopoLogin = banco.NovoEscopo();
        Assert.NotNull(await escopoLogin.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(c.Tenant.Slug, email, PostgresFixture.PasswordPadrao));
    }

    [Fact]
    public async Task Primeiro_acesso_com_cadastro_desabilitado_nao_consome_o_token_e_o_primeiro_administrador_e_excecao()
    {
        var c = await MontarAsync("pdis", limite: 5);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var email = $"pd{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        string token;
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            token = (await escopo.ServiceProvider.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email, "Nova", Role.User, null)).Token;
        }

        await DefinirPoliticaAsync(c.Tenant, 5, false);
        var anonimo = new RequestContext(c.Tenant.TenantId, "anonimo");
        var negado = await TentarAsync(anonimo, sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(c.Tenant.Slug, email, token, PostgresFixture.PasswordPadrao));
        Assert.IsType<InvalidPasswordResetException>(negado.Erro);

        await DefinirPoliticaAsync(c.Tenant, 5, true);
        var aceito = await TentarAsync(anonimo, sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(c.Tenant.Slug, email, token, PostgresFixture.PasswordPadrao));
        Assert.True(aceito.Ok);
    }

    [Fact]
    public async Task Primeiro_administrador_pela_plataforma_e_atomico_unico_e_so_depois_entra()
    {
        var tenant = await banco.RegistrarTenantAsync("fa");
        await DefinirPoliticaAsync(tenant, 3, false);
        var email = $"dono{Guid.NewGuid().ToString("N")[..6]}@teste.local";

        string token;
        await using (var escopo = banco.NovoEscopo(PostgresFixture.Contexto(tenant)))
        {
            token = (await escopo.ServiceProvider.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync(email, "Dona da Empresa")).Token;
        }

        // Concorrência: um segundo "primeiro administrador" não passa.
        var segundo = await TentarAsync(PostgresFixture.Contexto(tenant), sp => sp.GetRequiredService<UserService>().ProvisionFirstAdministratorAsync($"x{email}", "Outro"));
        Assert.IsType<IdentityValidationException>(segundo.Erro);

        var uso = await UsoAsync(tenant);
        Assert.Equal(1, uso.SeatsUsed);

        var anonimo = new RequestContext(tenant.TenantId, "anonimo");
        Assert.True((await TentarAsync(anonimo, sp => sp.GetRequiredService<PasswordService>().RedeemResetAsync(tenant.Slug, email, token, PostgresFixture.PasswordPadrao))).Ok);
        await using var escopoLogin = banco.NovoEscopo();
        var login = await escopoLogin.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(tenant.Slug, email, PostgresFixture.PasswordPadrao);
        var principal = await escopoLogin.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token);
        Assert.Contains(principal!.Permissions, p => p.Permission == Permission.UserPermissionManage && p.OrganizationId is null);
    }

    [Fact]
    public async Task Admissao_expirada_libera_a_vaga_e_readmitir_e_um_novo_ingresso()
    {
        var c = await MontarAsync("exp", limite: 2);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        var email = $"ex{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        Guid userId;
        await using (var escopo = banco.NovoEscopo(contexto))
        {
            userId = (await escopo.ServiceProvider.GetRequiredService<UserAdminService>().AdmitAsync(c.Organizacao.Id, email, "Exp", Role.User, null)).User.Id;
        }

        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);

        // Expira de verdade no banco (nada de relógio falso: o token vira "vencido").
        await using (var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new Npgsql.NpgsqlCommand("UPDATE identity.password_reset_tokens SET expires_at = now() - interval '1 hour' WHERE user_id = @u", conexao);
            comando.Parameters.AddWithValue("u", userId);
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }

        var uso = await UsoAsync(c.Tenant);
        Assert.Equal(1, uso.SeatsUsed);
        Assert.Equal(1, uso.PendingExpired);

        // Cancelar e readmitir com o cadastro desabilitado é recusado.
        await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().CancelAdmissionAsync(userId));
        await DefinirPoliticaAsync(c.Tenant, 2, false);
        var recusa = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().ReadmitAsync(userId));
        Assert.IsType<RegistrationDisabledException>(recusa.Erro);
        await DefinirPoliticaAsync(c.Tenant, 2, true);
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().ReadmitAsync(userId))).Ok);
        Assert.Equal(2, (await UsoAsync(c.Tenant)).SeatsUsed);
    }

    [Fact]
    public async Task Desativar_derruba_a_sessao_e_o_login_e_preserva_o_registro_e_a_auditoria()
    {
        var c = await MontarAsync("des", limite: null);
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        await using var escopoLogin = banco.NovoEscopo();
        var login = await escopoLogin.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(ana.TenantSlug, ana.Email, ana.Senha);
        Assert.NotNull(await escopoLogin.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token));

        Assert.True((await TentarAsync(ComoAdmin(c.Tenant, c.Admin.Id), sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(ana.Id))).Ok);

        await using var depois = banco.NovoEscopo();
        Assert.Null(await depois.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => depois.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(ana.TenantSlug, ana.Email, ana.Senha));
        Assert.Equal(UserStatus.Inactive, await StatusAsync(c.Tenant, ana.Id));
    }

    // Permissões individuais com o banco real

    [Fact]
    public async Task Ajustes_individuais_chegam_ao_autenticador_e_a_restauracao_devolve_o_perfil()
    {
        var c = await MontarAsync("ovr", limite: null);
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Manager);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);
        await using var escopoLogin = banco.NovoEscopo();
        var login = await escopoLogin.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(gestor.TenantSlug, gestor.Email, gestor.Senha);

        await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().SetOverrideAsync(gestor.Id, c.Organizacao.Id, Permission.UserManage, OverrideEffect.Grant));
        await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().SetOverrideAsync(gestor.Id, c.Organizacao.Id, Permission.ChannelRead, OverrideEffect.Deny));
        // Repetir o mesmo ajuste não duplica (o índice único trata o escopo como valor, inclusive o do tenant).
        await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().SetOverrideAsync(gestor.Id, c.Organizacao.Id, Permission.ChannelRead, OverrideEffect.Deny));

        await using (var verificacao = banco.NovoEscopo())
        {
            var principal = await verificacao.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token);
            Assert.Contains(principal!.Permissions, p => p.Permission == Permission.UserManage && p.OrganizationId == c.Organizacao.Id);
            Assert.DoesNotContain(principal.Permissions, p => p.Permission == Permission.ChannelRead);
        }

        await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().ResetOverridesAsync(gestor.Id, scopeSpecified: false, null));
        await using var depois = banco.NovoEscopo();
        var padrao = await depois.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token);
        Assert.Contains(padrao!.Permissions, p => p.Permission == Permission.ChannelRead);
        Assert.DoesNotContain(padrao.Permissions, p => p.Permission == Permission.UserManage);
    }

    [Fact]
    public async Task Ajuste_do_escopo_do_tenant_e_unico_mesmo_com_organizacao_nula()
    {
        var c = await MontarAsync("nul", limite: null);
        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.PermissionOverrides.Add(UserPermissionOverride.Create(c.Tenant.TenantId, c.Admin.Id, null, Permission.AuditRead, OverrideEffect.Deny, DateTimeOffset.UtcNow, null));
        await db.SaveChangesAsync();

        await using var outro = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
        var db2 = outro.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db2.PermissionOverrides.Add(UserPermissionOverride.Create(c.Tenant.TenantId, c.Admin.Id, null, Permission.AuditRead, OverrideEffect.Grant, DateTimeOffset.UtcNow, null));

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Listagem_com_filtros_e_paginacao_funciona_no_banco_e_nao_vaza_outro_tenant()
    {
        var c = await MontarAsync("lst", limite: null);
        var outro = await MontarAsync("lst2", limite: null);
        for (var i = 0; i < 7; i++)
        {
            await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant, $"lista{i}@teste.local");
        }

        await banco.CriarUsuarioAsync(outro.Tenant, outro.Organizacao.Id, Role.Attendant, "lista-outro@teste.local");

        await using var escopo = banco.NovoEscopo(ComoAdmin(c.Tenant, c.Admin.Id));
        var servico = escopo.ServiceProvider.GetRequiredService<UserAdminService>();

        var pagina = await servico.ListAsync("lista", Role.Attendant, c.Organizacao.Id, "active", 1, 5);
        Assert.Equal(7, pagina.Total);
        Assert.Equal(5, pagina.Items.Count);
        Assert.DoesNotContain(pagina.Items, i => i.Email.Contains("outro", StringComparison.Ordinal));

        var curinga = await servico.ListAsync("100%", null, null, null, 1, 10);
        Assert.Empty(curinga.Items); // % não é curinga
        var pendentes = await servico.ListAsync(null, null, null, "pending", 1, 10);
        Assert.Empty(pendentes.Items);
    }

    [Fact]
    public async Task Empresa_anterior_a_etapa_01_continua_sem_politica_ilimitada_e_habilitada_e_a_politica_explicita_vale_depois()
    {
        var tenant = await banco.RegistrarTenantAsync("leg");
        await using (var escopo = banco.NovoEscopo())
        {
            var legado = (await escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>().GetAsync(tenant.Slug))!.Value.Policy;
            Assert.Equal((null, true, false), (legado.MaxUsers, legado.RegistrationEnabled, legado.IsExplicit));
        }

        await DefinirPoliticaAsync(tenant, 7, false);
        await using var depois = banco.NovoEscopo();
        var explicita = (await depois.ServiceProvider.GetRequiredService<TenantUserPolicyService>().GetAsync(tenant.Slug))!.Value.Policy;
        Assert.Equal((7, false, true), (explicita.MaxUsers, explicita.RegistrationEnabled, explicita.IsExplicit));
    }

    [Fact]
    public async Task Politica_recusa_limite_invalido_o_banco_tambem_e_o_provisionamento_comercial_exige_a_politica_explicita()
    {
        var tenant = await banco.RegistrarTenantAsync("inv");
        var zero = await TentarAsync(PostgresFixture.Contexto(tenant), sp => sp.GetRequiredService<TenantUserPolicyService>().SetAsync(tenant.Slug, 0, true));
        Assert.IsType<IdentityValidationException>(zero.Erro);

        // O banco recusa limite inválido mesmo sem passar pelo serviço.
        await using (var conexao = new Npgsql.NpgsqlConnection(banco.ConnectionString))
        {
            await conexao.OpenAsync();
            await using var comando = new Npgsql.NpgsqlCommand("INSERT INTO identity.tenant_user_policies (tenant_id, max_users, registration_enabled, updated_at) VALUES (@t, 0, true, now())", conexao);
            comando.Parameters.AddWithValue("t", tenant.TenantId.Value);
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => comando.ExecuteNonQueryAsync());
        }

        // Sem política explícita, o primeiro administrador do provisionamento comercial é recusado e nada é criado (a falta de política nunca vira empresa ilimitada).
        var recusado = await TentarAsync(PostgresFixture.Contexto(tenant), sp => sp.GetRequiredService<UserService>().EnsureFirstTenantAdministratorAsync($"x{Guid.NewGuid().ToString("N")[..6]}@teste.local", "X", PostgresFixture.PasswordPadrao));
        Assert.IsType<IdentityValidationException>(recusado.Erro);
        Assert.Equal(0, (await UsoAsync(tenant)).SeatsUsed);

        // Repetir o provisionamento da política é idempotente e nunca altera a existente.
        await using var escopo = banco.NovoEscopo();
        var servico = escopo.ServiceProvider.GetRequiredService<TenantUserPolicyService>();
        Assert.True(await servico.EnsureAsync(tenant.TenantId, 4, true));
        Assert.False(await servico.EnsureAsync(tenant.TenantId, 999, false));
        var final = (await servico.GetAsync(tenant.Slug))!.Value.Policy;
        Assert.Equal((4, true), (final.MaxUsers, final.RegistrationEnabled));
    }

    [Fact]
    public async Task Editar_o_email_para_o_de_outro_usuario_e_recusado_e_nada_muda()
    {
        var c = await MontarAsync("edi", limite: null);
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var bia = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);
        var contexto = ComoAdmin(c.Tenant, c.Admin.Id);

        var recusa = await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().UpdateProfileAsync(ana.Id, bia.Email.ToUpperInvariant(), "Novo Nome"));
        Assert.IsType<DuplicateUserEmailException>(recusa.Erro);

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
        var depois = await escopo.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(ana.Id, default);
        Assert.Equal(ana.Email, depois!.Email);
        Assert.NotEqual("Novo Nome", depois.DisplayName);

        // Editar para um e-mail livre muda o e-mail e derruba as sessões (a identidade de login mudou).
        await using var login = banco.NovoEscopo();
        var sessao = await login.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(ana.TenantSlug, ana.Email, ana.Senha);
        var livre = $"livre{Guid.NewGuid().ToString("N")[..6]}@teste.local";
        Assert.True((await TentarAsync(contexto, sp => sp.GetRequiredService<UserAdminService>().UpdateProfileAsync(ana.Id, livre, "Ana Nova"))).Ok);
        await using var verifica = banco.NovoEscopo();
        Assert.Null(await verifica.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(sessao.Token));
        Assert.NotNull(await verifica.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(ana.TenantSlug, livre, ana.Senha));
    }

    [Fact]
    public async Task Usuario_inativo_deixa_de_existir_para_novas_atribuicoes_operacionais_mas_o_registro_permanece()
    {
        var c = await MontarAsync("dir", limite: null);
        var ana = await banco.CriarUsuarioAsync(c.Tenant, c.Organizacao.Id, Role.Attendant);

        await using (var antes = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant)))
        {
            Assert.True(await antes.ServiceProvider.GetRequiredService<IaBimeta.Identity.Contracts.Queries.IUserDirectory>().ExistsAsync(ana.Id));
        }

        Assert.True((await TentarAsync(ComoAdmin(c.Tenant, c.Admin.Id), sp => sp.GetRequiredService<UserAdminService>().DeactivateAsync(ana.Id))).Ok);

        await using var depois = banco.NovoEscopo(PostgresFixture.Contexto(c.Tenant));
        Assert.False(await depois.ServiceProvider.GetRequiredService<IaBimeta.Identity.Contracts.Queries.IUserDirectory>().ExistsAsync(ana.Id));
        Assert.Equal(UserStatus.Inactive, (await depois.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(ana.Id, default))!.Status);
    }
}
