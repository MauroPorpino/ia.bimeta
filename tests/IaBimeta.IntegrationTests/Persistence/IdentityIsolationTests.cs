using System.Security.Cryptography;
using System.Text;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Identidade com PostgreSQL real: usuários, concessões e sessões isolados por tenant, a autenticação que roda antes
/// de existir contexto sem nunca alcançar outro tenant, token e senha guardados só como hash, e restrições do banco.
/// Cada teste cria os próprios tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IdentityIsolationTests(PostgresFixture banco)
{
    private sealed record Cenario(TenantInfo Tenant, OrganizationView Organizacao, UsuarioCriado Admin);

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        var admin = await banco.CriarUsuarioAsync(tenant, organizacao.Id, Role.OrganizationAdmin);
        return new Cenario(tenant, organizacao, admin);
    }

    private async Task<LoginResult> LoginAsync(UsuarioCriado usuario)
    {
        await using var escopo = banco.NovoEscopo();
        return await escopo.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(usuario.TenantSlug, usuario.Email, usuario.Senha);
    }

    // Isolamento de usuários

    [Fact]
    public async Task Mesmo_e_mail_vale_em_tenants_diferentes_mas_nao_dentro_do_mesmo_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await banco.CriarUsuarioAsync(a.Tenant, a.Organizacao.Id, Role.User, "ana@teste.local");
        await banco.CriarUsuarioAsync(b.Tenant, b.Organizacao.Id, Role.User, "ana@teste.local");

        await Assert.ThrowsAsync<DuplicateUserEmailException>(() => banco.CriarUsuarioAsync(a.Tenant, a.Organizacao.Id, Role.User, "ANA@teste.local"));
    }

    [Fact]
    public async Task Falha_de_e_mail_repetido_nao_deixa_usuario_nem_concessao_pela_metade()
    {
        var a = await MontarAsync("a");
        await banco.CriarUsuarioAsync(a.Tenant, a.Organizacao.Id, Role.User, "ana@teste.local");

        await Assert.ThrowsAsync<DuplicateUserEmailException>(() => banco.CriarUsuarioAsync(a.Tenant, a.Organizacao.Id, Role.Manager, "ana@teste.local"));

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var acessos = await escopo.ServiceProvider.GetRequiredService<UserService>().ListAsync(a.Organizacao.Id);
        Assert.Equal(2, acessos.Count); // o administrador e a Ana; nenhuma sobra da tentativa repetida
        Assert.Single(acessos, u => u.Email == "ana@teste.local");
    }

    [Fact]
    public async Task Cada_tenant_so_ve_os_proprios_usuarios_e_concessoes()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));

        var deA = await escopoA.ServiceProvider.GetRequiredService<UserService>().ListAsync(a.Organizacao.Id);
        Assert.Equal(new[] { a.Admin.Email }, deA.Select(u => u.Email));

        var repositorioB = escopoB.ServiceProvider.GetRequiredService<IUserRepository>();
        Assert.Null(await repositorioB.FindByIdAsync(a.Admin.Id, default));
        Assert.Null(await repositorioB.FindByEmailAsync(a.Admin.Email, default));
        Assert.Empty(await escopoB.ServiceProvider.GetRequiredService<IGrantRepository>().ListByUserAsync(a.Admin.Id, default));

        // A organização de A não é visível para B: o caso de uso responde como inexistente.
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => escopoB.ServiceProvider.GetRequiredService<UserService>().ListAsync(a.Organizacao.Id));
    }

    [Fact]
    public async Task Tenant_nao_concede_papel_a_usuario_de_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() =>
            escopoB.ServiceProvider.GetRequiredService<UserService>().GrantRoleAsync(b.Organizacao.Id, a.Admin.Id, Role.User));
    }

    [Fact]
    public async Task Gravar_usuario_ou_concessao_em_nome_de_outro_tenant_e_bloqueado_antes_do_banco()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var intruso = User.Create(a.Tenant.TenantId, "intruso@teste.local", "Intruso", "hash", DateTimeOffset.UtcNow);
        dbB.Users.Add(intruso);
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();

        dbB.Grants.Add(UserOrganizationGrant.Create(a.Tenant.TenantId, a.Admin.Id, a.Organizacao.Id, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Excluir_ou_alterar_usuario_de_outro_tenant_e_bloqueado()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var alvo = await dbB.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == a.Admin.Id);

        alvo.ReplacePasswordHash("hash-do-invasor");
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        dbB.ChangeTracker.Clear();

        dbB.Users.Remove(await dbB.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == a.Admin.Id));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var intacto = await escopoA.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(a.Admin.Id, default);
        Assert.NotNull(intacto);
        Assert.StartsWith("pbkdf2-sha256$", intacto.PasswordHash);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_as_consultas_e_gravacoes_diretas_falham_fechadas()
    {
        var a = await MontarAsync("a");
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Users.ToListAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Grants.CountAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Sessions.CountAsync());

        db.Users.Add(User.Create(a.Tenant.TenantId, "sem-contexto@teste.local", "X", "hash", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_os_casos_de_uso_de_usuario_falham_fechados()
    {
        await using var escopo = banco.NovoEscopo();
        var servico = escopo.ServiceProvider.GetRequiredService<UserService>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.CreateAsync(Guid.NewGuid(), "a@b.co", "A", PostgresFixture.PasswordPadrao, Role.User));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.ListAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.GetCurrentAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.EnsureTenantAdministratorAsync("a@b.co", "A", PostgresFixture.PasswordPadrao));
    }

    // Autenticação anterior ao contexto

    [Fact]
    public async Task A_autenticacao_so_alcanca_o_tenant_informado()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopo = banco.NovoEscopo();
        var loja = escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>();

        Assert.NotNull(await loja.FindUserByEmailAsync(a.Tenant.TenantId, a.Admin.Email, default));
        Assert.Null(await loja.FindUserByEmailAsync(b.Tenant.TenantId, a.Admin.Email, default));
        Assert.Null(await loja.FindUserByIdAsync(b.Tenant.TenantId, a.Admin.Id, default));
        Assert.Empty(await loja.ListGrantsAsync(b.Tenant.TenantId, a.Admin.Id, default));
        Assert.NotEmpty(await loja.ListGrantsAsync(a.Tenant.TenantId, a.Admin.Id, default));
    }

    [Fact]
    public async Task O_token_leva_ao_tenant_da_propria_sessao_e_um_token_desconhecido_a_nada()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");
        var loginDeA = await LoginAsync(a.Admin);

        await using var escopo = banco.NovoEscopo();
        var autenticador = escopo.ServiceProvider.GetRequiredService<SessionAuthenticator>();

        var principal = await autenticador.AuthenticateAsync(loginDeA.Token);
        Assert.NotNull(principal);
        Assert.Equal(a.Tenant.TenantId, principal.TenantId);
        Assert.Equal(a.Admin.Id, principal.UserId);
        Assert.NotEqual(b.Tenant.TenantId, principal.TenantId);
        Assert.Null(await autenticador.AuthenticateAsync(SessionTokens.Generate().Token));
    }

    [Fact]
    public async Task A_autenticacao_nao_grava_sessao_em_tenant_diferente_do_contexto_estabelecido()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var loja = escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>();
        var (_, hash) = SessionTokens.Generate();
        var sessaoDeA = Session.Create(a.Tenant.TenantId, a.Admin.Id, hash, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => loja.AddSessionAsync(sessaoDeA, default));
    }

    [Fact]
    public async Task Login_de_um_tenant_com_credenciais_de_outro_e_recusado()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopo = banco.NovoEscopo();
        var autenticacao = escopo.ServiceProvider.GetRequiredService<AuthenticationService>();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => autenticacao.LoginAsync(b.Tenant.Slug, a.Admin.Email, a.Admin.Senha));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => autenticacao.LoginAsync(a.Tenant.Slug, a.Admin.Email, "senha-errada-123"));
        Assert.NotEmpty((await autenticacao.LoginAsync(a.Tenant.Slug, a.Admin.Email, a.Admin.Senha)).Token);
    }

    // Segredos no banco

    [Fact]
    public async Task O_banco_guarda_so_o_hash_do_token_e_so_o_hash_da_senha()
    {
        var a = await MontarAsync("a");
        var login = await LoginAsync(a.Admin);

        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hashEsperado = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.Token))).ToLowerInvariant();

        var guardado = await db.Database
            .SqlQueryRaw<string>(
                "select encode(token_hash, 'hex') as \"Value\" from identity.sessions where tenant_id = {0}",
                a.Tenant.TenantId.Value)
            .SingleAsync();
        var hashDaSenha = await db.Database
            .SqlQueryRaw<string>(
                "select password_hash as \"Value\" from identity.users where tenant_id = {0} and id = {1}",
                a.Tenant.TenantId.Value, a.Admin.Id)
            .SingleAsync();

        Assert.Equal(hashEsperado, guardado);
        Assert.NotEqual(Convert.ToHexString(Encoding.UTF8.GetBytes(login.Token)).ToLowerInvariant(), guardado);
        Assert.StartsWith("pbkdf2-sha256$", hashDaSenha);
        Assert.DoesNotContain(a.Admin.Senha, hashDaSenha);

        // Nenhuma coluna de texto do esquema contém o token nem a senha em claro.
        var tokenEmClaro = await db.Database
            .SqlQueryRaw<int>(
                "select count(*)::int as \"Value\" from identity.users u where u.email like '%' || {0} || '%' or u.display_name like '%' || {0} || '%' or u.password_hash like '%' || {1} || '%'",
                login.Token, a.Admin.Senha)
            .SingleAsync();
        Assert.Equal(0, tokenEmClaro);
    }

    [Fact]
    public async Task Login_regrava_no_banco_o_hash_de_senha_com_os_parametros_atuais()
    {
        var a = await MontarAsync("a");
        const int maisForte = PostgresFixture.PasswordIterations * 2;

        await using (var escopo = banco.NovoEscopo())
        {
            var opcoes = new IdentityOptions { PasswordHashIterations = maisForte };
            var autenticacao = new AuthenticationService(
                escopo.ServiceProvider.GetRequiredService<ITenantDirectory>(),
                escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>(),
                escopo.ServiceProvider.GetRequiredService<ISessionRepository>(),
                new PasswordHasher(opcoes),
                opcoes,
                TimeProvider.System,
                escopo.ServiceProvider.GetRequiredService<IAuditWriter>(),
                escopo.ServiceProvider.GetRequiredService<IRequestContextAccessor>());

            await autenticacao.LoginAsync(a.Admin.TenantSlug, a.Admin.Email, a.Admin.Senha);
        }

        await using var consulta = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var usuario = await consulta.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(a.Admin.Id, default);
        Assert.StartsWith($"pbkdf2-sha256${maisForte}$", usuario!.PasswordHash);

        // A senha continua valendo com o hash novo.
        await using var outro = banco.NovoEscopo();
        Assert.NotEmpty((await outro.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(a.Admin.TenantSlug, a.Admin.Email, a.Admin.Senha)).Token);
    }

    // Sessões

    [Fact]
    public async Task Sessao_revogada_e_recusada_e_a_revogacao_vale_so_para_a_propria_sessao()
    {
        var a = await MontarAsync("a");
        var primeira = await LoginAsync(a.Admin);
        var segunda = await LoginAsync(a.Admin);

        await using (var escopo = banco.NovoEscopo())
        {
            var principal = await escopo.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(primeira.Token);
            Assert.NotNull(principal);
            escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(
                new RequestContext(principal.TenantId, "session", new ActorPrincipal(principal.UserId, principal.SessionId, principal.Permissions)));

            await escopo.ServiceProvider.GetRequiredService<AuthenticationService>().LogoutAsync();
        }

        await using var depois = banco.NovoEscopo();
        var autenticador = depois.ServiceProvider.GetRequiredService<SessionAuthenticator>();
        Assert.Null(await autenticador.AuthenticateAsync(primeira.Token));
        Assert.NotNull(await autenticador.AuthenticateAsync(segunda.Token));
    }

    [Fact]
    public async Task Sessao_expirada_e_recusada_com_relogio_controlado()
    {
        var a = await MontarAsync("a");
        var login = await LoginAsync(a.Admin);

        await using var escopo = banco.NovoEscopo();
        var tenants = escopo.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var loja = escopo.ServiceProvider.GetRequiredService<IAuthenticationStore>();

        var noLimite = new SessionAuthenticator(loja, tenants, new RelogioFixoEm(DateTimeOffset.UtcNow.AddHours(8).AddMinutes(-1)));
        var depois = new SessionAuthenticator(loja, tenants, new RelogioFixoEm(DateTimeOffset.UtcNow.AddHours(8).AddMinutes(1)));

        Assert.NotNull(await noLimite.AuthenticateAsync(login.Token));
        Assert.Null(await depois.AuthenticateAsync(login.Token));
    }

    [Fact]
    public async Task Concessao_revogada_reduz_as_permissoes_na_proxima_validacao_do_token()
    {
        var a = await MontarAsync("a");
        var usuario = await banco.CriarUsuarioAsync(a.Tenant, a.Organizacao.Id, Role.Manager);
        var login = await LoginAsync(usuario);

        await using (var admin = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant)))
        {
            await admin.ServiceProvider.GetRequiredService<UserService>().RevokeRoleAsync(a.Organizacao.Id, usuario.Id);
        }

        await using var escopo = banco.NovoEscopo();
        var principal = await escopo.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token);
        Assert.NotNull(principal);
        Assert.Empty(principal.Permissions);
    }

    [Fact]
    public async Task Tenant_suspenso_invalida_as_sessoes_dele()
    {
        var a = await MontarAsync("a");
        var login = await LoginAsync(a.Admin);

        await using (var escopo = banco.NovoEscopo())
        {
            await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().SuspendAsync(a.Tenant.Slug);
        }

        await using var depois = banco.NovoEscopo();
        Assert.Null(await depois.ServiceProvider.GetRequiredService<SessionAuthenticator>().AuthenticateAsync(login.Token));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            depois.ServiceProvider.GetRequiredService<AuthenticationService>().LoginAsync(a.Tenant.Slug, a.Admin.Email, a.Admin.Senha));
    }

    // Restrições do banco

    [Fact]
    public async Task Banco_recusa_duas_concessoes_para_o_mesmo_usuario_e_escopo_inclusive_no_escopo_de_tenant()
    {
        var a = await MontarAsync("a");
        var tenantWide = await banco.CriarAdministradorDoTenantAsync(a.Tenant);

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.Grants.Add(UserOrganizationGrant.Create(a.Tenant.TenantId, tenantWide.Id, null, Role.Manager, DateTimeOffset.UtcNow, null));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(falha.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ix_user_organization_grants_tenant_user_scope", postgres.ConstraintName);
    }

    [Fact]
    public async Task Banco_recusa_sessao_de_usuario_de_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var (_, hash) = SessionTokens.Generate();
        dbB.Sessions.Add(Session.Create(b.Tenant.TenantId, a.Admin.Id, hash, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => dbB.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    [Fact]
    public async Task Banco_recusa_concessao_de_usuario_de_outro_tenant()
    {
        var a = await MontarAsync("a");
        var b = await MontarAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b.Tenant));
        var dbB = escopoB.ServiceProvider.GetRequiredService<IdentityDbContext>();
        dbB.Grants.Add(UserOrganizationGrant.Create(b.Tenant.TenantId, a.Admin.Id, b.Organizacao.Id, Role.User, DateTimeOffset.UtcNow, null));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => dbB.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    [Fact]
    public async Task Hash_de_token_repetido_e_recusado_pelo_banco()
    {
        var a = await MontarAsync("a");
        var (_, hash) = SessionTokens.Generate();

        await using var escopo = banco.NovoEscopo(PostgresFixture.Contexto(a.Tenant));
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();
        db.Sessions.Add(Session.Create(a.Tenant.TenantId, a.Admin.Id, hash, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));
        db.Sessions.Add(Session.Create(a.Tenant.TenantId, a.Admin.Id, hash, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(falha.InnerException).SqlState);
    }

    private sealed class RelogioFixoEm : TimeProvider
    {
        private readonly DateTimeOffset _agora;

        public RelogioFixoEm(DateTimeOffset agora)
        {
            _agora = agora;
        }

        public override DateTimeOffset GetUtcNow() => _agora;
    }
}
