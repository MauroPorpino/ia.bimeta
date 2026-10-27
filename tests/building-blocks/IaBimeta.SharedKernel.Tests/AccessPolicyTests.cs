namespace IaBimeta.SharedKernel.Tests;

public class AccessPolicyTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());

    private static RequestContext Usuario(params PermissionGrant[] permissoes) =>
        new(Tenant, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), permissoes));

    private static RequestContext Sistema() => new(Tenant, "teste");

    [Fact]
    public void Contexto_de_sistema_e_identificado_e_nao_passa_por_permissao_de_usuario()
    {
        var contexto = Sistema();

        Assert.True(contexto.IsSystem);
        Assert.Null(contexto.Principal);
        Assert.True(contexto.IsAllowed(Permission.UserManage, Guid.NewGuid()));
        Assert.True(contexto.IsAllowed(Permission.OrganizationCreate));
        Assert.Null(contexto.OrganizationsWithPermission(Permission.OrganizationRead));
    }

    [Fact]
    public void Contexto_de_usuario_nao_e_de_sistema()
    {
        var contexto = Usuario();

        Assert.False(contexto.IsSystem);
        Assert.NotNull(contexto.Principal);
    }

    [Fact]
    public void Usuario_sem_concessoes_nega_tudo_por_padrao()
    {
        var contexto = Usuario();

        foreach (var permissao in Enum.GetValues<Permission>())
        {
            Assert.False(contexto.IsAllowed(permissao, Guid.NewGuid()));
            Assert.False(contexto.IsAllowed(permissao));
        }
    }

    [Fact]
    public void Concessao_de_organizacao_vale_so_para_aquela_organizacao()
    {
        var minha = Guid.NewGuid();
        var contexto = Usuario(new PermissionGrant(Permission.StructureManage, minha));

        Assert.True(contexto.IsAllowed(Permission.StructureManage, minha));
        Assert.False(contexto.IsAllowed(Permission.StructureManage, Guid.NewGuid()));
    }

    [Fact]
    public void Concessao_de_organizacao_nao_cobre_permissao_de_escopo_do_tenant()
    {
        var contexto = Usuario(new PermissionGrant(Permission.OrganizationCreate, Guid.NewGuid()));

        Assert.False(contexto.IsAllowed(Permission.OrganizationCreate));
    }

    [Fact]
    public void Concessao_de_tenant_cobre_qualquer_organizacao_e_o_escopo_do_tenant()
    {
        var contexto = Usuario(new PermissionGrant(Permission.StructureRead, null));

        Assert.True(contexto.IsAllowed(Permission.StructureRead, Guid.NewGuid()));
        Assert.True(contexto.IsAllowed(Permission.StructureRead));
    }

    [Fact]
    public void Permissao_diferente_nao_e_concedida()
    {
        var organizacao = Guid.NewGuid();
        var contexto = Usuario(new PermissionGrant(Permission.StructureRead, organizacao));

        Assert.False(contexto.IsAllowed(Permission.StructureManage, organizacao));
        Assert.False(contexto.IsAllowed(Permission.UserManage, organizacao));
    }

    [Fact]
    public void Require_lanca_acesso_negado_com_a_permissao_exigida()
    {
        var contexto = Usuario();

        var falha = Assert.Throws<AccessDeniedException>(() => contexto.Require(Permission.UserManage, Guid.NewGuid()));

        Assert.Equal(Permission.UserManage, falha.Permission);
    }

    [Fact]
    public void Require_nao_lanca_quando_ha_concessao()
    {
        var organizacao = Guid.NewGuid();
        var contexto = Usuario(new PermissionGrant(Permission.UserManage, organizacao));

        contexto.Require(Permission.UserManage, organizacao);
    }

    [Fact]
    public void Organizacoes_com_permissao_listam_so_as_concedidas()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var contexto = Usuario(
            new PermissionGrant(Permission.OrganizationRead, a),
            new PermissionGrant(Permission.OrganizationRead, b),
            new PermissionGrant(Permission.StructureManage, Guid.NewGuid()));

        var visiveis = contexto.OrganizationsWithPermission(Permission.OrganizationRead);

        Assert.NotNull(visiveis);
        Assert.Equal(new HashSet<Guid> { a, b }, visiveis);
    }

    [Fact]
    public void Organizacoes_com_permissao_sao_irrestritas_na_concessao_de_tenant_e_vazias_sem_concessao()
    {
        Assert.Null(Usuario(new PermissionGrant(Permission.OrganizationRead, null)).OrganizationsWithPermission(Permission.OrganizationRead));
        Assert.Empty(Usuario().OrganizationsWithPermission(Permission.OrganizationRead)!);
    }

    [Fact]
    public void RequirePrincipal_exige_usuario_autenticado()
    {
        Assert.Throws<AuthenticationRequiredException>(() => Sistema().RequirePrincipal());

        var contexto = Usuario();
        Assert.Same(contexto.Principal, contexto.RequirePrincipal());
    }

    [Fact]
    public void Principal_guarda_usuario_sessao_e_copia_as_concessoes()
    {
        var usuario = Guid.NewGuid();
        var sessao = Guid.NewGuid();
        var lista = new List<PermissionGrant> { new(Permission.OrganizationRead, null) };

        var principal = new ActorPrincipal(usuario, sessao, lista);
        lista.Clear();

        Assert.Equal(usuario, principal.UserId);
        Assert.Equal(sessao, principal.SessionId);
        Assert.Single(principal.Grants);
    }
}
