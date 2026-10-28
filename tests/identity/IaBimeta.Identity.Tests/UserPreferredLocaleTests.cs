using IaBimeta.Audit.Contracts;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Tests;

/// <summary>
/// Idioma preferido do usuário (BCP 47): é dado do próprio usuário, definido por ele mesmo, e não é o idioma de nenhuma
/// conversa. O alvo é sempre o usuário do contexto, nunca um parâmetro.
/// </summary>
public class UserPreferredLocaleTests
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

        public User NovoUsuario(string email, Locale? idioma = null)
        {
            var usuario = User.Create(Tenant, email, "Pessoa", Hasher.Hash(Senhas.Valida), DateTimeOffset.UtcNow, idioma);
            Dados.Usuarios.Add(usuario);
            Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant, usuario.Id, null, Role.User, DateTimeOffset.UtcNow, null));
            return usuario;
        }

        public void Como(User usuario) =>
            Holder.Set(new RequestContext(Tenant, "session", new ActorPrincipal(usuario.Id, Guid.NewGuid(), RolePermissions.Expand(Role.User, null).ToArray())));
    }

    [Fact]
    public void O_usuario_nasce_sem_preferencia_ou_com_a_informada()
    {
        var c = new Cenario();

        Assert.Null(c.NovoUsuario("sem@exemplo.com").PreferredLocale);
        Assert.Equal(Locale.Parse("fr-FR"), c.NovoUsuario("com@exemplo.com", Locale.Parse("fr-fr")).PreferredLocale);
    }

    [Fact]
    public async Task O_usuario_define_o_proprio_idioma_na_forma_canonica_e_o_ve_em_seu_perfil()
    {
        var c = new Cenario();
        var bia = c.NovoUsuario("bia@exemplo.com");
        var outro = c.NovoUsuario("outro@exemplo.com", Locale.Parse("en-US"));
        c.Como(bia);

        var definido = await c.Servico.SetMyPreferredLocaleAsync("ES-es");
        var perfil = await c.Servico.GetCurrentAsync();

        Assert.Equal(Locale.Parse("es-ES"), definido);
        Assert.Equal(Locale.Parse("es-ES"), perfil.PreferredLocale);
        Assert.Equal(Locale.Parse("es-ES"), bia.PreferredLocale);
        Assert.Equal(Locale.Parse("en-US"), outro.PreferredLocale); // o de outra pessoa não foi tocado
        var evento = Assert.Single(c.Auditoria.Eventos);
        Assert.Equal(("identity.user.locale.set", AuditOutcome.Success), (evento.Entrada.Operation, evento.Entrada.Outcome));
    }

    [Fact]
    public async Task Limpar_a_preferencia_com_nulo_ou_vazio_e_repetir_a_mesma_nao_audita_de_novo()
    {
        var c = new Cenario();
        var bia = c.NovoUsuario("bia@exemplo.com", Locale.Parse("de-DE"));
        c.Como(bia);

        await c.Servico.SetMyPreferredLocaleAsync("de-DE"); // igual: nada muda
        Assert.Empty(c.Auditoria.Eventos);

        Assert.Null(await c.Servico.SetMyPreferredLocaleAsync(null));
        Assert.Null(bia.PreferredLocale);
        Assert.Null(await c.Servico.SetMyPreferredLocaleAsync("  "));
        Assert.Single(c.Auditoria.Eventos);
    }

    [Theory]
    [InlineData("pt_BR")]
    [InlineData("portugues")]
    [InlineData("xx-")]
    public async Task Idioma_que_nao_e_BCP_47_e_recusado_e_o_anterior_continua(string entrada)
    {
        var c = new Cenario();
        var bia = c.NovoUsuario("bia@exemplo.com", Locale.Parse("it-IT"));
        c.Como(bia);

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.SetMyPreferredLocaleAsync(entrada));

        Assert.Equal(Locale.Parse("it-IT"), bia.PreferredLocale);
    }

    [Fact]
    public async Task Sem_usuario_autenticado_nao_ha_preferencia_para_definir()
    {
        var c = new Cenario();
        c.NovoUsuario("bia@exemplo.com");

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.Servico.SetMyPreferredLocaleAsync("en-US"));

        c.Holder.Set(new RequestContext(Tenant, "development-header")); // contexto de sistema: sem usuário
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.Servico.SetMyPreferredLocaleAsync("en-US"));
    }

    [Fact]
    public async Task Usuario_da_sessao_que_ja_nao_existe_responde_como_inexistente()
    {
        var c = new Cenario();
        c.Holder.Set(new RequestContext(Tenant, "session", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<PermissionGrant>())));

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.SetMyPreferredLocaleAsync("en-US"));
    }

    [Fact]
    public async Task O_administrador_cria_o_usuario_ja_com_o_idioma_preferido_e_idioma_invalido_recusa_antes_de_gravar()
    {
        var c = new Cenario();
        var org = c.Organizacoes.Adicionar();
        c.Holder.Set(new RequestContext(Tenant, "session", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), RolePermissions.Expand(Role.OrganizationAdmin, org).ToArray())));

        var criado = await c.Servico.CreateAsync(org, "nova@exemplo.com", "Nova", Senhas.Valida, Role.User, "pt-pt");
        var semIdioma = await c.Servico.CreateAsync(org, "sem@exemplo.com", "Sem", Senhas.Valida, Role.User);

        Assert.Equal(Locale.Parse("pt-PT"), criado.PreferredLocale);
        Assert.Null(semIdioma.PreferredLocale);
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.CreateAsync(org, "ruim@exemplo.com", "Ruim", Senhas.Valida, Role.User, "pt_BR"));
        Assert.DoesNotContain(c.Dados.Usuarios, u => u.Email == "ruim@exemplo.com");
    }
}
