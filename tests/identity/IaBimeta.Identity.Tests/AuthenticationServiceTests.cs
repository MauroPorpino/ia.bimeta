using IaBimeta.Audit.Contracts;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Identity.Tests;

/// <summary>
/// Regras do login, do logout e da validação de sessão. O comportamento que depende de persistência real (hash no
/// banco, isolamento, revogação por HTTP) é provado com PostgreSQL em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class AuthenticationServiceTests
{
    private sealed class Cenario
    {
        public RelogioManual Relogio { get; } = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        public IdentidadeEmMemoria Dados { get; } = new();
        public TenantsEmMemoria Tenants { get; } = new();
        public AuditoriaEmMemoria Auditoria { get; } = new();
        public IdentityOptions Opcoes { get; } = Senhas.Rapidas();
        public RequestContextHolder Holder { get; } = new();
        public PasswordHasher Hasher { get; }
        public TenantInfo Tenant { get; }

        public Cenario()
        {
            Hasher = new PasswordHasher(Opcoes);
            Tenant = Tenants.Adicionar("lab-a");
        }

        public AuthenticationService Autenticacao => new(Tenants, Dados, Dados, Hasher, Opcoes, Relogio, Auditoria, Holder);

        public SessionAuthenticator Autenticador => new(Dados, Tenants, Relogio);

        public User NovoUsuario(string email = "ana@exemplo.com", string senha = Senhas.Valida, TenantInfo? tenant = null, string? hash = null)
        {
            var dono = tenant ?? Tenant;
            var usuario = User.Create(dono.TenantId, email, "Ana", hash ?? Hasher.Hash(senha), Relogio.GetUtcNow());
            Dados.Usuarios.Add(usuario);
            return usuario;
        }

        public void ConcederPapel(User usuario, Role papel, Guid? organizacao)
        {
            Dados.Concessoes.Add(UserOrganizationGrant.Create(usuario.TenantId, usuario.Id, organizacao, papel, Relogio.GetUtcNow(), null));
        }
    }

    // Login

    [Fact]
    public async Task Credenciais_validas_criam_a_sessao_e_devolvem_o_token_uma_unica_vez()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();

        var resultado = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        Assert.StartsWith(SessionTokens.Prefix, resultado.Token);
        Assert.Equal(c.Relogio.GetUtcNow() + c.Opcoes.SessionLifetime, resultado.ExpiresAt);
        var sessao = Assert.Single(c.Dados.Sessoes);
        Assert.Equal(usuario.Id, sessao.UserId);
        Assert.Equal(c.Tenant.TenantId, sessao.TenantId);
    }

    [Fact]
    public async Task A_sessao_guarda_so_o_hash_do_token()
    {
        var c = new Cenario();
        c.NovoUsuario();

        var resultado = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        var sessao = Assert.Single(c.Dados.Sessoes);
        Assert.True(SessionTokens.TryHash(resultado.Token, out var hash));
        Assert.Equal(hash, sessao.TokenHash);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(resultado.Token), sessao.TokenHash);
    }

    [Theory]
    [InlineData("LAB-A", "ANA@EXEMPLO.COM")]
    [InlineData("  lab-a ", "  ana@exemplo.com ")]
    public async Task Slug_e_e_mail_sao_normalizados(string slug, string email)
    {
        var c = new Cenario();
        c.NovoUsuario();

        var resultado = await c.Autenticacao.LoginAsync(slug, email, Senhas.Valida);

        Assert.NotEmpty(resultado.Token);
    }

    [Fact]
    public async Task Todas_as_falhas_de_login_viram_a_mesma_excecao_sem_criar_sessao()
    {
        var c = new Cenario();
        c.NovoUsuario();
        c.Tenants.Adicionar("lab-suspenso", TenantStatus.Suspended);
        c.NovoUsuario("ana@exemplo.com", tenant: c.Tenants.Adicionar("lab-b"));

        var tentativas = new (string? Tenant, string? Email, string? Senha)[]
        {
            ("lab-a", "ana@exemplo.com", Senhas.Outra),
            ("lab-a", "ninguem@exemplo.com", Senhas.Valida),
            ("nao-existe", "ana@exemplo.com", Senhas.Valida),
            ("lab-suspenso", "ana@exemplo.com", Senhas.Valida),
            ("lab-a", null, Senhas.Valida),
            ("lab-a", "ana@exemplo.com", null),
            ("lab-a", "ana@exemplo.com", ""),
            ("lab-a", "email-invalido", Senhas.Valida),
            (null, "ana@exemplo.com", Senhas.Valida),
            ("lab-a", "ana@exemplo.com", new string('x', PasswordPolicy.MaxLength + 1)),
        };

        foreach (var (tenant, email, senha) in tentativas)
        {
            var falha = await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync(tenant, email, senha));
            Assert.Equal("Credenciais inválidas.", falha.Message);
        }

        Assert.Empty(c.Dados.Sessoes);
    }

    [Fact]
    public async Task Senha_de_outro_tenant_para_o_mesmo_e_mail_nao_entra()
    {
        var c = new Cenario();
        c.NovoUsuario("ana@exemplo.com", "Senha-Do-Tenant-A!");
        var outro = c.Tenants.Adicionar("lab-b");
        c.NovoUsuario("ana@exemplo.com", "Senha-Do-Tenant-B!", outro);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", "Senha-Do-Tenant-B!"));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-b", "ana@exemplo.com", "Senha-Do-Tenant-A!"));
        Assert.NotEmpty((await c.Autenticacao.LoginAsync("lab-b", "ana@exemplo.com", "Senha-Do-Tenant-B!")).Token);
    }

    [Fact]
    public async Task Sessao_criada_e_do_tenant_do_usuario_encontrado_e_nunca_do_slug_de_outro()
    {
        var c = new Cenario();
        var outro = c.Tenants.Adicionar("lab-b");
        var usuarioDeB = c.NovoUsuario("bia@exemplo.com", tenant: outro);

        await c.Autenticacao.LoginAsync("lab-b", "bia@exemplo.com", Senhas.Valida);

        var sessao = Assert.Single(c.Dados.Sessoes);
        Assert.Equal(outro.TenantId, sessao.TenantId);
        Assert.Equal(usuarioDeB.Id, sessao.UserId);
    }

    // Auditoria do login

    [Fact]
    public async Task Login_bem_sucedido_e_auditado_com_o_tenant_e_o_ator_sem_expor_segredos()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();

        var resultado = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.True(evento.AntesDoContexto);
        Assert.Equal(c.Tenant.TenantId, evento.Tenant);
        Assert.Equal(usuario.Id, evento.Ator);
        Assert.Equal("identity.login", evento.Entrada.Operation);
        Assert.Equal(AuditOutcome.Success, evento.Entrada.Outcome);
        SemSegredos(c, resultado.Token);
    }

    [Fact]
    public async Task Login_negado_e_auditado_quando_o_tenant_existe_e_nao_revela_a_senha()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Outra));

        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal(AuditOutcome.Denied, evento.Entrada.Outcome);
        Assert.Equal(usuario.Id, evento.Ator);
        SemSegredos(c, Senhas.Outra);
    }

    [Fact]
    public async Task Login_negado_de_usuario_inexistente_e_auditado_sem_ator()
    {
        var c = new Cenario();

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "ninguem@exemplo.com", Senhas.Valida));

        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.Null(evento.Ator);
        Assert.Null(evento.Entrada.ResourceId);
        Assert.Equal(AuditOutcome.Denied, evento.Entrada.Outcome);
    }

    [Fact]
    public async Task Tenant_inexistente_ou_suspenso_nao_gera_evento_pois_nao_ha_tenant_para_auditar()
    {
        var c = new Cenario();
        c.Tenants.Adicionar("lab-suspenso", TenantStatus.Suspended);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("nao-existe", "ana@exemplo.com", Senhas.Valida));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-suspenso", "ana@exemplo.com", Senhas.Valida));

        Assert.Empty(c.Auditoria.Eventos);
    }

    private static void SemSegredos(Cenario c, params string[] segredos)
    {
        foreach (var evento in c.Auditoria.Eventos)
        {
            var texto = $"{evento.Entrada.Operation}|{evento.Entrada.ResourceType}|{evento.Entrada.ResourceId}|{evento.Entrada.Outcome}";
            foreach (var segredo in segredos)
            {
                Assert.DoesNotContain(segredo, texto);
            }

            Assert.DoesNotContain("ana@exemplo.com", texto);
        }
    }

    // Regravação do hash

    [Fact]
    public async Task Login_com_hash_de_parametros_antigos_regrava_o_hash_com_os_atuais()
    {
        var c = new Cenario();
        var antigo = new PasswordHasher(new IdentityOptions { PasswordHashIterations = 10_000 }).Hash(Senhas.Valida);
        c.Opcoes.PasswordHashIterations = 20_000;
        var hasherAtual = new PasswordHasher(c.Opcoes);
        var usuario = c.NovoUsuario(hash: antigo);
        var servico = new AuthenticationService(c.Tenants, c.Dados, c.Dados, hasherAtual, c.Opcoes, c.Relogio, c.Auditoria, c.Holder);

        await servico.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        Assert.NotEqual(antigo, usuario.PasswordHash);
        Assert.StartsWith("pbkdf2-sha256$20000$", usuario.PasswordHash);
        Assert.Equal(PasswordVerification.Success, hasherAtual.Verify(Senhas.Valida, usuario.PasswordHash));
    }

    [Fact]
    public async Task Login_com_hash_atual_nao_regrava()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();
        var antes = usuario.PasswordHash;

        await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        Assert.Equal(antes, usuario.PasswordHash);
    }

    // Logout

    [Fact]
    public async Task Logout_revoga_a_sessao_do_contexto_e_audita()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();
        await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);
        var sessao = Assert.Single(c.Dados.Sessoes);
        c.Holder.Set(new RequestContext(c.Tenant.TenantId, "session", new ActorPrincipal(usuario.Id, sessao.Id, Array.Empty<PermissionGrant>())));

        await c.Autenticacao.LogoutAsync();

        Assert.NotNull(sessao.RevokedAt);
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.logout" && e.ResourceId == sessao.Id.ToString());
    }

    [Fact]
    public async Task Logout_exige_contexto_e_usuario_autenticado()
    {
        var c = new Cenario();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Autenticacao.LogoutAsync());

        c.Holder.Set(new RequestContext(c.Tenant.TenantId, "development-header"));
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.Autenticacao.LogoutAsync());
    }

    // Validação da sessão

    [Fact]
    public async Task Token_valido_devolve_o_principal_com_as_permissoes_das_concessoes()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();
        var organizacao = Guid.NewGuid();
        c.ConcederPapel(usuario, Role.Manager, organizacao);
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        var principal = await c.Autenticador.AuthenticateAsync(login.Token);

        Assert.NotNull(principal);
        Assert.Equal(c.Tenant.TenantId, principal.TenantId);
        Assert.Equal(usuario.Id, principal.UserId);
        Assert.Equal(Assert.Single(c.Dados.Sessoes).Id, principal.SessionId);
        Assert.Contains(new PermissionGrant(Permission.StructureManage, organizacao), principal.Permissions);
        Assert.DoesNotContain(principal.Permissions, p => p.Permission == Permission.UserManage);
        Assert.All(principal.Permissions, p => Assert.Equal(organizacao, p.OrganizationId));
    }

    [Fact]
    public async Task Usuario_sem_concessoes_autentica_mas_nao_tem_nenhuma_permissao()
    {
        var c = new Cenario();
        c.NovoUsuario();
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        var principal = await c.Autenticador.AuthenticateAsync(login.Token);

        Assert.NotNull(principal);
        Assert.Empty(principal.Permissions);
    }

    [Fact]
    public async Task Sessao_expirada_e_recusada()
    {
        var c = new Cenario();
        c.NovoUsuario();
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        c.Relogio.Avancar(c.Opcoes.SessionLifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(await c.Autenticador.AuthenticateAsync(login.Token));

        c.Relogio.Avancar(TimeSpan.FromSeconds(1));
        Assert.Null(await c.Autenticador.AuthenticateAsync(login.Token));
    }

    [Fact]
    public async Task Sessao_revogada_e_recusada()
    {
        var c = new Cenario();
        c.NovoUsuario();
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);
        c.Dados.Sessoes.Single().Revoke(c.Relogio.GetUtcNow());

        Assert.Null(await c.Autenticador.AuthenticateAsync(login.Token));
    }

    [Fact]
    public async Task Token_desconhecido_e_recusado()
    {
        var c = new Cenario();
        c.NovoUsuario();
        await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        var (outroToken, _) = SessionTokens.Generate();

        Assert.Null(await c.Autenticador.AuthenticateAsync(outroToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("ibs_curto")]
    public async Task Token_malformado_e_recusado_sem_consultar_o_banco(string? token)
    {
        var c = new Cenario();

        Assert.Null(await c.Autenticador.AuthenticateAsync(token));
        Assert.Equal(0, c.Dados.BuscasDeSessao);
    }

    [Fact]
    public async Task Tenant_suspenso_depois_do_login_invalida_a_sessao()
    {
        var c = new Cenario();
        var tenantQueSeraSuspenso = c.Tenants.Adicionar("lab-c");
        c.NovoUsuario("ana@exemplo.com", tenant: tenantQueSeraSuspenso);
        var login = await c.Autenticacao.LoginAsync("lab-c", "ana@exemplo.com", Senhas.Valida);
        Assert.NotNull(await c.Autenticador.AuthenticateAsync(login.Token));

        var autenticador = new SessionAuthenticator(c.Dados, new TenantSuspensoDiretorio(tenantQueSeraSuspenso.TenantId), c.Relogio);

        Assert.Null(await autenticador.AuthenticateAsync(login.Token));
    }

    [Fact]
    public async Task Usuario_removido_invalida_a_sessao()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        c.Dados.Usuarios.Remove(usuario);

        Assert.Null(await c.Autenticador.AuthenticateAsync(login.Token));
    }

    [Fact]
    public async Task Concessao_revogada_reduz_as_permissoes_na_proxima_validacao()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();
        var organizacao = Guid.NewGuid();
        c.ConcederPapel(usuario, Role.OrganizationAdmin, organizacao);
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);
        Assert.NotEmpty((await c.Autenticador.AuthenticateAsync(login.Token))!.Permissions);

        c.Dados.Concessoes.Clear();

        Assert.Empty((await c.Autenticador.AuthenticateAsync(login.Token))!.Permissions);
    }

    private sealed class TenantSuspensoDiretorio : IaBimeta.Tenancy.Contracts.Queries.ITenantDirectory
    {
        private readonly TenantId _tenantId;

        public TenantSuspensoDiretorio(TenantId tenantId)
        {
            _tenantId = tenantId;
        }

        public Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult<TenantInfo?>(null);

        public Task<TenantInfo?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TenantInfo?>(new TenantInfo(_tenantId, "lab-c", "Lab C", TenantStatus.Suspended));
    }
}
