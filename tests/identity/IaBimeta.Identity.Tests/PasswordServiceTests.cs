using IaBimeta.Audit.Contracts;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Identity.Tests;

/// <summary>
/// Regras da troca de senha, da redefinição por token de uso único e da revogação de sessões (mesmo mecanismo de autenticação: sessões revogáveis, PBKDF2 e bloqueio por tentativas). O que depende de persistência real
/// (atomicidade do uso único, isolamento por tenant, HTTP) é provado com PostgreSQL em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class PasswordServiceTests
{
    private sealed class Cenario
    {
        public RelogioManual Relogio { get; } = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        public IdentidadeEmMemoria Dados { get; } = new();
        public TenantsEmMemoria Tenants { get; } = new();
        public OrganizacoesEmMemoria Organizacoes { get; } = new();
        public AuditoriaEmMemoria Auditoria { get; } = new();
        public IdentityOptions Opcoes { get; } = Senhas.Rapidas();
        public RequestContextHolder Holder { get; } = new();
        public PasswordHasher Hasher { get; }
        public TenantInfo Tenant { get; }
        public Guid Organizacao { get; }

        public Cenario()
        {
            Hasher = new PasswordHasher(Opcoes);
            Tenant = Tenants.Adicionar("lab-a");
            Organizacao = Organizacoes.Adicionar();
        }

        public PasswordService Servico => AdminFabrica.Senha(Dados, Tenants, Organizacoes, Hasher, Opcoes, Holder, Relogio, Auditoria);

        public User NovoUsuario(string email = "ana@exemplo.com", string senha = Senhas.Valida, Role papel = Role.User, bool tenantInteiro = false, TenantInfo? tenant = null)
        {
            var dono = tenant ?? Tenant;
            var usuario = User.Create(dono.TenantId, email, "Ana", Hasher.Hash(senha), Relogio.GetUtcNow());
            Dados.Usuarios.Add(usuario);
            Dados.Concessoes.Add(UserOrganizationGrant.Create(dono.TenantId, usuario.Id, tenantInteiro ? null : Organizacao, papel, Relogio.GetUtcNow(), null));
            return usuario;
        }

        public Session NovaSessao(User usuario)
        {
            var sessao = Session.Create(usuario.TenantId, usuario.Id, SessionTokens.Generate().Hash, Relogio.GetUtcNow(), TimeSpan.FromHours(8));
            Dados.Sessoes.Add(sessao);
            return sessao;
        }

        /// <summary>O contexto de uma requisição autenticada (como o host o cria a partir do token da sessão).</summary>
        public void ComoSessao(User usuario, Session sessao)
        {
            var permissoes = Dados.Concessoes.Where(g => g.UserId == usuario.Id).SelectMany(g => RolePermissions.Expand(g.Role, g.OrganizationId)).Distinct().ToArray();
            Holder.Set(new RequestContext(usuario.TenantId, "session", new ActorPrincipal(usuario.Id, sessao.Id, permissoes)));
        }

        public void ComoSistema() => Holder.Set(new RequestContext(Tenant.TenantId, "provisioning-cli"));
    }

    // Trocar a própria senha

    [Fact]
    public async Task Trocar_a_senha_com_a_atual_certa_grava_o_hash_novo_revoga_as_outras_sessoes_e_mantem_a_atual()
    {
        var c = new Cenario();
        var ana = c.NovoUsuario();
        var atual = c.NovaSessao(ana);
        var outra = c.NovaSessao(ana);
        var deOutraPessoa = c.NovaSessao(c.NovoUsuario("bia@exemplo.com"));
        c.ComoSessao(ana, atual);

        await c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, Senhas.Outra);

        Assert.Equal(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Valida, ana.PasswordHash));
        Assert.NotEqual(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Outra, ana.PasswordHash));
        Assert.True(atual.IsActiveAt(c.Relogio.GetUtcNow())); // a sessão em uso continua
        Assert.False(outra.IsActiveAt(c.Relogio.GetUtcNow())); // as outras sessões do usuário caem
        Assert.True(deOutraPessoa.IsActiveAt(c.Relogio.GetUtcNow())); // e só as dele
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.password.change" && e.Outcome == AuditOutcome.Success);
        Assert.All(c.Auditoria.Entradas, e => Assert.DoesNotContain(Senhas.Outra, e.ResourceId ?? string.Empty));
    }

    [Fact]
    public async Task Senha_atual_errada_e_recusada_conta_no_bloqueio_do_login_e_nao_troca_nada()
    {
        var c = new Cenario();
        var ana = c.NovoUsuario();
        var sessao = c.NovaSessao(ana);
        c.ComoSessao(ana, sessao);
        var hashAntes = ana.PasswordHash;

        for (var i = 0; i < c.Opcoes.MaxFailedLogins; i++)
        {
            await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.ChangeOwnPasswordAsync("senha-errada-123", Senhas.Outra));
        }

        // Depois de errar demais, até a senha certa é recusada por um tempo (o mesmo bloqueio do login), sem sequer verificá-la.
        await Assert.ThrowsAsync<LoginThrottledException>(() => c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, Senhas.Outra));
        Assert.Equal(hashAntes, ana.PasswordHash);
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.password.change" && e.Outcome == AuditOutcome.Denied);

        c.Relogio.Avancar(c.Opcoes.LockoutDuration + TimeSpan.FromSeconds(1));
        await c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, Senhas.Outra); // passado o bloqueio, a senha certa funciona
    }

    [Fact]
    public async Task Nova_senha_fora_da_politica_ou_igual_a_atual_e_recusada_sem_contar_como_tentativa()
    {
        var c = new Cenario();
        var ana = c.NovoUsuario();
        c.ComoSessao(ana, c.NovaSessao(ana));

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, "curta"));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, null));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, Senhas.Valida));

        Assert.Empty(c.Dados.Contagens);
        Assert.NotEqual(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Valida, ana.PasswordHash));
    }

    [Fact]
    public async Task Sem_usuario_autenticado_nao_ha_troca()
    {
        var c = new Cenario();
        c.NovoUsuario();
        c.ComoSistema();

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, Senhas.Outra));
    }

    // Emitir o token

    [Fact]
    public async Task O_administrador_emite_o_token_que_vale_pouco_tempo_e_so_o_hash_fica_guardado()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        c.ComoSessao(admin, c.NovaSessao(admin));

        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);

        Assert.StartsWith(ResetTokens.Prefix, emitido.Token);
        Assert.Equal(c.Relogio.GetUtcNow() + c.Opcoes.PasswordResetLifetime, emitido.ExpiresAt);
        var guardado = Assert.Single(c.Dados.TokensDeRedefinicao);
        Assert.Equal(admin.Id, guardado.IssuedByUserId);
        Assert.True(ResetTokens.TryHash(emitido.Token, out var hash));
        Assert.Equal(hash, guardado.TokenHash); // só o hash; o token em claro não está em lugar nenhum do armazém
        var entrada = Assert.Single(c.Auditoria.Entradas, e => e.Operation == "identity.password.reset.issue" && e.Outcome == AuditOutcome.Success);
        Assert.DoesNotContain(emitido.Token, entrada.ResourceId ?? string.Empty);
    }

    [Fact]
    public async Task Emitir_outro_token_revoga_o_anterior_ainda_nao_usado()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        c.ComoSessao(admin, c.NovaSessao(admin));
        var primeiro = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);
        var segundo = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", primeiro.Token, Senhas.Outra));
        await c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", segundo.Token, Senhas.Outra);
    }

    [Theory]
    [InlineData(Role.User)]
    [InlineData(Role.Manager)]
    public async Task Papeis_sem_gestao_de_usuarios_nao_emitem_token_e_a_negacao_e_auditada(Role papel)
    {
        var c = new Cenario();
        var quem = c.NovoUsuario("quem@exemplo.com", papel: papel);
        var ana = c.NovoUsuario();
        c.ComoSessao(quem, c.NovaSessao(quem));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.IssueResetAsync(c.Organizacao, ana.Id));

        Assert.Empty(c.Dados.TokensDeRedefinicao);
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.password.reset.issue" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Quem_administra_so_a_organizacao_nao_redefine_a_senha_de_quem_administra_o_tenant_inteiro()
    {
        var c = new Cenario();
        var adminDaOrganizacao = c.NovoUsuario("org@exemplo.com", papel: Role.OrganizationAdmin);
        var adminDoTenant = c.NovoUsuario("tenant@exemplo.com", papel: Role.OrganizationAdmin, tenantInteiro: true);
        c.ComoSessao(adminDaOrganizacao, c.NovaSessao(adminDaOrganizacao));

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.IssueResetAsync(c.Organizacao, adminDoTenant.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.RevokeUserSessionsAsync(c.Organizacao, adminDoTenant.Id));
        Assert.Empty(c.Dados.TokensDeRedefinicao);

        // O administrador do tenant inteiro pode.
        c.ComoSessao(adminDoTenant, c.NovaSessao(adminDoTenant));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, adminDaOrganizacao.Id);
        Assert.StartsWith(ResetTokens.Prefix, emitido.Token);
    }

    [Fact]
    public async Task Usuario_fora_da_organizacao_ou_organizacao_alheia_respondem_como_inexistentes()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var semAcesso = User.Create(c.Tenant.TenantId, "fora@exemplo.com", "Fora", c.Hasher.Hash(Senhas.Valida), c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(semAcesso);
        c.ComoSessao(admin, c.NovaSessao(admin));

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.IssueResetAsync(c.Organizacao, semAcesso.Id));
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.IssueResetAsync(Guid.NewGuid(), admin.Id));
    }

    [Fact]
    public async Task O_operador_emite_pelo_e_mail_com_contexto_de_sistema_e_usuario_autenticado_nao_pode()
    {
        var c = new Cenario();
        var ana = c.NovoUsuario();
        c.ComoSessao(ana, c.NovaSessao(ana));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Servico.IssueResetForOperatorAsync("ana@exemplo.com"));

        c.ComoSistema();
        var emitido = await c.Servico.IssueResetForOperatorAsync(" ANA@exemplo.com ");

        Assert.Null(Assert.Single(c.Dados.TokensDeRedefinicao).IssuedByUserId);
        await c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, Senhas.Outra);
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Servico.IssueResetForOperatorAsync("ninguem@exemplo.com"));
    }

    // Usar o token

    [Fact]
    public async Task Redefinir_troca_a_senha_revoga_todas_as_sessoes_e_o_token_nao_vale_de_novo()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        var s1 = c.NovaSessao(ana);
        var s2 = c.NovaSessao(ana);
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);
        c.Holder.Set(new RequestContext(c.Tenant.TenantId, "anonimo")); // quem usa o token não está autenticado

        await c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, Senhas.Outra);

        Assert.NotEqual(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Outra, ana.PasswordHash));
        Assert.Equal(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Valida, ana.PasswordHash));
        Assert.False(s1.IsActiveAt(c.Relogio.GetUtcNow()));
        Assert.False(s2.IsActiveAt(c.Relogio.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, "Mais-Uma-Senha-789!"));
        Assert.Contains(c.Auditoria.Eventos, e => e.AntesDoContexto && e.Entrada.Operation == "identity.password.reset.redeem" && e.Entrada.Outcome == AuditOutcome.Success && e.Ator == ana.Id);
    }

    [Fact]
    public async Task Token_expirado_e_recusado()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);

        c.Relogio.Avancar(c.Opcoes.PasswordResetLifetime + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, Senhas.Outra));
        Assert.NotEqual(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Valida, ana.PasswordHash));
    }

    [Fact]
    public async Task Toda_falha_tem_a_mesma_resposta_exista_o_tenant_o_usuario_ou_nao()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        var suspenso = c.Tenants.Adicionar("lab-suspenso", TenantStatus.Suspended);
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);

        var tentativas = new (string? Tenant, string? Email, string? Token)[]
        {
            ("lab-a", "ana@exemplo.com", "ibr_" + new string('A', 43)), // token certo no formato, errado no valor
            ("lab-a", "ana@exemplo.com", "lixo"), // não é token nosso
            ("lab-a", "ninguem@exemplo.com", emitido.Token), // e-mail inexistente
            ("lab-a", "ana@exemplo.com", null),
            ("outro-tenant", "ana@exemplo.com", emitido.Token), // tenant inexistente
            ("lab-suspenso", "ana@exemplo.com", emitido.Token), // tenant suspenso
            (null, null, null),
            ("lab-a", "nao-e-email", emitido.Token),
        };

        foreach (var (tenant, email, token) in tentativas)
        {
            var erro = await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync(tenant, email, token, Senhas.Outra));
            Assert.Equal("O token de redefinição é inválido ou expirou.", erro.Message);
        }

        // Nenhuma dessas tentativas gastou o token verdadeiro.
        await c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, Senhas.Outra);
        Assert.NotNull(suspenso);
    }

    [Fact]
    public async Task Tentativas_demais_bloqueiam_por_e_mail_existindo_ou_nao_e_o_token_certo_nao_passa_durante_o_bloqueio()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);
        var errado = "ibr_" + new string('B', 43);

        foreach (var email in new[] { "ana@exemplo.com", "fantasma@exemplo.com" })
        {
            for (var i = 0; i < c.Opcoes.MaxFailedLogins; i++)
            {
                await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-a", email, errado, Senhas.Outra));
            }

            // A mesma resposta de bloqueio para e-mail que existe e para e-mail que não existe.
            await Assert.ThrowsAsync<LoginThrottledException>(() => c.Servico.RedeemResetAsync("lab-a", email, emitido.Token, Senhas.Outra));
        }

        Assert.NotEqual(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Valida, ana.PasswordHash));

        // O bloqueio da redefinição não bloqueia o login (chaves separadas), e passado o tempo o token certo ainda funciona.
        Assert.DoesNotContain(c.Dados.Contagens.Keys, k => k.Chave == "ana@exemplo.com");
        c.Relogio.Avancar(c.Opcoes.LockoutDuration + TimeSpan.FromSeconds(1));
        await c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, Senhas.Outra);
    }

    [Fact]
    public async Task Nova_senha_fora_da_politica_nao_consome_o_token()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, "curta"));

        await c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, Senhas.Outra);
    }

    [Fact]
    public async Task Token_de_um_tenant_nao_vale_em_outro_nem_para_outro_usuario()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        var outroTenant = c.Tenants.Adicionar("lab-b");
        c.NovoUsuario("ana@exemplo.com", senha: "Senha-Do-B-Longa-1!", tenant: outroTenant);
        var bia = c.NovoUsuario("bia@exemplo.com");
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-b", "ana@exemplo.com", emitido.Token, Senhas.Outra));
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-a", "bia@exemplo.com", emitido.Token, Senhas.Outra));
        Assert.NotEqual(PasswordVerification.Failed, c.Hasher.Verify(Senhas.Valida, bia.PasswordHash));
    }

    [Fact]
    public async Task Trocar_a_senha_invalida_os_tokens_de_redefinicao_pendentes()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        c.ComoSessao(admin, c.NovaSessao(admin));
        var emitido = await c.Servico.IssueResetAsync(c.Organizacao, ana.Id);
        c.ComoSessao(ana, c.NovaSessao(ana));

        await c.Servico.ChangeOwnPasswordAsync(Senhas.Valida, Senhas.Outra);

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Servico.RedeemResetAsync("lab-a", "ana@exemplo.com", emitido.Token, "Mais-Uma-Senha-789!"));
    }

    // Revogar sessões

    [Fact]
    public async Task O_administrador_revoga_todas_as_sessoes_de_um_usuario_da_organizacao()
    {
        var c = new Cenario();
        var admin = c.NovoUsuario("admin@exemplo.com", papel: Role.OrganizationAdmin);
        var ana = c.NovoUsuario();
        var s1 = c.NovaSessao(ana);
        var s2 = c.NovaSessao(ana);
        var deAdmin = c.NovaSessao(admin);
        c.ComoSessao(admin, deAdmin);

        var revogadas = await c.Servico.RevokeUserSessionsAsync(c.Organizacao, ana.Id);

        Assert.Equal(2, revogadas);
        Assert.False(s1.IsActiveAt(c.Relogio.GetUtcNow()));
        Assert.False(s2.IsActiveAt(c.Relogio.GetUtcNow()));
        Assert.True(deAdmin.IsActiveAt(c.Relogio.GetUtcNow()));
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.session.revoke-all" && e.Outcome == AuditOutcome.Success);
    }

    // Token

    [Fact]
    public void O_token_tem_prefixo_proprio_tamanho_fixo_e_so_o_formato_certo_gera_hash()
    {
        var (token, hash) = ResetTokens.Generate();

        Assert.StartsWith("ibr_", token);
        Assert.NotEqual(SessionTokens.Prefix, token[..4]);
        Assert.Equal(47, token.Length);
        Assert.True(ResetTokens.TryHash(token, out var igual));
        Assert.Equal(hash, igual);
        Assert.False(ResetTokens.TryHash(token + "x", out _));
        Assert.False(ResetTokens.TryHash(token.Replace("ibr_", "ibs_"), out _));
        Assert.False(ResetTokens.TryHash(null, out _));
        Assert.NotEqual(ResetTokens.Generate().Token, token);
    }
}
