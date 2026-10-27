using IaBimeta.Audit.Contracts;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Tests;

/// <summary>
/// Autorização e regras dos casos de uso de usuário e concessão (TEN-2): nega por padrão, a organização vem da rota e
/// é validada contra as concessões, sem leitura sobre ela o recurso "não existe" e a negação é auditada.
/// </summary>
public class UserServiceTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());

    private sealed class Cenario
    {
        public IdentidadeEmMemoria Dados { get; } = new();
        public OrganizacoesEmMemoria Organizacoes { get; } = new();
        public AuditoriaEmMemoria Auditoria { get; } = new();
        public RequestContextHolder Holder { get; } = new();
        public PasswordHasher Hasher { get; } = new(Senhas.Rapidas());

        public UserService Servico => new(Dados, Dados, Organizacoes, Hasher, Holder, TimeProvider.System, Auditoria);

        public void ComoSistema() => Holder.Set(new RequestContext(Tenant, "development-seed"));

        public void ComoUsuario(params PermissionGrant[] permissoes) =>
            Holder.Set(new RequestContext(Tenant, "session", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), permissoes)));

        public void ComoPapel(Role papel, Guid? organizacao) =>
            ComoUsuario(RolePermissions.Expand(papel, organizacao).ToArray());

        public User NovoUsuario(string email = "bia@exemplo.com")
        {
            var usuario = User.Create(Tenant, email, "Bia", Hasher.Hash(Senhas.Valida), DateTimeOffset.UtcNow);
            Dados.Usuarios.Add(usuario);
            return usuario;
        }
    }

    // Criar usuário

    [Fact]
    public async Task Administrador_da_organizacao_cria_usuario_com_papel_e_a_senha_vira_hash()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);

        var criado = await c.Servico.CreateAsync(organizacao, "Bia@Exemplo.com", "Bia", Senhas.Valida, Role.Manager);

        Assert.Equal("bia@exemplo.com", criado.Email);
        var usuario = Assert.Single(c.Dados.Usuarios);
        Assert.StartsWith("pbkdf2-sha256$", usuario.PasswordHash);
        Assert.DoesNotContain(Senhas.Valida, usuario.PasswordHash);
        var concessao = Assert.Single(c.Dados.Concessoes);
        Assert.Equal(organizacao, concessao.OrganizationId);
        Assert.Equal(Role.Manager, concessao.Role);
        Assert.NotNull(concessao.GrantedByUserId);
        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal("identity.user.create", evento.Entrada.Operation);
        Assert.Equal(AuditOutcome.Success, evento.Entrada.Outcome);
        Assert.DoesNotContain(Senhas.Valida, evento.Entrada.ResourceId ?? string.Empty);
    }

    [Theory]
    [InlineData(Role.User)]
    [InlineData(Role.Manager)]
    public async Task Papeis_menores_nao_criam_usuarios_e_a_negacao_e_auditada(Role papel)
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        c.ComoPapel(papel, organizacao);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.CreateAsync(organizacao, "bia@exemplo.com", "Bia", Senhas.Valida, Role.User));

        Assert.Empty(c.Dados.Usuarios);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(c.Auditoria.Eventos).Entrada.Outcome);
    }

    [Fact]
    public async Task Administrador_de_uma_organizacao_nao_cria_usuario_em_outra_que_responde_como_inexistente()
    {
        var c = new Cenario();
        var minha = c.Organizacoes.Adicionar();
        var alheia = c.Organizacoes.Adicionar();
        c.ComoPapel(Role.OrganizationAdmin, minha);

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.CreateAsync(alheia, "bia@exemplo.com", "Bia", Senhas.Valida, Role.User));

        Assert.Empty(c.Dados.Usuarios);
    }

    [Fact]
    public async Task Organizacao_inexistente_responde_igual_a_organizacao_sem_concessao()
    {
        var c = new Cenario();
        c.ComoPapel(Role.OrganizationAdmin, null);

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.CreateAsync(Guid.NewGuid(), "bia@exemplo.com", "Bia", Senhas.Valida, Role.User));
    }

    [Fact]
    public async Task Dados_invalidos_sao_recusados_antes_de_gravar()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.CreateAsync(organizacao, "invalido", "Bia", Senhas.Valida, Role.User));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.CreateAsync(organizacao, "bia@exemplo.com", " ", Senhas.Valida, Role.User));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.CreateAsync(organizacao, "bia@exemplo.com", "Bia", "curta", Role.User));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.CreateAsync(organizacao, "bia@exemplo.com", "Bia", Senhas.Valida, (Role)99));

        Assert.Empty(c.Dados.Usuarios);
    }

    [Fact]
    public async Task E_mail_repetido_no_tenant_e_recusado()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);
        await c.Servico.CreateAsync(organizacao, "bia@exemplo.com", "Bia", Senhas.Valida, Role.User);

        await Assert.ThrowsAsync<DuplicateUserEmailException>(() => c.Servico.CreateAsync(organizacao, "BIA@exemplo.com", "Outra Bia", Senhas.Valida, Role.User));
    }

    // Conceder e revogar

    [Fact]
    public async Task Conceder_cria_a_concessao_e_conceder_de_novo_troca_o_papel()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);

        await c.Servico.GrantRoleAsync(organizacao, usuario.Id, Role.User);
        await c.Servico.GrantRoleAsync(organizacao, usuario.Id, Role.Manager);

        var concessao = Assert.Single(c.Dados.Concessoes);
        Assert.Equal(Role.Manager, concessao.Role);
        Assert.Equal(2, c.Auditoria.Entradas.Count(e => e.Operation == "identity.grant.set" && e.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Gestor_nao_concede_papeis()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.ComoPapel(Role.Manager, organizacao);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.GrantRoleAsync(organizacao, usuario.Id, Role.OrganizationAdmin));

        Assert.Empty(c.Dados.Concessoes);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(c.Auditoria.Eventos).Entrada.Outcome);
    }

    [Fact]
    public async Task Conceder_a_usuario_inexistente_responde_nao_encontrado()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.GrantRoleAsync(organizacao, Guid.NewGuid(), Role.User));
    }

    [Fact]
    public async Task Conceder_papel_inexistente_e_recusado()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.GrantRoleAsync(organizacao, usuario.Id, (Role)99));
    }

    [Fact]
    public async Task Revogar_remove_a_concessao_e_audita()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);
        await c.Servico.GrantRoleAsync(organizacao, usuario.Id, Role.User);

        await c.Servico.RevokeRoleAsync(organizacao, usuario.Id);

        Assert.Empty(c.Dados.Concessoes);
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.grant.revoke" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Revogar_concessao_que_nao_existe_responde_nao_encontrado()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.ComoPapel(Role.OrganizationAdmin, organizacao);

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.RevokeRoleAsync(organizacao, usuario.Id));
    }

    [Fact]
    public async Task Usuario_comum_nao_revoga_concessoes()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.ComoPapel(Role.User, organizacao);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.RevokeRoleAsync(organizacao, usuario.Id));
    }

    // Listar

    [Fact]
    public async Task Gestor_lista_os_usuarios_da_organizacao_com_o_papel_e_o_escopo()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var bia = c.NovoUsuario();
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant, bia.Id, organizacao, Role.User, DateTimeOffset.UtcNow, null));
        var admin = c.NovoUsuario("admin@exemplo.com");
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant, admin.Id, null, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null));
        var deOutra = c.NovoUsuario("outra@exemplo.com");
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant, deOutra.Id, Guid.NewGuid(), Role.User, DateTimeOffset.UtcNow, null));
        c.ComoPapel(Role.Manager, organizacao);

        var lista = await c.Servico.ListAsync(organizacao);

        Assert.Equal(new[] { "admin@exemplo.com", "bia@exemplo.com" }, lista.Select(u => u.Email).OrderBy(e => e));
        Assert.True(lista.Single(u => u.Email == "admin@exemplo.com").TenantWide);
        Assert.False(lista.Single(u => u.Email == "bia@exemplo.com").TenantWide);
    }

    [Fact]
    public async Task Usuario_comum_nao_lista_usuarios()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        c.ComoPapel(Role.User, organizacao);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.ListAsync(organizacao));
    }

    // Usuário atual

    [Fact]
    public async Task Usuario_atual_exige_usuario_autenticado()
    {
        var c = new Cenario();
        c.ComoSistema();

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.Servico.GetCurrentAsync());
    }

    [Fact]
    public async Task Usuario_atual_devolve_os_dados_e_as_concessoes_sem_hash()
    {
        var c = new Cenario();
        var organizacao = c.Organizacoes.Adicionar();
        var usuario = c.NovoUsuario();
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant, usuario.Id, organizacao, Role.Manager, DateTimeOffset.UtcNow, null));
        c.Holder.Set(new RequestContext(Tenant, "session", new ActorPrincipal(usuario.Id, Guid.NewGuid(), Array.Empty<PermissionGrant>())));

        var atual = await c.Servico.GetCurrentAsync();

        Assert.Equal(usuario.Email, atual.Email);
        var concessao = Assert.Single(atual.Grants);
        Assert.Equal(organizacao, concessao.OrganizationId);
        Assert.Equal(Role.Manager, concessao.Role);
    }

    // Administrador do tenant (bootstrap)

    [Fact]
    public async Task Contexto_de_sistema_cria_o_administrador_do_tenant_com_concessao_de_tenant_e_e_idempotente()
    {
        var c = new Cenario();
        c.ComoSistema();

        Assert.True(await c.Servico.EnsureTenantAdministratorAsync("admin@exemplo.com", "Admin", Senhas.Valida));
        Assert.False(await c.Servico.EnsureTenantAdministratorAsync("ADMIN@exemplo.com", "Admin", Senhas.Valida));

        var concessao = Assert.Single(c.Dados.Concessoes);
        Assert.Null(concessao.OrganizationId);
        Assert.Equal(Role.OrganizationAdmin, concessao.Role);
        Assert.Single(c.Dados.Usuarios);
    }

    [Fact]
    public async Task Nenhum_usuario_autenticado_cria_administrador_de_tenant()
    {
        var c = new Cenario();
        c.ComoPapel(Role.OrganizationAdmin, null);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.EnsureTenantAdministratorAsync("admin@exemplo.com", "Admin", Senhas.Valida));

        Assert.Empty(c.Dados.Usuarios);
    }

    [Fact]
    public async Task Administrador_de_tenant_com_senha_fraca_e_recusado()
    {
        var c = new Cenario();
        c.ComoSistema();

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.EnsureTenantAdministratorAsync("admin@exemplo.com", "Admin", "123"));
    }

    [Fact]
    public async Task Sem_contexto_todos_os_casos_de_uso_de_usuario_falham_fechados()
    {
        var c = new Cenario();
        var organizacao = Guid.NewGuid();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.CreateAsync(organizacao, "a@b.co", "A", Senhas.Valida, Role.User));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.GrantRoleAsync(organizacao, Guid.NewGuid(), Role.User));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.RevokeRoleAsync(organizacao, Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.ListAsync(organizacao));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.GetCurrentAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.EnsureTenantAdministratorAsync("a@b.co", "A", Senhas.Valida));
    }
}
