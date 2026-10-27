using System.Text;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Tests;

public class SessionTokensTests
{
    [Fact]
    public void Token_tem_prefixo_tamanho_fixo_e_hash_sha256()
    {
        var (token, hash) = SessionTokens.Generate();

        Assert.StartsWith(SessionTokens.Prefix, token);
        Assert.Equal(47, token.Length);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void Hash_apresentado_e_igual_ao_hash_gerado()
    {
        var (token, hash) = SessionTokens.Generate();

        Assert.True(SessionTokens.TryHash(token, out var apresentado));
        Assert.Equal(hash, apresentado);
    }

    [Fact]
    public void O_hash_nao_revela_o_token()
    {
        var (token, hash) = SessionTokens.Generate();

        Assert.NotEqual(Encoding.UTF8.GetBytes(token), hash);
        Assert.DoesNotContain(token, Convert.ToBase64String(hash));
    }

    [Fact]
    public void Tokens_gerados_nao_se_repetem()
    {
        var tokens = Enumerable.Range(0, 2_000).Select(_ => SessionTokens.Generate().Token).ToHashSet();

        Assert.Equal(2_000, tokens.Count);
    }

    [Fact]
    public void Tokens_gerados_usam_so_o_alfabeto_base64url()
    {
        for (var i = 0; i < 200; i++)
        {
            var (token, _) = SessionTokens.Generate();
            Assert.True(SessionTokens.TryHash(token, out _));
            Assert.All(token.Substring(SessionTokens.Prefix.Length), c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ibs_curto")]
    [InlineData("xxx_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ibs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ibs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+/")]
    [InlineData("ibs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA =")]
    public void Texto_que_nao_e_um_token_nosso_e_descartado_sem_hash(string? texto)
    {
        Assert.False(SessionTokens.TryHash(texto, out var hash));
        Assert.Empty(hash);
    }

    [Fact]
    public void Token_adulterado_gera_outro_hash()
    {
        var (token, hash) = SessionTokens.Generate();
        var trocado = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.True(SessionTokens.TryHash(trocado, out var outro));
        Assert.NotEqual(hash, outro);
    }
}

public class RolePermissionsTests
{
    private static readonly Guid Organizacao = Guid.NewGuid();

    private static HashSet<Permission> Permissoes(Role papel, Guid? organizacao) =>
        RolePermissions.Expand(papel, organizacao).Select(p => p.Permission).ToHashSet();

    [Fact]
    public void Usuario_so_le_a_organizacao_e_a_estrutura()
    {
        Assert.Equal(
            new HashSet<Permission> { Permission.OrganizationRead, Permission.StructureRead },
            Permissoes(Role.User, Organizacao));
    }

    [Fact]
    public void Gestor_acrescenta_gerir_estrutura_e_ver_usuarios()
    {
        Assert.Equal(
            new HashSet<Permission> { Permission.OrganizationRead, Permission.StructureRead, Permission.StructureManage, Permission.UserRead },
            Permissoes(Role.Manager, Organizacao));
    }

    [Fact]
    public void Administrador_da_organizacao_acrescenta_gerir_usuarios_mas_nao_o_que_e_do_tenant()
    {
        var permissoes = Permissoes(Role.OrganizationAdmin, Organizacao);

        Assert.Contains(Permission.UserManage, permissoes);
        Assert.DoesNotContain(Permission.OrganizationCreate, permissoes);
        Assert.DoesNotContain(Permission.AuditRead, permissoes);
    }

    [Fact]
    public void Administrador_com_escopo_de_tenant_acrescenta_criar_organizacao_e_ler_auditoria()
    {
        var permissoes = Permissoes(Role.OrganizationAdmin, null);

        Assert.Contains(Permission.OrganizationCreate, permissoes);
        Assert.Contains(Permission.AuditRead, permissoes);
        Assert.Contains(Permission.UserManage, permissoes);
    }

    [Theory]
    [InlineData(Role.User)]
    [InlineData(Role.Manager)]
    public void Papeis_menores_nunca_ganham_permissoes_de_tenant_mesmo_com_escopo_de_tenant(Role papel)
    {
        var permissoes = Permissoes(papel, null);

        Assert.DoesNotContain(Permission.OrganizationCreate, permissoes);
        Assert.DoesNotContain(Permission.AuditRead, permissoes);
        Assert.DoesNotContain(Permission.UserManage, permissoes);
    }

    [Fact]
    public void As_permissoes_levam_o_escopo_da_concessao()
    {
        Assert.All(RolePermissions.Expand(Role.Manager, Organizacao), p => Assert.Equal(Organizacao, p.OrganizationId));
        Assert.All(RolePermissions.Expand(Role.Manager, null), p => Assert.Null(p.OrganizationId));
    }

    [Fact]
    public void Papel_inexistente_nao_concede_nada()
    {
        Assert.Empty(RolePermissions.Expand((Role)99, Organizacao));
    }

    [Fact]
    public void Cada_papel_e_estritamente_maior_que_o_anterior()
    {
        var usuario = Permissoes(Role.User, Organizacao);
        var gestor = Permissoes(Role.Manager, Organizacao);
        var administrador = Permissoes(Role.OrganizationAdmin, Organizacao);

        Assert.True(usuario.IsProperSubsetOf(gestor));
        Assert.True(gestor.IsProperSubsetOf(administrador));
    }
}
