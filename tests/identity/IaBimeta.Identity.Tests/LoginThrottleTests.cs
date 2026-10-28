using IaBimeta.Audit.Contracts;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Identity.Tests;

public class LoginThrottleDomainTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly LoginThrottlePolicy Politica = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    private static LoginThrottle Nova() => LoginThrottle.Create(Tenant, "ana@exemplo.com", Agora);

    [Fact]
    public void Abaixo_do_limite_so_conta_e_nao_bloqueia()
    {
        var contagem = Nova();

        for (var i = 1; i <= 4; i++)
        {
            Assert.False(contagem.RegisterFailure(Agora.AddSeconds(i), Politica));
        }

        Assert.Equal(4, contagem.FailedCount);
        Assert.False(contagem.IsLockedAt(Agora.AddSeconds(5)));
    }

    [Fact]
    public void A_falha_do_limite_bloqueia_pela_duracao_e_so_ela_devolve_verdadeiro()
    {
        var contagem = Nova();
        for (var i = 1; i <= 4; i++)
        {
            contagem.RegisterFailure(Agora.AddSeconds(i), Politica);
        }

        var bloqueou = contagem.RegisterFailure(Agora.AddSeconds(5), Politica);

        Assert.True(bloqueou);
        Assert.Equal(Agora.AddSeconds(5) + Politica.LockoutDuration, contagem.LockedUntil);
        Assert.True(contagem.IsLockedAt(Agora.AddMinutes(14)));
        Assert.False(contagem.IsLockedAt(Agora.AddSeconds(5) + Politica.LockoutDuration));
    }

    [Fact]
    public void Falha_durante_o_bloqueio_nao_o_prolonga()
    {
        var contagem = Nova();
        for (var i = 0; i < 5; i++)
        {
            contagem.RegisterFailure(Agora.AddSeconds(i), Politica);
        }

        var ate = contagem.LockedUntil;
        var bloqueouDeNovo = contagem.RegisterFailure(Agora.AddMinutes(5), Politica);

        Assert.False(bloqueouDeNovo);
        Assert.Equal(ate, contagem.LockedUntil);
    }

    [Fact]
    public void Bloqueio_vencido_zera_a_contagem_antes_de_contar_a_nova_falha()
    {
        var contagem = Nova();
        for (var i = 0; i < 5; i++)
        {
            contagem.RegisterFailure(Agora.AddSeconds(i), Politica);
        }

        var depoisDoBloqueio = Agora.AddMinutes(20);
        var bloqueou = contagem.RegisterFailure(depoisDoBloqueio, Politica);

        Assert.False(bloqueou);
        Assert.Equal(1, contagem.FailedCount);
        Assert.Null(contagem.LockedUntil);
        Assert.False(contagem.IsLockedAt(depoisDoBloqueio));
    }

    [Fact]
    public void Janela_vencida_sem_bloqueio_recomeca_a_contagem()
    {
        var contagem = Nova();
        for (var i = 0; i < 4; i++)
        {
            contagem.RegisterFailure(Agora.AddSeconds(i), Politica);
        }

        var bloqueou = contagem.RegisterFailure(Agora.AddMinutes(16), Politica);

        Assert.False(bloqueou);
        Assert.Equal(1, contagem.FailedCount);
        Assert.Equal(Agora.AddMinutes(16), contagem.WindowStartedAt);
    }

    [Fact]
    public void Depois_de_vencido_o_bloqueio_novas_falhas_bloqueiam_de_novo_pelo_mesmo_limite()
    {
        var contagem = Nova();
        for (var i = 0; i < 5; i++)
        {
            contagem.RegisterFailure(Agora.AddSeconds(i), Politica);
        }

        var depois = Agora.AddMinutes(20);
        var resultados = Enumerable.Range(0, 5).Select(i => contagem.RegisterFailure(depois.AddSeconds(i), Politica)).ToList();

        Assert.Equal(new[] { false, false, false, false, true }, resultados);
    }

    [Fact]
    public void Limite_de_uma_tentativa_bloqueia_na_primeira_falha()
    {
        var contagem = Nova();

        Assert.True(contagem.RegisterFailure(Agora, new LoginThrottlePolicy(1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))));
    }
}

