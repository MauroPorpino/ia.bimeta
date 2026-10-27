using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Tests;

public class SessionTokenAlphabetTests
{
    [Fact]
    public void Token_com_caractere_fora_do_alfabeto_e_descartado_mesmo_com_o_tamanho_certo()
    {
        var (token, _) = SessionTokens.Generate();

        foreach (var invalido in new[] { '+', '/', '=', ' ', '!', 'é' })
        {
            var adulterado = token[..^1] + invalido;
            Assert.Equal(token.Length, adulterado.Length);
            Assert.False(SessionTokens.TryHash(adulterado, out _), $"aceitou '{invalido}'");
        }
    }
}

public class UserDomainTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("  Ana@Exemplo.COM ", "ana@exemplo.com")]
    [InlineData("a@b.co", "a@b.co")]
    public void E_mail_e_normalizado_sem_espacos_e_em_minusculas(string informado, string esperado)
    {
        Assert.Equal(esperado, User.NormalizeEmail(informado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem-arroba")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    [InlineData("a@@c.com")]
    [InlineData("@c.com")]
    public void E_mail_invalido_nao_e_normalizado(string? informado)
    {
        Assert.Null(User.NormalizeEmail(informado));
    }

    [Fact]
    public void E_mail_acima_do_limite_e_invalido()
    {
        var longo = new string('a', User.EmailMaxLength) + "@b.com";

        Assert.Null(User.NormalizeEmail(longo));
    }

    [Fact]
    public void Usuario_guarda_e_mail_normalizado_nome_aparado_e_o_hash_nunca_a_senha()
    {
        var usuario = User.Create(Tenant, " Ana@Exemplo.com ", "  Ana  ", "pbkdf2-sha256$1$x$y", Agora);

        Assert.Equal("ana@exemplo.com", usuario.Email);
        Assert.Equal("Ana", usuario.DisplayName);
        Assert.Equal(Tenant, usuario.TenantId);
        Assert.Equal("pbkdf2-sha256$1$x$y", usuario.PasswordHash);
    }

    [Fact]
    public void Usuario_recusa_nome_vazio_ou_longo_e_e_mail_invalido()
    {
        Assert.Throws<IdentityValidationException>(() => User.Create(Tenant, "ana@exemplo.com", " ", "hash", Agora));
        Assert.Throws<IdentityValidationException>(() => User.Create(Tenant, "ana@exemplo.com", new string('x', User.DisplayNameMaxLength + 1), "hash", Agora));
        Assert.Throws<IdentityValidationException>(() => User.Create(Tenant, "invalido", "Ana", "hash", Agora));
    }

    [Fact]
    public void Trocar_o_hash_da_senha_atualiza_so_o_hash()
    {
        var usuario = User.Create(Tenant, "ana@exemplo.com", "Ana", "hash-antigo", Agora);

        usuario.ReplacePasswordHash("hash-novo");

        Assert.Equal("hash-novo", usuario.PasswordHash);
        Assert.Equal("ana@exemplo.com", usuario.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta")]
    [InlineData("onze-chars!")]
    public void Senha_abaixo_do_minimo_e_recusada(string? senha)
    {
        Assert.Throws<IdentityValidationException>(() => PasswordPolicy.Validate(senha));
    }

    [Fact]
    public void Senha_acima_do_maximo_e_recusada_e_a_do_limite_e_aceita()
    {
        Assert.Throws<IdentityValidationException>(() => PasswordPolicy.Validate(new string('x', PasswordPolicy.MaxLength + 1)));
        PasswordPolicy.Validate(new string('x', PasswordPolicy.MinLength));
        PasswordPolicy.Validate(new string('x', PasswordPolicy.MaxLength));
    }

    [Fact]
    public void A_mensagem_de_senha_invalida_nao_repete_a_senha()
    {
        var falha = Assert.Throws<IdentityValidationException>(() => PasswordPolicy.Validate("segredo"));

        Assert.DoesNotContain("segredo", falha.Message);
    }
}

public class SessionDomainTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Hash = new byte[Session.TokenHashLength];

    [Fact]
    public void Sessao_vale_ate_o_instante_de_expiracao_exclusive()
    {
        var sessao = Session.Create(Tenant, Guid.NewGuid(), Hash, Agora, TimeSpan.FromHours(8));

        Assert.True(sessao.IsActiveAt(Agora));
        Assert.True(sessao.IsActiveAt(Agora.AddHours(8).AddTicks(-1)));
        Assert.False(sessao.IsActiveAt(Agora.AddHours(8)));
        Assert.False(sessao.IsActiveAt(Agora.AddDays(1)));
    }

    [Fact]
    public void Sessao_revogada_deixa_de_valer_e_revogar_de_novo_nao_muda_nada()
    {
        var sessao = Session.Create(Tenant, Guid.NewGuid(), Hash, Agora, TimeSpan.FromHours(8));

        Assert.True(sessao.Revoke(Agora.AddMinutes(5)));
        Assert.False(sessao.Revoke(Agora.AddMinutes(10)));

        Assert.Equal(Agora.AddMinutes(5), sessao.RevokedAt);
        Assert.False(sessao.IsActiveAt(Agora.AddMinutes(6)));
    }

    [Fact]
    public void Sessao_exige_hash_de_32_bytes_e_duracao_positiva()
    {
        Assert.Throws<ArgumentException>(() => Session.Create(Tenant, Guid.NewGuid(), new byte[10], Agora, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Session.Create(Tenant, Guid.NewGuid(), Hash, Agora, TimeSpan.Zero));
    }
}

public class GrantDomainTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Concessao_guarda_usuario_escopo_papel_e_quem_concedeu()
    {
        var usuario = Guid.NewGuid();
        var organizacao = Guid.NewGuid();
        var concedente = Guid.NewGuid();

        var concessao = UserOrganizationGrant.Create(Tenant, usuario, organizacao, Role.Manager, Agora, concedente);

        Assert.Equal(usuario, concessao.UserId);
        Assert.Equal(organizacao, concessao.OrganizationId);
        Assert.Equal(Role.Manager, concessao.Role);
        Assert.Equal(concedente, concessao.GrantedByUserId);
    }

    [Fact]
    public void Concessao_de_escopo_de_tenant_tem_organizacao_nula()
    {
        var concessao = UserOrganizationGrant.Create(Tenant, Guid.NewGuid(), null, Role.OrganizationAdmin, Agora, null);

        Assert.Null(concessao.OrganizationId);
        Assert.Null(concessao.GrantedByUserId);
    }

    [Fact]
    public void Conceder_de_novo_troca_o_papel_e_quem_concedeu()
    {
        var concessao = UserOrganizationGrant.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), Role.User, Agora, null);
        var novo = Guid.NewGuid();

        concessao.Reassign(Role.OrganizationAdmin, Agora.AddDays(1), novo);

        Assert.Equal(Role.OrganizationAdmin, concessao.Role);
        Assert.Equal(Agora.AddDays(1), concessao.GrantedAt);
        Assert.Equal(novo, concessao.GrantedByUserId);
    }

    [Fact]
    public void Papel_inexistente_e_ids_vazios_sao_recusados()
    {
        Assert.Throws<IdentityValidationException>(() => UserOrganizationGrant.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), (Role)99, Agora, null));
        Assert.Throws<IdentityValidationException>(() => UserOrganizationGrant.Create(Tenant, Guid.Empty, Guid.NewGuid(), Role.User, Agora, null));
        Assert.Throws<IdentityValidationException>(() => UserOrganizationGrant.Create(Tenant, Guid.NewGuid(), Guid.Empty, Role.User, Agora, null));
        var concessao = UserOrganizationGrant.Create(Tenant, Guid.NewGuid(), Guid.NewGuid(), Role.User, Agora, null);
        Assert.Throws<IdentityValidationException>(() => concessao.Reassign((Role)99, Agora, null));
    }
}
