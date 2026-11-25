using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Identity.Tests;

/// <summary>
/// Etapa 01: status do usuário, vagas, habilitação, primeiro acesso, perfis, permissões individuais (precedência entre escopos), elevação de privilégio e último administrador. As regras são as do serviço; a trava, a transação e a
/// concorrência são provadas com PostgreSQL real em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
public class UserAdminTests
{
    private sealed class Cenario
    {
        public RelogioManual Relogio { get; } = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        public IdentidadeEmMemoria Dados { get; } = new();
        public TenantsEmMemoria Tenants { get; } = new();
        public OrganizacoesEmMemoria Organizacoes { get; } = new();
        public AuditoriaEmMemoria Auditoria { get; } = new();
        public IdentityOptions Opcoes { get; } = Senhas.Rapidas();
        public RequestContextHolder Holder { get; } = new();
        public PasswordHasher Hasher { get; }
        public TenantInfo Tenant { get; private set; }
        public Guid OrgA { get; }
        public Guid OrgB { get; }

        public Cenario(int? limite = null, bool cadastroHabilitado = true, bool explicita = false)
        {
            Hasher = new PasswordHasher(Opcoes);
            Tenant = Tenants.Adicionar(new TenantId(Guid.NewGuid()), "lab-a");
            if (limite is not null || !cadastroHabilitado || explicita)
            {
                Dados.DefinirPolitica(Tenant.TenantId, limite, cadastroHabilitado);
            }
            OrgA = Organizacoes.Adicionar();
            OrgB = Organizacoes.Adicionar();
        }

        public UserAdminService Admin => AdminFabrica.Admin(Dados, Tenants, Organizacoes, Hasher, Opcoes, Holder, Relogio, Auditoria);

        public PasswordService Senha => AdminFabrica.Senha(Dados, Tenants, Organizacoes, Hasher, Opcoes, Holder, Relogio, Auditoria);

        public AuthenticationService Autenticacao => new(Tenants, Dados, Dados, Hasher, Opcoes, Relogio, Auditoria, Holder);

        public SessionAuthenticator Autenticador => new(Dados, new EmbeddedEmMemoria(), Tenants, Relogio);

        public void Politica(int? limite, bool habilitado) => Dados.DefinirPolitica(Tenant.TenantId, limite, habilitado);

        public User Existente(string email, Role papel, Guid? organizacao, UserStatus status = UserStatus.Active, string nome = "Pessoa")
        {
            var usuario = User.Create(Tenant.TenantId, email, nome, Hasher.Hash(Senhas.Valida), Relogio.GetUtcNow());
            if (status == UserStatus.Inactive)
            {
                usuario.Deactivate(Relogio.GetUtcNow());
            }

            Dados.Usuarios.Add(usuario);
            Dados.Concessoes.Add(UserOrganizationGrant.Create(Tenant.TenantId, usuario.Id, organizacao, papel, Relogio.GetUtcNow(), null));
            return usuario;
        }

        public User Administrador(string email = "admin@a.com") => Existente(email, Role.OrganizationAdmin, null);

        /// <summary>Entra como o usuário informado, com as permissões EFETIVAS dele (perfil + ajustes), como o SessionAuthenticator faria.</summary>
        public void Como(User usuario)
        {
            var permissoes = EffectivePermissions.Compute(Dados.Concessoes.Where(g => g.UserId == usuario.Id), Dados.Ajustes.Where(o => o.UserId == usuario.Id));
            Holder.Set(new RequestContext(Tenant.TenantId, "session", new ActorPrincipal(usuario.Id, Guid.NewGuid(), permissoes)));
        }

        public void ComoSistema() => Holder.Set(new RequestContext(Tenant.TenantId, "provision"));

        /// <summary>Quem usa o token não está autenticado (o primeiro acesso roda antes de existir usuário).</summary>
        public void Anonimo() => Holder.Set(new RequestContext(Tenant.TenantId, "anonimo"));

        public void Ajustar(User usuario, Guid? organizacao, Permission permissao, OverrideEffect efeito) =>
            Dados.Ajustes.Add(UserPermissionOverride.Create(Tenant.TenantId, usuario.Id, organizacao, permissao, efeito, Relogio.GetUtcNow(), null));

        public int VagasUsadas() => Dados.Usuarios.Count(u => u.TenantId == Tenant.TenantId)
            is var _ ? new AdminEmMemoria(Dados, Holder).GetSeatUsageAsync(Tenant.TenantId, Relogio.GetUtcNow(), default).Result.SeatsUsed : 0;
    }

    // Permissões efetivas

    [Fact]
    public void Sem_ajustes_as_permissoes_efetivas_sao_exatamente_as_do_perfil_como_antes()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var org = Guid.NewGuid();
        var concessoes = new[]
        {
            UserOrganizationGrant.Create(tenant, Guid.NewGuid(), org, Role.Manager, DateTimeOffset.UtcNow, null),
            UserOrganizationGrant.Create(tenant, Guid.NewGuid(), null, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null),
        };

        var esperadas = concessoes.SelectMany(c => RolePermissions.Expand(c.Role, c.OrganizationId)).ToHashSet();
        var efetivas = EffectivePermissions.Compute(concessoes, Array.Empty<UserPermissionOverride>());