/// <summary>
/// O bloqueio temporário de login no serviço. A contagem atômica com falhas simultâneas e o isolamento por tenant são
/// provados com PostgreSQL real em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class LoginThrottleServiceTests
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

        public User NovoUsuario(string email = "ana@exemplo.com", TenantInfo? tenant = null)
        {
            var usuario = User.Create((tenant ?? Tenant).TenantId, email, "Ana", Hasher.Hash(Senhas.Valida), Relogio.GetUtcNow());
            Dados.Usuarios.Add(usuario);
            return usuario;
        }

        public async Task ErrarAsync(int vezes, string slug = "lab-a", string email = "ana@exemplo.com")
        {
            for (var i = 0; i < vezes; i++)
            {
                await Assert.ThrowsAsync<InvalidCredentialsException>(() => Autenticacao.LoginAsync(slug, email, Senhas.Outra));
            }
        }
    }

    [Fact]
    public void Os_padroes_sao_cinco_falhas_em_quinze_minutos_com_bloqueio_de_quinze_minutos()
    {
        var opcoes = new IdentityOptions();

        Assert.Equal(5, opcoes.MaxFailedLogins);
        Assert.Equal(TimeSpan.FromMinutes(15), opcoes.FailedLoginWindow);
        Assert.Equal(TimeSpan.FromMinutes(15), opcoes.LockoutDuration);
    }

    [Fact]
    public async Task Cinco_senhas_erradas_bloqueiam_e_a_senha_certa_tambem_e_recusada_durante_o_bloqueio()
    {
        var c = new Cenario();
        c.NovoUsuario();
        await c.ErrarAsync(5);

        var bloqueio = await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));

        Assert.Equal(c.Opcoes.LockoutDuration, bloqueio.RetryAfter);
        Assert.Empty(c.Dados.Sessoes);
    }

    [Fact]
    public async Task Durante_o_bloqueio_a_senha_nem_e_verificada_e_nada_novo_entra_na_trilha()
    {
        var c = new Cenario();
        c.NovoUsuario();
        await c.ErrarAsync(5);
        var eventosAntes = c.Auditoria.Eventos.Count;
        var contagemAntes = c.Dados.Contagens.Single().Value.FailedCount;

        for (var i = 0; i < 10; i++)
        {
            await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Outra));
        }

        Assert.Equal(eventosAntes, c.Auditoria.Eventos.Count);
        Assert.Equal(contagemAntes, c.Dados.Contagens.Single().Value.FailedCount);
    }

    [Fact]
    public async Task O_bloqueio_entra_na_trilha_uma_unica_vez()
    {
        var c = new Cenario();
        var usuario = c.NovoUsuario();
        await c.ErrarAsync(5);

        var bloqueios = c.Auditoria.Eventos.Where(e => e.Entrada.Operation == "identity.login.lockout").ToList();

        var bloqueio = Assert.Single(bloqueios);
        Assert.Equal(AuditOutcome.Denied, bloqueio.Entrada.Outcome);
        Assert.Equal(usuario.Id, bloqueio.Ator);
        Assert.Equal(5, c.Auditoria.Eventos.Count(e => e.Entrada.Operation == "identity.login" && e.Entrada.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task E_mail_que_nao_existe_e_bloqueado_do_mesmo_jeito_sem_revelar_a_diferenca()
    {
        var c = new Cenario();
        c.NovoUsuario();

        await c.ErrarAsync(5, email: "ninguem@exemplo.com");
        var inexistente = await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ninguem@exemplo.com", Senhas.Valida));

        await c.ErrarAsync(5, email: "ana@exemplo.com");
        var existente = await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));

        Assert.Equal(existente.Message, inexistente.Message);
        Assert.Equal(existente.RetryAfter, inexistente.RetryAfter);
    }

    [Fact]
    public async Task O_bloqueio_vale_so_para_o_e_mail_e_o_tenant_em_que_aconteceu()
    {
        var c = new Cenario();
        c.NovoUsuario();
        var outroTenant = c.Tenants.Adicionar("lab-b");
        c.NovoUsuario("ana@exemplo.com", outroTenant);
        c.NovoUsuario("bia@exemplo.com");

        await c.ErrarAsync(5);

        await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));
        Assert.NotEmpty((await c.Autenticacao.LoginAsync("lab-b", "ana@exemplo.com", Senhas.Valida)).Token);
        Assert.NotEmpty((await c.Autenticacao.LoginAsync("lab-a", "bia@exemplo.com", Senhas.Valida)).Token);
    }

    [Fact]
    public async Task Passado_o_bloqueio_a_senha_certa_entra_e_zera_a_contagem()
    {
        var c = new Cenario();
        c.NovoUsuario();
        await c.ErrarAsync(5);

        c.Relogio.Avancar(c.Opcoes.LockoutDuration);
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);

        Assert.NotEmpty(login.Token);
        Assert.Empty(c.Dados.Contagens);
    }

    [Fact]
    public async Task Um_instante_antes_do_fim_do_bloqueio_ainda_esta_bloqueado()
    {
        var c = new Cenario();
        c.NovoUsuario();
        await c.ErrarAsync(5);

        c.Relogio.Avancar(c.Opcoes.LockoutDuration - TimeSpan.FromSeconds(1));

        var bloqueio = await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));
        Assert.Equal(TimeSpan.FromSeconds(1), bloqueio.RetryAfter);
    }

    [Fact]
    public async Task Login_bem_sucedido_zera_as_falhas_anteriores()
    {
        var c = new Cenario();
        c.NovoUsuario();

        await c.ErrarAsync(4);
        await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida);
        await c.ErrarAsync(4);

        // Quatro e quatro não chegam a cinco: o sucesso zerou a contagem.
        Assert.NotEmpty((await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida)).Token);
    }

    [Fact]
    public async Task Falhas_espalhadas_por_mais_que_a_janela_nao_bloqueiam()
    {
        var c = new Cenario();
        c.NovoUsuario();

        await c.ErrarAsync(4);
        c.Relogio.Avancar(c.Opcoes.FailedLoginWindow);
        await c.ErrarAsync(4);

        Assert.NotEmpty((await c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida)).Token);
    }

    [Fact]
    public async Task Senha_vazia_ou_acima_do_limite_tambem_conta_como_falha()
    {
        var c = new Cenario();
        c.NovoUsuario();

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", ""));
        }

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", new string('x', PasswordPolicy.MaxLength + 1)));
        }

        await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));
    }

    [Fact]
    public async Task Tenant_inexistente_ou_suspenso_e_e_mail_malformado_nao_criam_contagem()
    {
        var c = new Cenario();
        c.Tenants.Adicionar("lab-suspenso", TenantStatus.Suspended);

        for (var i = 0; i < 8; i++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("nao-existe", "ana@exemplo.com", Senhas.Outra));
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-suspenso", "ana@exemplo.com", Senhas.Outra));
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "nao-e-email", Senhas.Outra));
        }

        Assert.Empty(c.Dados.Contagens);
    }

    [Fact]
    public async Task O_e_mail_e_normalizado_na_contagem_entao_variar_a_caixa_nao_foge_do_bloqueio()
    {
        var c = new Cenario();
        c.NovoUsuario();

        foreach (var variacao in new[] { "ana@exemplo.com", "ANA@exemplo.com", " Ana@Exemplo.com ", "aNa@EXEMPLO.com", "ana@exemplo.COM" })
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", variacao, Senhas.Outra));
        }

        await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ANA@exemplo.com", Senhas.Valida));
    }

    [Fact]
    public async Task Os_limites_vem_da_configuracao()
    {
        var c = new Cenario();
        c.Opcoes.MaxFailedLogins = 2;
        c.Opcoes.LockoutDuration = TimeSpan.FromMinutes(1);
        c.NovoUsuario();

        await c.ErrarAsync(2);

        var bloqueio = await Assert.ThrowsAsync<LoginThrottledException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));
        Assert.Equal(TimeSpan.FromMinutes(1), bloqueio.RetryAfter);
    }

    // Falha de auditoria nunca vira sucesso silencioso

    [Fact]
    public async Task Se_a_auditoria_falha_o_login_bem_sucedido_falha_e_o_token_nao_e_devolvido()
    {
        var c = new Cenario();
        c.NovoUsuario();
        var servico = new AuthenticationService(c.Tenants, c.Dados, c.Dados, c.Hasher, c.Opcoes, c.Relogio, new AuditoriaQueFalha(), c.Holder);

        var falha = await Assert.ThrowsAsync<InvalidOperationException>(() => servico.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));

        Assert.Equal(AuditoriaQueFalha.Mensagem, falha.Message);
    }

    [Fact]
    public async Task Se_a_auditoria_falha_a_contagem_de_falhas_de_login_continua_valendo()
    {
        var c = new Cenario();
        c.NovoUsuario();
        var servico = new AuthenticationService(c.Tenants, c.Dados, c.Dados, c.Hasher, c.Opcoes, c.Relogio, new AuditoriaQueFalha(), c.Holder);

        // Cada tentativa falha por causa da auditoria, mas a contagem (a proteção) foi feita antes e não se perde.
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => servico.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Outra));
        }

        await Assert.ThrowsAsync<LoginThrottledException>(() => servico.LoginAsync("lab-a", "ana@exemplo.com", Senhas.Valida));
    }
}