        Assert.Equal(esperadas, efetivas.ToHashSet());
    }

    [Fact]
    public void Concessao_individual_soma_e_restricao_retira_e_a_restricao_vence()
    {
        var c = new Cenario();
        var gestor = c.Existente("g@a.com", Role.Manager, c.OrgA);

        // Concede o que o perfil não dá e retira o que o perfil dá.
        c.Ajustar(gestor, c.OrgA, Permission.UserManage, OverrideEffect.Grant);
        c.Ajustar(gestor, c.OrgA, Permission.ChannelRead, OverrideEffect.Deny);

        var efetivas = EffectivePermissions.Compute(c.Dados.Concessoes.Where(g => g.UserId == gestor.Id), c.Dados.Ajustes.Where(o => o.UserId == gestor.Id)).ToHashSet();

        Assert.Contains(new PermissionGrant(Permission.UserManage, c.OrgA), efetivas);
        Assert.DoesNotContain(new PermissionGrant(Permission.ChannelRead, c.OrgA), efetivas);
        Assert.Contains(new PermissionGrant(Permission.QueueRead, c.OrgA), efetivas);
    }

    [Fact]
    public void Ajuste_so_vale_no_escopo_em_que_foi_criado_e_nunca_vira_privilegio_global()
    {
        var c = new Cenario();
        var gestor = c.Existente("g@a.com", Role.Manager, c.OrgA);
        c.Ajustar(gestor, c.OrgA, Permission.QueueManage, OverrideEffect.Grant);

        var efetivas = EffectivePermissions.Compute(c.Dados.Concessoes.Where(g => g.UserId == gestor.Id), c.Dados.Ajustes.Where(o => o.UserId == gestor.Id)).ToHashSet();

        Assert.Contains(new PermissionGrant(Permission.QueueManage, c.OrgA), efetivas);
        Assert.DoesNotContain(new PermissionGrant(Permission.QueueManage, c.OrgB), efetivas);
        Assert.DoesNotContain(new PermissionGrant(Permission.QueueManage, null), efetivas);
    }

    [Fact]
    public void Concessao_em_escopo_sem_vinculo_e_permissao_de_tenant_em_organizacao_nao_produzem_nada()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var orgComVinculo = Guid.NewGuid();
        var orgSemVinculo = Guid.NewGuid();
        var usuario = Guid.NewGuid();
        var concessoes = new[] { UserOrganizationGrant.Create(tenant, usuario, orgComVinculo, Role.User, DateTimeOffset.UtcNow, null) };
        var ajustes = new[]
        {
            UserPermissionOverride.Create(tenant, usuario, orgSemVinculo, Permission.QueueRead, OverrideEffect.Grant, DateTimeOffset.UtcNow, null),
        };

        var efetivas = EffectivePermissions.Compute(concessoes, ajustes).ToHashSet();

        Assert.DoesNotContain(new PermissionGrant(Permission.QueueRead, orgSemVinculo), efetivas);
        Assert.Throws<IdentityValidationException>(() =>
            UserPermissionOverride.Create(tenant, usuario, orgComVinculo, Permission.AuditRead, OverrideEffect.Grant, DateTimeOffset.UtcNow, null));
        Assert.Throws<IdentityValidationException>(() =>
            UserPermissionOverride.Create(tenant, usuario, orgComVinculo, Permission.SystemOperation, OverrideEffect.Grant, DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void Restricao_do_tenant_retira_a_permissao_de_todos_os_escopos_e_a_de_organizacao_so_dela()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var usuario = Guid.NewGuid();
        var concessoes = new[]
        {
            UserOrganizationGrant.Create(tenant, usuario, a, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null),
            UserOrganizationGrant.Create(tenant, usuario, b, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null),
            UserOrganizationGrant.Create(tenant, usuario, null, Role.User, DateTimeOffset.UtcNow, null),
        };

        // Restrição só da organização A: retira só de A.
        var soA = EffectivePermissions.Compute(concessoes, new[] { UserPermissionOverride.Create(tenant, usuario, a, Permission.UserManage, OverrideEffect.Deny, DateTimeOffset.UtcNow, null) }).ToHashSet();
        Assert.DoesNotContain(new PermissionGrant(Permission.UserManage, a), soA);
        Assert.Contains(new PermissionGrant(Permission.UserManage, b), soA);

        // Restrição do TENANT: retira de A e de B (a concessão mais abrangente/outra concessão não a contorna).
        var doTenant = EffectivePermissions.Compute(concessoes, new[] { UserPermissionOverride.Create(tenant, usuario, null, Permission.UserManage, OverrideEffect.Deny, DateTimeOffset.UtcNow, null) }).ToHashSet();
        Assert.DoesNotContain(new PermissionGrant(Permission.UserManage, a), doTenant);
        Assert.DoesNotContain(new PermissionGrant(Permission.UserManage, b), doTenant);
    }

    [Fact]
    public void Concessao_de_tenant_nao_contorna_restricao_de_organizacao_a_permissao_do_tenant_e_descartada_por_inteiro()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var a = Guid.NewGuid();
        var usuario = Guid.NewGuid();
        var concessoes = new[]
        {
            UserOrganizationGrant.Create(tenant, usuario, null, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null),
            UserOrganizationGrant.Create(tenant, usuario, a, Role.Manager, DateTimeOffset.UtcNow, null),
        };
        var restricao = UserPermissionOverride.Create(tenant, usuario, a, Permission.ConversationManage, OverrideEffect.Deny, DateTimeOffset.UtcNow, null);

        var efetivas = EffectivePermissions.Compute(concessoes, new[] { restricao }).ToHashSet();

        // Falha fechada: nem a concessão de tenant nem a da organização A dão ConversationManage (a combinação é recusada na escrita; aqui se prova que, se aparecer, não sobra acesso além do declarado).
        Assert.DoesNotContain(new PermissionGrant(Permission.ConversationManage, a), efetivas);
        Assert.DoesNotContain(new PermissionGrant(Permission.ConversationManage, null), efetivas);
        // O resto da concessão de tenant continua.
        Assert.Contains(new PermissionGrant(Permission.UserManage, null), efetivas);
    }

    [Fact]
    public void Avaliacao_do_AccessPolicy_respeita_a_restricao_por_organizacao_sem_lista_negativa()
    {
        var c = new Cenario();
        var admin = c.Existente("adm@a.com", Role.OrganizationAdmin, c.OrgA);
        c.Existente("adm@a.com.b", Role.OrganizationAdmin, c.OrgB);
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, admin.Id, c.OrgB, Role.OrganizationAdmin, DateTimeOffset.UtcNow, null));
        c.Ajustar(admin, c.OrgA, Permission.UserManage, OverrideEffect.Deny);

        c.Como(admin);
        var contexto = c.Holder.GetRequired();

        Assert.False(contexto.IsAllowed(Permission.UserManage, c.OrgA));
        Assert.True(contexto.IsAllowed(Permission.UserManage, c.OrgB));
    }

    // Autenticação e status

    [Fact]
    public async Task Usuario_desativado_nao_entra_e_a_sessao_existente_cai_na_proxima_requisicao()
    {
        var c = new Cenario();
        var admin = c.Administrador();
        var alvo = c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        var login = await c.Autenticacao.LoginAsync("lab-a", "ana@a.com", Senhas.Valida);
        Assert.NotNull(await c.Autenticador.AuthenticateAsync(login.Token));

        c.Como(admin);
        await c.Admin.DeactivateAsync(alvo.Id);

        Assert.Equal(UserStatus.Inactive, alvo.Status);
        Assert.Null(await c.Autenticador.AuthenticateAsync(login.Token));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "ana@a.com", Senhas.Valida));
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.user.deactivate" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Desativar_preserva_concessoes_e_historico_e_libera_a_vaga()
    {
        var c = new Cenario(limite: 2);
        var admin = c.Administrador();
        var alvo = c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        c.Como(admin);
        Assert.Equal(2, c.VagasUsadas());

        await c.Admin.DeactivateAsync(alvo.Id);

        Assert.Equal(1, c.VagasUsadas());
        Assert.Single(c.Dados.Concessoes, g => g.UserId == alvo.Id);
        Assert.Contains(c.Dados.Usuarios, u => u.Id == alvo.Id);
    }

    // Admissão, primeiro acesso, vagas e habilitação

    [Fact]
    public async Task Admissao_cria_usuario_pendente_com_credencial_inutilizavel_e_reserva_uma_vaga()
    {
        var c = new Cenario(limite: 5);
        var admin = c.Administrador();
        c.Como(admin);

        var admitido = await c.Admin.AdmitAsync(c.OrgA, "Nova@A.com", "Nova Pessoa", Role.Attendant, null);

        var usuario = Assert.Single(c.Dados.Usuarios, u => u.Email == "nova@a.com");
        Assert.Equal(UserStatus.Pending, usuario.Status);
        Assert.StartsWith("!", usuario.PasswordHash);
        Assert.StartsWith(ResetTokens.Prefix, admitido.Token);
        Assert.Equal("Pending", admitido.User.Status);
        Assert.Equal(2, c.VagasUsadas());
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => c.Autenticacao.LoginAsync("lab-a", "nova@a.com", "qualquer-senha-12345"));
    }

    [Fact]
    public async Task Token_de_primeiro_acesso_vale_ate_24_horas_e_nunca_aparece_na_auditoria()
    {
        var c = new Cenario();
        c.Opcoes.FirstAccessLifetime = TimeSpan.FromDays(10);
        c.Como(c.Administrador());

        var admitido = await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);

        Assert.Equal(c.Relogio.GetUtcNow().AddHours(24), admitido.ExpiresAt);
        Assert.DoesNotContain(c.Auditoria.Eventos, e => e.Entrada.ToString()!.Contains(admitido.Token, StringComparison.Ordinal));
        Assert.DoesNotContain(c.Auditoria.Eventos, e => e.Entrada.ResourceId?.Contains(admitido.Token, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Primeiro_acesso_define_a_senha_ativa_o_usuario_e_o_token_so_vale_uma_vez()
    {
        var c = new Cenario();
        c.Como(c.Administrador());
        var admitido = await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.Attendant, null);
        c.Anonimo();

        await c.Senha.RedeemResetAsync("lab-a", "nova@a.com", admitido.Token, Senhas.Valida);

        var usuario = c.Dados.Usuarios.Single(u => u.Email == "nova@a.com");
        Assert.Equal(UserStatus.Active, usuario.Status);
        Assert.NotNull(await c.Autenticacao.LoginAsync("lab-a", "nova@a.com", Senhas.Valida));
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "nova@a.com", admitido.Token, "Outra-Senha-2026!x"));
    }

    [Fact]
    public async Task Token_expirado_nao_ativa_e_a_vaga_e_liberada_pela_expiracao()
    {
        var c = new Cenario(limite: 2);
        c.Como(c.Administrador());
        var admitido = await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);
        Assert.Equal(2, c.VagasUsadas());

        c.Relogio.Avancar(TimeSpan.FromHours(25));
        c.Anonimo();

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "nova@a.com", admitido.Token, Senhas.Valida));
        Assert.Equal(1, c.VagasUsadas());
        Assert.Equal(UserStatus.Pending, c.Dados.Usuarios.Single(u => u.Email == "nova@a.com").Status);
    }

    [Fact]
    public async Task Substituir_o_token_invalida_o_anterior_e_cancelar_o_token_libera_a_reserva()
    {
        var c = new Cenario(limite: 2);
        var admin = c.Administrador();
        c.Como(admin);
        var admitido = await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);
        var alvo = c.Dados.Usuarios.Single(u => u.Email == "nova@a.com");

        var novo = await c.Admin.IssueFirstAccessAsync(alvo.Id);
        c.Anonimo();
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "nova@a.com", admitido.Token, Senhas.Valida));

        c.Como(admin);
        Assert.Equal(2, c.VagasUsadas());
        await c.Admin.CancelTokenAsync(alvo.Id);
        Assert.Equal(1, c.VagasUsadas());
        c.Anonimo();
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "nova@a.com", novo.Token, Senhas.Valida));
    }

    [Fact]
    public async Task Limite_atingido_recusa_novas_admissoes_e_cancelar_libera_a_vaga()
    {
        var c = new Cenario(limite: 2);
        var admin = c.Administrador();
        c.Como(admin);
        await c.Admin.AdmitAsync(c.OrgA, "um@a.com", "Um", Role.User, null);

        var erro = await Assert.ThrowsAsync<UserLimitReachedException>(() => c.Admin.AdmitAsync(c.OrgA, "dois@a.com", "Dois", Role.User, null));
        Assert.Equal(2, erro.Limit);
        Assert.Equal(2, erro.Used);
        Assert.DoesNotContain(c.Dados.Usuarios, u => u.Email == "dois@a.com");

        await c.Admin.CancelAdmissionAsync(c.Dados.Usuarios.Single(u => u.Email == "um@a.com").Id);
        await c.Admin.AdmitAsync(c.OrgA, "dois@a.com", "Dois", Role.User, null);
        Assert.Equal(2, c.VagasUsadas());
    }

    [Fact]
    public async Task Cadastro_desabilitado_barra_ingresso_mas_nao_o_acesso_existente()
    {
        var c = new Cenario(limite: 10, cadastroHabilitado: false);
        var admin = c.Administrador();
        c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        c.Como(admin);

        await Assert.ThrowsAsync<RegistrationDisabledException>(() => c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null));
        await Assert.ThrowsAsync<RegistrationDisabledException>(() => c.Admin.CreateWithPasswordAsync(c.OrgA, "outra@a.com", "Outra", Senhas.Valida, Role.User, null));
        Assert.NotNull(await c.Autenticacao.LoginAsync("lab-a", "ana@a.com", Senhas.Valida));
    }

    [Fact]
    public async Task Vinculo_organizacional_de_usuario_ja_admitido_e_permitido_com_o_cadastro_desabilitado()
    {
        var c = new Cenario(limite: 10, cadastroHabilitado: false);
        var admin = c.Administrador();
        var ana = c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        c.Como(admin);

        await c.Admin.SetRoleAsync(c.OrgB, ana.Id, Role.Attendant);

        Assert.Equal(2, c.Dados.Concessoes.Count(g => g.UserId == ana.Id));
        Assert.Equal(2, c.VagasUsadas()); // a mesma pessoa em duas organizações consome UMA vaga
    }

    [Fact]
    public async Task Admissao_pendente_nao_e_aceita_com_o_cadastro_desabilitado_e_o_token_nao_e_consumido()
    {
        var c = new Cenario(limite: 10);
        c.Como(c.Administrador());
        var admitido = await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);
        c.Politica(10, habilitado: false);
        c.Anonimo();

        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "nova@a.com", admitido.Token, Senhas.Valida));

        c.Politica(10, habilitado: true);
        await c.Senha.RedeemResetAsync("lab-a", "nova@a.com", admitido.Token, Senhas.Valida);
        Assert.Equal(UserStatus.Active, c.Dados.Usuarios.Single(u => u.Email == "nova@a.com").Status);
    }

    [Fact]
    public async Task Admissao_cancelada_ou_expirada_nao_contorna_a_desabilitacao_e_readmitir_exige_vaga()
    {
        var c = new Cenario(limite: 2);
        var admin = c.Administrador();
        c.Como(admin);
        await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);
        var alvo = c.Dados.Usuarios.Single(u => u.Email == "nova@a.com");
        await c.Admin.CancelAdmissionAsync(alvo.Id);
        Assert.Equal(UserStatus.Cancelled, alvo.Status);

        c.Politica(2, habilitado: false);
        await Assert.ThrowsAsync<RegistrationDisabledException>(() => c.Admin.ReadmitAsync(alvo.Id));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Admin.ReactivateAsync(alvo.Id));

        c.Politica(2, habilitado: true);
        c.Existente("outra@a.com", Role.User, c.OrgA);
        await Assert.ThrowsAsync<UserLimitReachedException>(() => c.Admin.ReadmitAsync(alvo.Id));
    }

    [Fact]
    public async Task Novo_token_de_pendente_expirado_e_novo_ingresso_e_exige_habilitacao_e_vaga()
    {
        var c = new Cenario(limite: 5);
        var admin = c.Administrador();
        c.Como(admin);
        await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);
        var alvo = c.Dados.Usuarios.Single(u => u.Email == "nova@a.com");
        c.Relogio.Avancar(TimeSpan.FromHours(30));

        c.Politica(5, habilitado: false);
        await Assert.ThrowsAsync<RegistrationDisabledException>(() => c.Admin.IssueFirstAccessAsync(alvo.Id));

        c.Politica(5, habilitado: true);
        var novo = await c.Admin.IssueFirstAccessAsync(alvo.Id);
        Assert.StartsWith(ResetTokens.Prefix, novo.Token);
    }

    [Fact]
    public async Task Reativar_exige_vaga_mas_vale_com_o_cadastro_desabilitado()
    {
        var c = new Cenario(limite: 2);
        var admin = c.Administrador();
        var ana = c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        c.Como(admin);
        await c.Admin.DeactivateAsync(ana.Id);
        c.Politica(2, habilitado: false);

        await c.Admin.ReactivateAsync(ana.Id);
        Assert.Equal(UserStatus.Active, ana.Status);

        await c.Admin.DeactivateAsync(ana.Id);
        c.Existente("bia@a.com", Role.User, c.OrgA);
        await Assert.ThrowsAsync<UserLimitReachedException>(() => c.Admin.ReactivateAsync(ana.Id));
    }

    [Fact]
    public async Task Reducao_do_limite_nao_desativa_ninguem_indica_excesso_bloqueia_ingresso_e_permite_desativar()
    {
        var c = new Cenario(limite: 10);
        var admin = c.Administrador();
        var ana = c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        c.Existente("bia@a.com", Role.Attendant, c.OrgA);
        c.Como(admin);
        c.Politica(2, habilitado: true);

        var uso = await c.Admin.GetUsageAsync();
        Assert.True(uso.OverLimit);
        Assert.Equal(1, uso.Excess);
        Assert.Equal(0, uso.Available);
        Assert.All(c.Dados.Usuarios, u => Assert.Equal(UserStatus.Active, u.Status));
        await Assert.ThrowsAsync<UserLimitReachedException>(() => c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null));

        await c.Admin.DeactivateAsync(ana.Id);
        Assert.False((await c.Admin.GetUsageAsync()).OverLimit);
    }

    [Fact]
    public async Task Uso_informa_ativos_inativos_pendentes_reservadas_e_disponiveis()
    {
        var c = new Cenario(limite: 6);
        var admin = c.Administrador();
        c.Existente("ana@a.com", Role.Attendant, c.OrgA);
        c.Existente("inativa@a.com", Role.Attendant, c.OrgA, UserStatus.Inactive);
        c.Como(admin);
        await c.Admin.AdmitAsync(c.OrgA, "pend@a.com", "Pend", Role.User, null);

        var uso = await c.Admin.GetUsageAsync();

        Assert.Equal(6, uso.Limit);
        Assert.Equal(2, uso.Active);
        Assert.Equal(1, uso.Inactive);
        Assert.Equal(1, uso.Pending);
        Assert.Equal(3, uso.SeatsUsed);
        Assert.Equal(3, uso.Available);
        Assert.True(uso.RegistrationEnabled);
    }

    [Fact]
    public async Task Tenants_diferentes_contam_vagas_de_forma_independente()
    {
        var c = new Cenario(limite: 1);
        var outro = c.Tenants.Adicionar(new TenantId(Guid.NewGuid()), "lab-b");
        var admin = c.Administrador();
        var deOutro = User.Create(outro.TenantId, "x@b.com", "X", c.Hasher.Hash(Senhas.Valida), c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(deOutro);

        var loja = new AdminEmMemoria(c.Dados, c.Holder);
        var usoA = await loja.GetSeatUsageAsync(c.Tenant.TenantId, c.Relogio.GetUtcNow(), default);
        var usoB = await loja.GetSeatUsageAsync(outro.TenantId, c.Relogio.GetUtcNow(), default);

        Assert.Equal(1, usoA.SeatsUsed);
        Assert.Equal(1, usoB.SeatsUsed);
        _ = admin;
    }

    // Primeiro administrador

    [Fact]
    public async Task Primeiro_administrador_e_excecao_unica_com_cadastro_desabilitado_e_consome_vaga()
    {
        var c = new Cenario(limite: 1, cadastroHabilitado: false);
        c.ComoSistema();
        var usuarios = AdminFabrica.Usuarios(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria);

        var emitido = await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");

        Assert.StartsWith(ResetTokens.Prefix, emitido.Token);
        var dono = c.Dados.Usuarios.Single();
        Assert.Equal(UserStatus.Pending, dono.Status);
        Assert.Contains(c.Dados.Concessoes, g => g.UserId == dono.Id && g.OrganizationId is null && g.Role == Role.OrganizationAdmin);
        Assert.Equal(1, c.VagasUsadas());

        // Não se repete: nem como outro primeiro administrador.
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.ProvisionFirstAdministratorAsync("outro@a.com", "Outro"));
        // O primeiro administrador consegue concluir o primeiro acesso mesmo com o cadastro desabilitado.
        c.Anonimo();
        await c.Senha.RedeemResetAsync("lab-a", "dono@a.com", emitido.Token, Senhas.Valida);
        Assert.Equal(UserStatus.Active, dono.Status);
    }

    [Fact]
    public async Task Provisionar_o_primeiro_administrador_exige_contexto_de_sistema()
    {
        var c = new Cenario();
        c.Como(c.Administrador());
        var usuarios = AdminFabrica.Usuarios(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria);

        await Assert.ThrowsAsync<AccessDeniedException>(() => usuarios.ProvisionFirstAdministratorAsync("outro@a.com", "Outro"));
    }

    // Elevação de privilégio

    private static (User Supervisor, User Admin, User Atendente) Equipe(Cenario c)
    {
        var admin = c.Administrador();
        var supervisor = c.Existente("sup@a.com", Role.Manager, c.OrgA, nome: "Supervisor");
        var atendente = c.Existente("at@a.com", Role.Attendant, c.OrgA, nome: "Atendente");
        // O supervisor recebe, por delegação individual, só o cadastro de usuários.
        c.Ajustar(supervisor, c.OrgA, Permission.UserRead, OverrideEffect.Grant);
        c.Ajustar(supervisor, c.OrgA, Permission.UserManage, OverrideEffect.Grant);
        return (supervisor, admin, atendente);
    }

    [Fact]
    public async Task Supervisor_com_UserManage_cadastra_atendente_mas_nao_administradores_nem_supervisores()
    {
        var c = new Cenario(limite: 20);
        var (supervisor, _, _) = Equipe(c);
        c.Como(supervisor);

        await c.Admin.AdmitAsync(c.OrgA, "novo@a.com", "Novo", Role.Attendant, null);
        await c.Admin.AdmitAsync(c.OrgA, "novo2@a.com", "Novo 2", Role.User, null);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.AdmitAsync(c.OrgA, "adm@a.com", "Adm", Role.OrganizationAdmin, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.AdmitAsync(c.OrgA, "sup2@a.com", "Sup", Role.Manager, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.CreateWithPasswordAsync(c.OrgA, "adm2@a.com", "Adm", Senhas.Valida, Role.OrganizationAdmin, null));
    }

    [Fact]
    public async Task Supervisor_nao_promove_atendente_a_administrador_e_nao_personaliza_permissoes()
    {
        var c = new Cenario(limite: 20);
        var (supervisor, _, atendente) = Equipe(c);
        c.Como(supervisor);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetRoleAsync(c.OrgA, atendente.Id, Role.OrganizationAdmin));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetRoleAsync(c.OrgA, atendente.Id, Role.Manager));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetOverrideAsync(atendente.Id, c.OrgA, Permission.QueueRead, OverrideEffect.Grant));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.DeactivateAsync(atendente.Id)); // sem UserStatusManage
    }

    [Fact]
    public async Task Supervisor_nao_assume_altera_nem_compromete_a_conta_de_um_administrador()
    {
        var c = new Cenario(limite: 20);
        var (supervisor, admin, _) = Equipe(c);
        c.Como(supervisor);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.UpdateProfileAsync(admin.Id, "novo-email@a.com", "Adm"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Senha.IssueResetAsync(c.OrgA, admin.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Senha.RevokeUserSessionsAsync(c.OrgA, admin.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.IssueFirstAccessAsync(admin.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.CancelTokenAsync(admin.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetRoleAsync(c.OrgA, admin.Id, Role.User));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.RevokeRoleAsync(c.OrgA, admin.Id));
        Assert.Equal("admin@a.com", admin.Email);
        Assert.Contains(c.Auditoria.Entradas, e => e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Supervisor_gerencia_a_conta_de_um_atendente_dentro_do_que_tem()
    {
        var c = new Cenario(limite: 20);
        var (supervisor, _, atendente) = Equipe(c);
        c.Como(supervisor);

        await c.Admin.UpdateProfileAsync(atendente.Id, "novo@a.com", "Atendente Novo");
        var token = await c.Senha.IssueResetAsync(c.OrgA, atendente.Id);

        Assert.Equal("novo@a.com", atendente.Email);
        Assert.StartsWith(ResetTokens.Prefix, token.Token);
    }

    [Fact]
    public async Task Ninguem_se_administra_a_si_mesmo_autopromocao_e_autodesativacao_sao_recusadas()
    {
        var c = new Cenario(limite: 20);
        var admin = c.Administrador();
        c.Existente("outro@a.com", Role.OrganizationAdmin, null);
        c.Como(admin);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.DeactivateAsync(admin.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetRoleAsync(c.OrgA, admin.Id, Role.OrganizationAdmin));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetOverrideAsync(admin.Id, null, Permission.AuditRead, OverrideEffect.Deny));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Senha.IssueResetAsync(c.OrgA, admin.Id));
    }

    [Fact]
    public async Task Administrador_de_uma_organizacao_nao_alcanca_o_administrador_do_tenant_nem_usuarios_de_outra_organizacao()
    {
        var c = new Cenario(limite: 20);
        var tenantAdmin = c.Administrador();
        var adminA = c.Existente("adma@a.com", Role.OrganizationAdmin, c.OrgA);
        var deB = c.Existente("b@a.com", Role.Attendant, c.OrgB);
        c.Como(adminA);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.DeactivateAsync(tenantAdmin.Id));
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Admin.DeactivateAsync(deB.Id)); // outra organização: nem enxerga
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Admin.GetAsync(deB.Id));
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Admin.AdmitAsync(c.OrgB, "x@a.com", "X", Role.User, null)); // sem leitura da organização B
    }

    [Fact]
    public async Task Nao_se_adota_usuario_por_identificador_conhecido_sem_vinculo_visivel()
    {
        var c = new Cenario(limite: 20);
        c.Administrador();
        var adminA = c.Existente("adma@a.com", Role.OrganizationAdmin, c.OrgA);
        var orfao = User.Create(c.Tenant.TenantId, "orfao@a.com", "Orfão", c.Hasher.Hash(Senhas.Valida), c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(orfao);
        var deOutroTenant = User.Create(new TenantId(Guid.NewGuid()), "z@z.com", "Z", c.Hasher.Hash(Senhas.Valida), c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(deOutroTenant);
        c.Como(adminA);

        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Admin.SetRoleAsync(c.OrgA, orfao.Id, Role.User));
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Admin.SetRoleAsync(c.OrgA, deOutroTenant.Id, Role.User));
    }

    // Último administrador

    /// <summary>Um ator que tem as permissões de administrador do tenant mas NÃO é um usuário administrador contado (por exemplo, outra conta de gestão): serve para provar a proteção sem que o ator seja o alvo.</summary>
    private static void ComoAdministradorExterno(Cenario c) =>
        c.Holder.Set(new RequestContext(c.Tenant.TenantId, "session", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), RolePermissions.Expand(Role.OrganizationAdmin, null))));

    [Fact]
    public async Task Ultimo_administrador_nao_pode_ser_desativado()
    {
        var c = new Cenario(limite: 20);
        var unico = c.Administrador("unico@a.com");
        ComoAdministradorExterno(c);

        await Assert.ThrowsAsync<LastAdministratorException>(() => c.Admin.DeactivateAsync(unico.Id));
    }

    [Fact]
    public async Task Com_dois_administradores_desativar_um_e_permitido_e_o_outro_passa_a_ser_o_ultimo()
    {
        var c = new Cenario(limite: 20);
        var a = c.Administrador("a@a.com");
        var b = c.Administrador("b@a.com");
        ComoAdministradorExterno(c);

        await c.Admin.DeactivateAsync(a.Id);

        await Assert.ThrowsAsync<LastAdministratorException>(() => c.Admin.DeactivateAsync(b.Id));
    }

    [Theory]
    [InlineData(Permission.UserManage)]
    [InlineData(Permission.UserPermissionManage)]
    [InlineData(Permission.UserStatusManage)]
    public async Task Retirar_privilegio_essencial_do_ultimo_administrador_e_recusado_pelas_permissoes_efetivas(Permission essencial)
    {
        var c = new Cenario(limite: 20);
        var unico = c.Administrador("unico@a.com");
        ComoAdministradorExterno(c);

        await Assert.ThrowsAsync<LastAdministratorException>(() => c.Admin.SetOverrideAsync(unico.Id, null, essencial, OverrideEffect.Deny));
    }

    [Fact]
    public async Task Retirar_privilegio_nao_essencial_do_ultimo_administrador_e_permitido()
    {
        var c = new Cenario(limite: 20);
        var unico = c.Administrador("unico@a.com");
        ComoAdministradorExterno(c);

        await c.Admin.SetOverrideAsync(unico.Id, null, Permission.AuditRead, OverrideEffect.Deny);

        Assert.Single(c.Dados.Ajustes, o => o.UserId == unico.Id);
    }

    [Fact]
    public async Task Administrador_so_com_restricao_individual_nao_conta_como_administrador_valido()
    {
        var c = new Cenario(limite: 20);
        var a = c.Administrador("a@a.com");
        var b = c.Administrador("b@a.com");
        c.Ajustar(b, null, Permission.UserManage, OverrideEffect.Deny); // b já não é administrador essencial
        ComoAdministradorExterno(c);

        await Assert.ThrowsAsync<LastAdministratorException>(() => c.Admin.DeactivateAsync(a.Id));
    }

    [Fact]
    public async Task Pendente_cancelado_e_inativo_nao_contam_como_administrador_valido()
    {
        var c = new Cenario(limite: 20);
        var ativo = c.Administrador("ativo@a.com");
        var inativo = c.Administrador("inativo@a.com");
        inativo.Deactivate(c.Relogio.GetUtcNow());
        var pendente = User.CreatePending(c.Tenant.TenantId, "pend@a.com", "Pend", c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(pendente);
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, pendente.Id, null, Role.OrganizationAdmin, c.Relogio.GetUtcNow(), null));
        // Quem opera é o "ativo"; o único administrador utilizável é ele mesmo, que não age sobre si.
        c.Como(ativo);

        var candidatos = await new AdminEmMemoria(c.Dados, c.Holder).ListTenantScopeUsersAsync(c.Relogio.GetUtcNow(), default);
        Assert.Equal(1, candidatos.Count(d => UserAdminService.IsUsableAdministrator(d)));
    }


    // Ajustes individuais

    [Fact]
    public async Task Administrador_personaliza_e_restaura_o_padrao_do_perfil()
    {
        var c = new Cenario(limite: 20);
        var admin = c.Administrador();
        var gestor = c.Existente("g@a.com", Role.Manager, c.OrgA);
        c.Como(admin);

        await c.Admin.SetOverrideAsync(gestor.Id, c.OrgA, Permission.UserManage, OverrideEffect.Grant);
        await c.Admin.SetOverrideAsync(gestor.Id, c.OrgA, Permission.ChannelRead, OverrideEffect.Deny);

        var detalhe = await c.Admin.GetAsync(gestor.Id);
        Assert.True(detalhe.Customized);
        Assert.Contains(detalhe.Permissions, p => p.Permission == Permission.UserManage && !p.InProfile && p.Override == "Grant" && p.Effective);
        Assert.Contains(detalhe.Permissions, p => p.Permission == Permission.ChannelRead && p.InProfile && p.Override == "Deny" && !p.Effective);

        await c.Admin.ResetOverridesAsync(gestor.Id, scopeSpecified: false, null);

        var depois = await c.Admin.GetAsync(gestor.Id);
        Assert.False(depois.Customized);
        Assert.DoesNotContain(depois.Permissions, p => p.Override is not null);
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation.StartsWith("identity.permission.override", StringComparison.Ordinal) && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task So_se_concede_o_que_se_tem_e_restricao_de_organizacao_contra_permissao_de_tenant_e_recusada()
    {
        var c = new Cenario(limite: 20);
        var adminA = c.Existente("adma@a.com", Role.OrganizationAdmin, c.OrgA);
        var gestor = c.Existente("g@a.com", Role.Manager, c.OrgA);
        c.Como(adminA);

        // O administrador de A não tem permissões de tenant: não as concede.
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Admin.SetOverrideAsync(gestor.Id, c.OrgA, Permission.AuditRead, OverrideEffect.Grant));
        // Nem concede o que ele mesmo não tem (restrição pessoal dele).
        c.Ajustar(adminA, c.OrgA, Permission.ChannelManage, OverrideEffect.Deny);
        c.Como(adminA);
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.SetOverrideAsync(gestor.Id, c.OrgA, Permission.ChannelManage, OverrideEffect.Grant));

        // Restrição de organização contra o administrador do tenant: nem o alcança (cobertura), mas contra quem tem a permissão por vínculo de tenant é recusada.
        var tenantAdmin = c.Administrador();
        var tenantUser = c.Existente("tu@a.com", Role.User, null);
        c.Como(tenantAdmin);
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, tenantUser.Id, c.OrgA, Role.Manager, c.Relogio.GetUtcNow(), null));
        c.Dados.Ajustes.Add(UserPermissionOverride.Create(c.Tenant.TenantId, tenantUser.Id, null, Permission.OrganizationRead, OverrideEffect.Grant, c.Relogio.GetUtcNow(), null));
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Admin.SetOverrideAsync(tenantUser.Id, c.OrgA, Permission.OrganizationRead, OverrideEffect.Deny));
    }

    [Fact]
    public async Task Restricao_individual_vale_na_requisicao_seguinte_pelo_autenticador()
    {
        var c = new Cenario(limite: 20);
        var admin = c.Administrador();
        var gestor = c.Existente("g@a.com", Role.Manager, c.OrgA);
        var login = await c.Autenticacao.LoginAsync("lab-a", "g@a.com", Senhas.Valida);
        var antes = await c.Autenticador.AuthenticateAsync(login.Token);
        Assert.Contains(antes!.Permissions, p => p.Permission == Permission.ChannelRead);

        c.Como(admin);
        await c.Admin.SetOverrideAsync(gestor.Id, c.OrgA, Permission.ChannelRead, OverrideEffect.Deny);

        var depois = await c.Autenticador.AuthenticateAsync(login.Token);
        Assert.DoesNotContain(depois!.Permissions, p => p.Permission == Permission.ChannelRead);
        Assert.Contains(depois.Permissions, p => p.Permission == Permission.QueueRead);
    }

    // Listagem

    [Fact]
    public async Task Listagem_filtra_pagina_pesquisa_e_respeita_a_visibilidade_por_organizacao()
    {
        var c = new Cenario(limite: 50);
        var admin = c.Administrador();
        for (var i = 0; i < 25; i++)
        {
            c.Existente($"u{i:00}@a.com", Role.Attendant, c.OrgA, nome: $"Pessoa {i:00}");
        }

        c.Existente("so-b@a.com", Role.Attendant, c.OrgB, nome: "Só B");
        c.Como(admin);

        var pagina1 = await c.Admin.ListAsync(null, null, null, null, 1, 10);
        Assert.Equal(10, pagina1.Items.Count);
        Assert.Equal(27, pagina1.Total);

        var busca = await c.Admin.ListAsync("pessoa 07", null, null, null, 1, 10);
        Assert.Single(busca.Items);

        var porOrg = await c.Admin.ListAsync(null, null, c.OrgB, null, 1, 50);
        Assert.Contains(porOrg.Items, i => i.Email == "so-b@a.com");

        var adminA = c.Existente("adma@a.com", Role.OrganizationAdmin, c.OrgA);
        c.Como(adminA);
        var visiveis = await c.Admin.ListAsync(null, null, null, null, 1, 100);
        Assert.DoesNotContain(visiveis.Items, i => i.Email == "so-b@a.com");
        await Assert.ThrowsAsync<IdentityResourceNotFoundException>(() => c.Admin.ListAsync(null, null, c.OrgB, null, 1, 10));
    }

    [Fact]
    public async Task Usuario_sem_permissao_de_leitura_de_usuarios_nao_lista_nem_consulta_o_uso()
    {
        var c = new Cenario();
        var atendente = c.Existente("at@a.com", Role.Attendant, c.OrgA);
        c.Como(atendente);

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.ListAsync(null, null, null, null, 1, 10));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.Admin.GetUsageAsync());
    }

    // Compatibilidade

    [Fact]
    public void Papeis_antigos_mantem_exatamente_as_permissoes_que_tinham_e_so_o_administrador_ganha_as_duas_novas()
    {
        var org = Guid.NewGuid();
        var novas = new[] { Permission.UserStatusManage, Permission.UserPermissionManage };

        foreach (var papel in new[] { Role.User, Role.Manager, Role.Attendant })
        {
            Assert.DoesNotContain(RolePermissions.Expand(papel, org), g => novas.Contains(g.Permission));
        }

        var admin = RolePermissions.Expand(Role.OrganizationAdmin, org).Select(g => g.Permission).ToHashSet();
        Assert.All(novas, n => Assert.Contains(n, admin));
        // O resto do administrador permanece como antes (sem tocar nas permissões antigas).
        Assert.Contains(Permission.UserManage, admin);
        Assert.Contains(Permission.IntegrationExecuteWrite, admin);
    }

    [Fact]
    public void Catalogo_so_tem_permissoes_implementadas_e_nunca_a_operacao_de_sistema()
    {
        var todas = Enum.GetValues<Permission>();
        Assert.DoesNotContain(PermissionCatalog.All, i => i.Permission == Permission.SystemOperation);
        Assert.All(PermissionCatalog.All, i => Assert.Contains(i.Permission, todas));
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(i => i.Permission).Distinct().Count());
        Assert.Contains(PermissionCatalog.All, i => i.Permission == Permission.UserStatusManage);
    }

    // Seed interno x provisionamento comercial

    [Fact]
    public async Task Provisionamento_comercial_so_cria_um_administrador_mesmo_com_o_cadastro_habilitado_e_e_idempotente_para_o_mesmo_email()
    {
        var c = new Cenario(limite: 10);
        c.ComoSistema();
        var usuarios = AdminFabrica.Usuarios(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria);

        Assert.True(await usuarios.EnsureFirstTenantAdministratorAsync("dono@a.com", "Dono", Senhas.Valida));
        Assert.False(await usuarios.EnsureFirstTenantAdministratorAsync("dono@a.com", "Dono", Senhas.Valida));
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.EnsureFirstTenantAdministratorAsync("outro@a.com", "Outro", Senhas.Valida));

        Assert.Single(c.Dados.Usuarios);
    }

    [Fact]
    public async Task Provisionamento_comercial_respeita_o_limite_e_exige_contexto_de_sistema()
    {
        var c = new Cenario(limite: 1, cadastroHabilitado: false);
        c.Existente("pendente-ocupa@a.com", Role.User, c.OrgA); // ocupa a única vaga
        c.ComoSistema();
        var usuarios = AdminFabrica.Usuarios(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria);

        await Assert.ThrowsAsync<UserLimitReachedException>(() => usuarios.EnsureFirstTenantAdministratorAsync("dono@a.com", "Dono", Senhas.Valida));

        c.Como(c.Existente("adm@a.com", Role.OrganizationAdmin, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => usuarios.EnsureFirstTenantAdministratorAsync("x@a.com", "X", Senhas.Valida));
    }

    [Fact]
    public async Task Seed_interno_permite_varios_administradores_com_cadastro_habilitado_mas_com_cadastro_desabilitado_vale_so_a_excecao_unica()
    {
        var habilitado = new Cenario(limite: 10);
        habilitado.ComoSistema();
        var seed = AdminFabrica.Usuarios(habilitado.Dados, habilitado.Tenants, habilitado.Organizacoes, habilitado.Hasher, habilitado.Opcoes, habilitado.Holder, habilitado.Relogio, habilitado.Auditoria);
        Assert.True(await seed.EnsureTenantAdministratorAsync("a@a.com", "A", Senhas.Valida));
        Assert.True(await seed.EnsureTenantAdministratorAsync("b@a.com", "B", Senhas.Valida));

        var desabilitado = new Cenario(limite: 10, cadastroHabilitado: false);
        desabilitado.ComoSistema();
        var seed2 = AdminFabrica.Usuarios(desabilitado.Dados, desabilitado.Tenants, desabilitado.Organizacoes, desabilitado.Hasher, desabilitado.Opcoes, desabilitado.Holder, desabilitado.Relogio, desabilitado.Auditoria);
        Assert.True(await seed2.EnsureTenantAdministratorAsync("a@a.com", "A", Senhas.Valida));
        await Assert.ThrowsAsync<IdentityValidationException>(() => seed2.EnsureTenantAdministratorAsync("b@a.com", "B", Senhas.Valida));
    }

    // Política ausente: compatibilidade das empresas legadas, nunca brecha do provisionamento comercial

    [Fact]
    public async Task Provisionamento_comercial_exige_a_politica_explicita_mas_o_seed_interno_e_empresas_legadas_seguem_sem_ela()
    {
        var c = new Cenario(); // sem linha de política: empresa anterior à Etapa 01
        c.ComoSistema();
        var usuarios = AdminFabrica.Usuarios(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria);

        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.EnsureFirstTenantAdministratorAsync("dono@a.com", "Dono", Senhas.Valida));
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono"));
        Assert.Empty(c.Dados.Usuarios);

        // O seed interno (desenvolvimento e testes) e o ingresso das empresas legadas continuam sem política (ilimitada e habilitada).
        Assert.True(await usuarios.EnsureTenantAdministratorAsync("seed@a.com", "Seed", Senhas.Valida));
        c.Como(c.Dados.Usuarios.Single());
        await c.Admin.AdmitAsync(c.OrgA, "nova@a.com", "Nova", Role.User, null);
        var uso = await c.Admin.GetUsageAsync();
        Assert.Null(uso.Limit);
        Assert.True(uso.RegistrationEnabled);
    }

    [Fact]
    public async Task Politica_criada_por_ensure_nao_altera_a_existente_e_a_ausencia_nao_contorna_o_limite_depois_de_definida()
    {
        var c = new Cenario();
        var loja = new AdminEmMemoria(c.Dados, c.Holder);
        var agora = c.Relogio.GetUtcNow();

        Assert.True(await loja.EnsurePolicyAsync(c.Tenant.TenantId, 2, true, agora, default));
        Assert.False(await loja.EnsurePolicyAsync(c.Tenant.TenantId, 99, false, agora, default));
        var politica = await loja.GetPolicyAsync(c.Tenant.TenantId, default);
        Assert.Equal((2, true, true), (politica.MaxUsers, politica.RegistrationEnabled, politica.IsExplicit));

        c.Como(c.Administrador());
        await c.Admin.AdmitAsync(c.OrgA, "um@a.com", "Um", Role.User, null);
        await Assert.ThrowsAsync<UserLimitReachedException>(() => c.Admin.AdmitAsync(c.OrgA, "dois@a.com", "Dois", Role.User, null));
    }

    // Revisão independente da Etapa 01

    [Fact]
    public async Task Reserva_que_vence_enquanto_o_resgate_espera_a_trava_nao_e_ativada_e_a_vaga_fica_para_outra_admissao()
    {
        var c = new Cenario(limite: 2);
        var admin = c.Administrador();
        c.Como(admin);
        var admitido = await c.Admin.AdmitAsync(c.OrgA, "vence@a.com", "Vence", Role.User, null);

        // O instante de validação é o de DENTRO da trava: o relógio anda enquanto o resgate "esperava" por ela.
        c.Anonimo();
        var resgate = AdminFabrica.Senha(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria, aoEntrarNaTrava: () =>
        {
            c.Relogio.Avancar(TimeSpan.FromHours(25));
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => resgate.RedeemResetAsync("lab-a", "vence@a.com", admitido.Token, Senhas.Valida));

        Assert.Equal(UserStatus.Pending, c.Dados.Usuarios.Single(u => u.Email == "vence@a.com").Status);
        c.Como(admin);
        Assert.Equal(1, c.VagasUsadas());
        await c.Admin.AdmitAsync(c.OrgA, "outra@a.com", "Outra", Role.User, null); // a vaga liberada é de outra admissão
        Assert.Equal(2, c.VagasUsadas());
    }

    [Fact]
    public async Task Operador_so_redefine_o_acesso_de_usuario_ativo_e_nunca_recria_a_reserva_de_uma_admissao()
    {
        var c = new Cenario(limite: 3);
        var admin = c.Administrador();
        c.Existente("ativo@a.com", Role.User, c.OrgA);
        c.Como(admin);
        await c.Admin.AdmitAsync(c.OrgA, "pend@a.com", "Pend", Role.User, null);
        var pendente = c.Dados.Usuarios.Single(u => u.Email == "pend@a.com");
        c.Relogio.Avancar(TimeSpan.FromHours(30)); // reserva vencida
        c.ComoSistema();

        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Senha.IssueResetForOperatorAsync("pend@a.com"));
        c.Politica(10, habilitado: false);
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Senha.IssueResetForOperatorAsync("pend@a.com"));
        Assert.Equal(2, c.VagasUsadas()); // só o administrador e o ativo ocupam vaga: a reserva NÃO foi recriada

        c.Politica(10, habilitado: true);
        c.Como(admin);
        await c.Admin.CancelAdmissionAsync(pendente.Id);
        c.ComoSistema();
        await Assert.ThrowsAsync<IdentityValidationException>(() => c.Senha.IssueResetForOperatorAsync("pend@a.com")); // cancelada também
        var emitido = await c.Senha.IssueResetForOperatorAsync("ativo@a.com");
        Assert.StartsWith(ResetTokens.Prefix, emitido.Token);
    }

    [Fact]
    public async Task Admissao_expirada_se_recupera_com_vaga_e_cadastro_habilitado_e_e_recusada_sem_vaga_ou_com_o_cadastro_desabilitado_mas_pode_ser_cancelada()
    {
        var c = new Cenario(limite: 3);
        var admin = c.Administrador();
        c.Como(admin);
        await c.Admin.AdmitAsync(c.OrgA, "exp@a.com", "Exp", Role.User, null);
        var alvo = c.Dados.Usuarios.Single(u => u.Email == "exp@a.com");
        c.Relogio.Avancar(TimeSpan.FromHours(30));
        Assert.Equal("Expired", (await c.Admin.GetAsync(alvo.Id)).Status);

        // Sem vaga (a reserva vencida liberou a dela e outras pessoas a ocuparam).
        c.Existente("a@a.com", Role.User, c.OrgA);
        c.Existente("b@a.com", Role.User, c.OrgA);
        await Assert.ThrowsAsync<UserLimitReachedException>(() => c.Admin.IssueFirstAccessAsync(alvo.Id));
        c.Politica(10, habilitado: false);
        await Assert.ThrowsAsync<RegistrationDisabledException>(() => c.Admin.IssueFirstAccessAsync(alvo.Id));

        // Com vaga e cadastro habilitado, o novo token reserva de novo.
        c.Politica(10, habilitado: true);
        var novo = await c.Admin.IssueFirstAccessAsync(alvo.Id);
        Assert.StartsWith(ResetTokens.Prefix, novo.Token);
        Assert.Equal("Pending", (await c.Admin.GetAsync(alvo.Id)).Status);

        // Cancelar a admissão sempre é possível (e libera a vaga), mesmo desabilitado.
        c.Politica(3, habilitado: false);
        await c.Admin.CancelAdmissionAsync(alvo.Id);
        Assert.Equal(UserStatus.Cancelled, alvo.Status);
    }

    // Reemissão do token do administrador inicial (provisionamento pela BImeta)

    private static UserService Provisionamento(Cenario c) =>
        AdminFabrica.Usuarios(c.Dados, c.Tenants, c.Organizacoes, c.Hasher, c.Opcoes, c.Holder, c.Relogio, c.Auditoria);

    [Fact]
    public async Task Administrador_inicial_com_token_expirado_recebe_novo_token_na_mesma_conta_e_o_anterior_deixa_de_valer()
    {
        var c = new Cenario(limite: 5);
        c.ComoSistema();
        var usuarios = Provisionamento(c);
        var primeiro = await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");
        Assert.False(primeiro.Reissued);
        var dono = c.Dados.Usuarios.Single();
        Assert.True(dono.IsInitialAdministrator);

        c.Relogio.Avancar(TimeSpan.FromHours(25)); // o token venceu e a reserva foi liberada
        var novo = await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");

        Assert.True(novo.Reissued);
        Assert.NotEqual(primeiro.Token, novo.Token);
        Assert.Same(dono, c.Dados.Usuarios.Single()); // a MESMA conta, sem duplicar usuário nem concessão
        Assert.Single(c.Dados.Concessoes, g => g.UserId == dono.Id);
        Assert.Equal(c.Relogio.GetUtcNow().AddHours(24), novo.ExpiresAt);
        Assert.Equal(1, c.VagasUsadas());

        // O token anterior não vale (mesmo sem estar vencido ele foi invalidado pela substituição); o novo conclui o primeiro acesso.
        c.Anonimo();
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "dono@a.com", primeiro.Token, Senhas.Valida));
        await c.Senha.RedeemResetAsync("lab-a", "dono@a.com", novo.Token, Senhas.Valida);
        Assert.Equal(UserStatus.Active, dono.Status);
        Assert.False(dono.IsInitialAdministrator);
        Assert.DoesNotContain(c.Auditoria.Eventos, e => e.Entrada.ToString()!.Contains(novo.Token, StringComparison.Ordinal) || e.Entrada.ToString()!.Contains(primeiro.Token, StringComparison.Ordinal));
        Assert.Contains(c.Auditoria.Entradas, e => e.Operation == "identity.tenant-admin.first-access.reissue" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Reemissao_com_reserva_viva_nao_muda_as_vagas_e_substitui_o_token()
    {
        var c = new Cenario(limite: 1);
        c.ComoSistema();
        var usuarios = Provisionamento(c);
        var primeiro = await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");

        var segundo = await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono"); // no limite, mas a reserva é a mesma

        Assert.True(segundo.Reissued);
        Assert.Equal(1, c.VagasUsadas());
        c.Anonimo();
        await Assert.ThrowsAsync<InvalidPasswordResetException>(() => c.Senha.RedeemResetAsync("lab-a", "dono@a.com", primeiro.Token, Senhas.Valida));
    }

    [Fact]
    public async Task Reemissao_com_a_reserva_vencida_respeita_o_limite_e_so_a_excecao_da_habilitacao_vale_para_o_administrador_inicial()
    {
        var c = new Cenario(limite: 1);
        c.ComoSistema();
        var usuarios = Provisionamento(c);
        await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");
        c.Relogio.Avancar(TimeSpan.FromHours(25));

        // A vaga liberada pelo vencimento é ocupada por outra pessoa: sem vaga, a reemissão é recusada (o limite vale).
        c.Existente("outra@a.com", Role.User, c.OrgA);
        await Assert.ThrowsAsync<UserLimitReachedException>(() => usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono"));

        // Com vaga e o cadastro DESABILITADO durante o provisionamento inicial: a exceção vale só para esta conclusão.
        var d = new Cenario(limite: 2, cadastroHabilitado: false);
        d.ComoSistema();
        var usuariosD = Provisionamento(d);
        await usuariosD.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");
        d.Relogio.Avancar(TimeSpan.FromHours(25));
        var novo = await usuariosD.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");
        Assert.True(novo.Reissued);

        // ...e NÃO serve para cadastrar outro administrador nem outro usuário.
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuariosD.ProvisionFirstAdministratorAsync("outro@a.com", "Outro"));
        d.Como(d.Dados.Usuarios.Single());
        d.Dados.Usuarios.Single().Activate(d.Relogio.GetUtcNow());
        d.Como(d.Dados.Usuarios.Single());
        await Assert.ThrowsAsync<RegistrationDisabledException>(() => d.Admin.AdmitAsync(d.OrgA, "x@a.com", "X", Role.User, null));
    }

    [Fact]
    public async Task Mecanismo_nao_serve_a_outro_administrador_nem_depois_da_conclusao_do_primeiro_acesso()
    {
        var c = new Cenario(limite: 10);
        c.ComoSistema();
        var usuarios = Provisionamento(c);
        var primeiro = await usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono");

        // Outro e-mail: nunca cria nem reemite (a conta inicial continua a única).
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.ProvisionFirstAdministratorAsync("outro@a.com", "Outro"));

        // Um administrador pendente de escopo do tenant SEM a marca (não provisionado pelo first-admin) não é alcançado, mesmo sendo o e-mail certo.
        var semMarca = User.CreatePending(c.Tenant.TenantId, "sem@a.com", "Sem marca", c.Relogio.GetUtcNow());
        c.Dados.Usuarios.Add(semMarca);
        c.Dados.Concessoes.Add(UserOrganizationGrant.Create(c.Tenant.TenantId, semMarca.Id, null, Role.OrganizationAdmin, c.Relogio.GetUtcNow(), null));
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.ProvisionFirstAdministratorAsync("sem@a.com", "Sem marca"));

        // Depois da conclusão do primeiro acesso, nunca mais (a marca some e a conta está ativa).
        c.Anonimo();
        await c.Senha.RedeemResetAsync("lab-a", "dono@a.com", primeiro.Token, Senhas.Valida);
        c.ComoSistema();
        await Assert.ThrowsAsync<IdentityValidationException>(() => usuarios.ProvisionFirstAdministratorAsync("dono@a.com", "Dono"));
        Assert.False(c.Dados.Usuarios.Single(u => u.Email == "dono@a.com").IsInitialAdministrator);

        // Mesmo uma admissão cancelada perde a marca (não há reemissão para ela).
        var cancelada = User.CreatePending(c.Tenant.TenantId, "canc@a.com", "Canc", c.Relogio.GetUtcNow(), initialAdministrator: true);
        cancelada.CancelAdmission(c.Relogio.GetUtcNow());
        Assert.False(cancelada.IsInitialAdministrator);
    }
}
