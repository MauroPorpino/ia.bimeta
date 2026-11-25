using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// A administração de usuários do tenant (Etapa 01): admissão com primeiro acesso, edição, ativação e desativação, perfis, permissões individuais, vagas. Todas as regras de segurança estão AQUI, no backend:
/// <list type="bullet">
/// <item><b>Quem administra só dá o que tem:</b> o papel atribuído e a permissão concedida precisam estar entre as permissões efetivas do responsável no escopo; papéis acima de Atendente e personalização exigem <see cref="Permission.UserPermissionManage"/>.</item>
/// <item><b>Quem tem mais não é alcançado por quem tem menos:</b> toda operação sobre um usuário exige que as permissões efetivas dele estejam cobertas pelas do responsável (<see cref="Covers"/>), o que vale para cadastro, edição, e-mail, status, redefinição, token e sessões.</item>
/// <item><b>Ninguém administra a si mesmo</b> (autopromoção); a troca da própria senha continua pelo mecanismo existente.</item>
/// <item><b>Último administrador:</b> sob a trava de admissão do tenant, nenhuma operação deixa o tenant sem um administrador ativo e utilizável, medido pelas permissões efetivas.</item>
/// <item><b>Vagas e habilitação:</b> pela <see cref="UserAdmissionGuard"/>, sempre dentro da trava.</item>
/// </list>
/// </summary>
public sealed class UserAdminService
{
    /// <summary>As permissões que fazem de um usuário um administrador essencial (no escopo do tenant).</summary>
    private static readonly Permission[] EssentialAdministratorPermissions = { Permission.UserManage, Permission.UserPermissionManage, Permission.UserStatusManage };

    private readonly IUserRepository _users;
    private readonly IGrantRepository _grants;
    private readonly IAuthenticationStore _auth;
    private readonly IUserAdminStore _store;
    private readonly IOrganizationDirectory _organizations;
    private readonly UserAdmissionGuard _guard;
    private readonly PasswordHasher _hasher;
    private readonly IdentityOptions _options;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public UserAdminService(
        IUserRepository users,
        IGrantRepository grants,
        IAuthenticationStore auth,
        IUserAdminStore store,
        IOrganizationDirectory organizations,
        UserAdmissionGuard guard,
        PasswordHasher hasher,
        IdentityOptions options,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _users = users;
        _grants = grants;
        _auth = auth;
        _store = store;
        _organizations = organizations;
        _guard = guard;
        _hasher = hasher;
        _options = options;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    // Consultas

    /// <summary>As vagas, o limite e a habilitação. Qualquer usuário com leitura de usuários consulta; só a plataforma altera.</summary>
    public async Task<UsageView> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireUserRead(contexto);

        var politica = await _store.GetPolicyAsync(contexto.TenantId, cancellationToken);
        var uso = await _store.GetSeatUsageAsync(contexto.TenantId, _clock.GetUtcNow(), cancellationToken);
        var usadas = uso.SeatsUsed;
        int? disponiveis = politica.MaxUsers is { } max ? Math.Max(0, max - usadas) : null;
        var excesso = politica.MaxUsers is { } limite ? Math.Max(0, usadas - limite) : 0;
        return new UsageView(politica.MaxUsers, politica.RegistrationEnabled, uso.Active, uso.Inactive, uso.PendingReserved, uso.PendingExpired, uso.Cancelled, usadas, disponiveis, excesso > 0, excesso);
    }

    public async Task<UserPageView> ListAsync(string? search, Role? role, Guid? organizationId, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireUserRead(contexto);

        var visiveis = contexto.OrganizationsWithPermission(Permission.UserRead);
        if (organizationId is { } org && visiveis is not null && !visiveis.Contains(org))
        {
            throw new IdentityResourceNotFoundException("Organização");
        }

        var tamanho = Math.Clamp(pageSize, 1, 100);
        var pagina = Math.Max(1, page);
        var resultado = await _store.ListUsersAsync(new UserListQuery(search, role, organizationId, status, pagina, tamanho, visiveis), _clock.GetUtcNow(), cancellationToken);
        return new UserPageView(resultado.Items.Select(UserAdminViewMapper.ToListItem).ToList(), resultado.Total, pagina, tamanho);
    }

    public async Task<UserDetailView> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireUserRead(contexto);
        var detalhe = await LoadVisibleAsync(contexto, userId, cancellationToken);
        return UserAdminViewMapper.ToDetail(detalhe);
    }

    // Admissão

    /// <summary>
    /// Admite um usuário: pendente, com credencial inutilizável e token de primeiro acesso (uso único, até 24 h, mostrado só nesta resposta). Reserva uma vaga. Exige o cadastro habilitado e vaga livre.
    /// </summary>
    public async Task<AdmissionIssued> AdmitAsync(Guid organizationId, string? email, string? displayName, Role role, string? preferredLocale, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.UserManage, organizationId, "identity.user.admit", "organization", organizationId.ToString(), cancellationToken);
        await RequireRoleAssignableAsync(contexto, role, organizationId, "identity.user.admit", null, cancellationToken);

        User.ValidateProfile(email, displayName);
        var idioma = User.ParsePreferredLocale(preferredLocale);
        var agora = _clock.GetUtcNow();
        var usuario = User.CreatePending(contexto.TenantId, email, displayName, agora, idioma);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, organizationId, role, agora, contexto.Principal?.UserId);

        var emitido = await _store.RunExclusiveAsync(async ct =>
        {
            await _guard.RequireIngressAsync(contexto.TenantId, agora, firstAdministrator: false, ct);
            await _users.AddAsync(usuario, concessao, ct);
            return await IssueFirstAccessTokenAsync(contexto.TenantId, usuario.Id, contexto.Principal?.UserId, agora, ct);
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("identity.user.admit", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        var detalhe = await _store.GetDetailAsync(usuario.Id, agora, cancellationToken);
        return new AdmissionIssued(UserAdminViewMapper.ToListItem(detalhe!), emitido.Token, emitido.ExpiresAt);
    }

    /// <summary>
    /// Cria um usuário ATIVO com senha definida pelo administrador (caminho anterior à Etapa 01, mantido por compatibilidade). Passa pelas mesmas regras: papel atribuível, cadastro habilitado e vaga.
    /// O caminho recomendado é <see cref="AdmitAsync"/>.
    /// </summary>
    public async Task<UserView> CreateWithPasswordAsync(Guid organizationId, string? email, string? displayName, string? password, Role role, string? preferredLocale, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.UserManage, organizationId, "identity.user.create", "organization", organizationId.ToString(), cancellationToken);
        await RequireRoleAssignableAsync(contexto, role, organizationId, "identity.user.create", null, cancellationToken);

        User.ValidateProfile(email, displayName);
        var idioma = User.ParsePreferredLocale(preferredLocale);
        PasswordPolicy.Validate(password);

        var agora = _clock.GetUtcNow();
        var usuario = User.Create(contexto.TenantId, email, displayName, _hasher.Hash(password!), agora, idioma);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, organizationId, role, agora, contexto.Principal?.UserId);

        await _store.RunExclusiveAsync(async ct =>
        {
            await _guard.RequireIngressAsync(contexto.TenantId, agora, firstAdministrator: false, ct);
            await _users.AddAsync(usuario, concessao, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("identity.user.create", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return UserView.From(usuario);
    }

    /// <summary>Readmite uma admissão cancelada: é um NOVO ingresso (exige cadastro habilitado e vaga) e gera um novo token de primeiro acesso.</summary>
    public async Task<AdmissionIssued> ReadmitAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.user.readmit";
        var agora = _clock.GetUtcNow();

        var emitido = await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            if (detalhe.User.Status != UserStatus.Cancelled)
            {
                throw new IdentityValidationException("Só uma admissão cancelada pode ser readmitida.");
            }

            await _guard.RequireIngressAsync(contexto.TenantId, agora, firstAdministrator: false, ct);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Usuário");
            usuario.Readmit(agora);
            await _users.SaveChangesAsync(ct);
            return await IssueFirstAccessTokenAsync(contexto.TenantId, userId, contexto.Principal?.UserId, agora, ct);
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        var depois = await _store.GetDetailAsync(userId, agora, cancellationToken);
        return new AdmissionIssued(UserAdminViewMapper.ToListItem(depois!), emitido.Token, emitido.ExpiresAt);
    }

    /// <summary>
    /// Emite (ou SUBSTITUI) o token de primeiro acesso de um usuário pendente. Se a reserva já não estava viva (token expirado ou cancelado), é um novo ingresso: exige cadastro habilitado e vaga. O token anterior é invalidado.
    /// </summary>
    public async Task<PasswordResetIssued> IssueFirstAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.first-access.issue";
        var agora = _clock.GetUtcNow();

        var emitido = await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            if (detalhe.User.Status != UserStatus.Pending)
            {
                throw new IdentityValidationException("O primeiro acesso só se aplica a um usuário pendente. Para um usuário ativo, use a redefinição de acesso.");
            }

            if (!detalhe.HasLiveFirstAccessToken)
            {
                await _guard.RequireIngressAsync(contexto.TenantId, agora, firstAdministrator: IsTenantScopeUser(detalhe), ct);
            }

            return await IssueFirstAccessTokenAsync(contexto.TenantId, userId, contexto.Principal?.UserId, agora, ct);
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        return emitido;
    }

    /// <summary>Cancela o token pendente do usuário SEM tocar na situação dele (a reserva da vaga termina com o token). Para encerrar a admissão, use <see cref="CancelAdmissionAsync"/>.</summary>
    public async Task CancelTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.token.cancel";
        await _store.RunExclusiveAsync(async ct =>
        {
            _ = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            await _auth.RevokePasswordResetTokensAsync(contexto.TenantId, userId, _clock.GetUtcNow(), ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Cancela a admissão pendente: o usuário vai a Cancelado, o token é invalidado e a vaga é liberada.</summary>
    public async Task CancelAdmissionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.user.admission.cancel";
        var agora = _clock.GetUtcNow();
        await _store.RunExclusiveAsync(async ct =>
        {
            _ = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Usuário");
            usuario.CancelAdmission(agora);
            await _users.SaveChangesAsync(ct);
            await _auth.RevokePasswordResetTokensAsync(contexto.TenantId, userId, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    // Edição

    /// <summary>Edita nome e e-mail. Mudar o e-mail revoga os tokens pendentes e as sessões do usuário (a identidade de login mudou).</summary>
    public async Task<UserListItemView> UpdateProfileAsync(Guid userId, string? email, string? displayName, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.user.update";
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Usuário");
            var emailAnterior = usuario.Email;
            usuario.UpdateProfile(email, displayName);
            await _users.SaveChangesAsync(ct);

            if (!string.Equals(emailAnterior, usuario.Email, StringComparison.Ordinal))
            {
                await _auth.RevokePasswordResetTokensAsync(contexto.TenantId, userId, agora, ct);
                if (detalhe.User.Status == UserStatus.Active)
                {
                    await _auth.RevokeSessionsAsync(contexto.TenantId, userId, null, agora, ct);
                }
            }

            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        return UserAdminViewMapper.ToListItem((await _store.GetDetailAsync(userId, agora, cancellationToken))!);
    }

    // Ativação e desativação

    /// <summary>Desativa: impede novos acessos, revoga as sessões e libera a vaga. Preserva histórico, auditoria e concessões. Nunca desativa o último administrador.</summary>
    public async Task DeactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.user.deactivate";
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserStatusManage, operacao, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Usuário");
            usuario.Deactivate(agora);
            await _users.SaveChangesAsync(ct);
            await _auth.RevokeSessionsAsync(contexto.TenantId, userId, null, agora, ct);
            await _auth.RevokePasswordResetTokensAsync(contexto.TenantId, userId, agora, ct);
            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Reativa um usuário que já foi admitido: permitido mesmo com o cadastro desabilitado, mas exige vaga livre. Admissão pendente ou cancelada não se reativa por aqui.</summary>
    public async Task ReactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.user.reactivate";
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserStatusManage, operacao, ct);
            if (detalhe.User.Status != UserStatus.Inactive)
            {
                throw new IdentityValidationException("Só um usuário inativo pode ser reativado.");
            }

            await _guard.RequireSeatForReactivationAsync(contexto.TenantId, agora, ct);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Usuário");
            usuario.Activate(agora);
            await _users.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    // Perfis e vínculos organizacionais

    /// <summary>Concede (ou troca) o papel do usuário na organização. Não é ingresso: o usuário já pertence ao tenant. Exige as regras de elevação e a proteção do último administrador.</summary>
    public async Task SetRoleAsync(Guid organizationId, Guid userId, Role role, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.grant.set";
        UserOrganizationGrant.ValidateRole(role);
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.UserManage, organizationId, operacao, "user", userId.ToString(), cancellationToken);
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            await RequireRoleAssignableAsync(contexto, role, organizationId, operacao, userId, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);

            var existente = await _grants.FindAsync(userId, organizationId, ct);
            if (existente is null)
            {
                await _grants.AddAsync(UserOrganizationGrant.Create(contexto.TenantId, userId, organizationId, role, agora, contexto.Principal?.UserId), ct);
            }
            else
            {
                existente.Reassign(role, agora, contexto.Principal?.UserId);
                await _grants.SaveChangesAsync(ct);
            }

            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Retira o vínculo do usuário com a organização (e os ajustes individuais desse escopo).</summary>
    public async Task RevokeRoleAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.grant.revoke";
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.UserManage, organizationId, operacao, "user", userId.ToString(), cancellationToken);
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserManage, operacao, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);
            var concessao = await _grants.FindAsync(userId, organizationId, ct) ?? throw new IdentityResourceNotFoundException("Concessão");
            await _store.RemoveOverridesAsync(userId, organizationId, ct);
            await _grants.RemoveAsync(concessao, ct);
            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    // Permissões individuais

    /// <summary>
    /// Concede ou retira (Grant/Deny) uma permissão individual no escopo. Regras: só <see cref="Permission.UserPermissionManage"/>; o responsável precisa TER a permissão no escopo para concedê-la; o alvo tem de ter concessão no escopo;
    /// restrição de organização é recusada se a permissão vem do tenant (a concessão mais abrangente não é contornada).
    /// </summary>
    public async Task SetOverrideAsync(Guid userId, Guid? organizationId, Permission permission, OverrideEffect effect, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.permission.override.set";
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserPermissionManage, operacao, ct);
            if (!PermissionCatalog.IsValidInScope(permission, organizationId))
            {
                throw new IdentityValidationException("Esta permissão não pode ser personalizada neste escopo.");
            }

            if (!detalhe.Grants.Any(g => g.OrganizationId == organizationId))
            {
                throw new IdentityValidationException("O usuário não tem vínculo neste escopo.");
            }

            // Só concede o que tem, no próprio escopo. A restrição não dá privilégio, mas exige cobertura do alvo (já conferida).
            if (effect == OverrideEffect.Grant && !contexto.IsAllowed(permission, organizationId))
            {
                await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Denied), ct);
                throw new AccessDeniedException(permission);
            }

            // Restrição de organização contra permissão que o usuário tem por concessão de tenant: recusada (precedência entre escopos).
            if (effect == OverrideEffect.Deny && organizationId is not null)
            {
                var doTenant = EffectivePermissions.Compute(detalhe.Grants, detalhe.Overrides).Any(g => g.OrganizationId is null && g.Permission == permission);
                if (doTenant)
                {
                    throw new IdentityValidationException("O usuário tem esta permissão por vínculo com o tenant inteiro; a restrição só vale por organização se o vínculo for por organização.");
                }
            }

            var eraAdministrador = IsUsableAdministrator(detalhe);
            var existente = await _store.FindOverrideAsync(userId, organizationId, permission, ct);
            if (existente is null)
            {
                await _store.AddOverrideAsync(UserPermissionOverride.Create(contexto.TenantId, userId, organizationId, permission, effect, agora, contexto.Principal?.UserId), ct);
            }
            else
            {
                existente.Replace(effect, agora, contexto.Principal?.UserId);
                await _store.SaveChangesAsync(ct);
            }

            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao + "." + effect.ToString().ToLowerInvariant(), "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Remove um ajuste individual (volta ao padrão do perfil para essa permissão).</summary>
    public async Task RemoveOverrideAsync(Guid userId, Guid? organizationId, Permission permission, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.permission.override.remove";
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserPermissionManage, operacao, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);
            var existente = await _store.FindOverrideAsync(userId, organizationId, permission, ct) ?? throw new IdentityResourceNotFoundException("Ajuste");

            // Remover uma restrição devolve a permissão: só quem a tem (e cobre o resultado) pode fazê-lo.
            if (existente.Effect == OverrideEffect.Deny && !contexto.IsAllowed(permission, organizationId))
            {
                await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Denied), ct);
                throw new AccessDeniedException(permission);
            }

            await _store.RemoveOverrideAsync(existente, ct);
            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Restaura o padrão do perfil: remove TODOS os ajustes do usuário (de um escopo, ou de todos com <c>scopeSpecified</c> falso).</summary>
    public async Task ResetOverridesAsync(Guid userId, bool scopeSpecified, Guid? organizationId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "identity.permission.override.reset";
        var agora = _clock.GetUtcNow();

        await _store.RunExclusiveAsync(async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.UserPermissionManage, operacao, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);

            // Remover restrições devolve permissões: o responsável só restaura se TEM tudo o que voltaria.
            var alvos = detalhe.Overrides.Where(o => !scopeSpecified || o.OrganizationId == organizationId).ToList();
            foreach (var ajuste in alvos.Where(o => o.Effect == OverrideEffect.Deny))
            {
                if (!contexto.IsAllowed(ajuste.Permission, ajuste.OrganizationId))
                {
                    await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Denied), ct);
                    throw new AccessDeniedException(ajuste.Permission);
                }
            }

            foreach (var escopo in alvos.Select(o => o.OrganizationId).Distinct())
            {
                await _store.RemoveOverridesAsync(userId, escopo, ct);
            }

            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    // Regras de segurança (reutilizadas pela redefinição de acesso e pela revogação de sessões)

    /// <summary>
    /// Carrega o alvo, confirma que o responsável o enxerga e o ADMINISTRA: tem a permissão da operação em todos os escopos do alvo, as permissões efetivas do alvo estão cobertas pelas dele, e não é o próprio usuário.
    /// A negação entra na auditoria.
    /// </summary>
    internal async Task<UserDetail> RequireAdministrableAsync(RequestContext contexto, Guid userId, Permission permission, string operation, CancellationToken cancellationToken)
    {
        var detalhe = await LoadVisibleAsync(contexto, userId, cancellationToken);

        var proprio = contexto.Principal?.UserId == userId;
        if (proprio || !HoldsInAllScopes(contexto, permission, detalhe) || !Covers(contexto, detalhe))
        {
            await _audit.WriteAsync(new AuditEntry(operation, "user", userId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(permission);
        }

        return detalhe;
    }

    private async Task<UserDetail> LoadVisibleAsync(RequestContext contexto, Guid userId, CancellationToken cancellationToken)
    {
        var detalhe = await _store.GetDetailAsync(userId, _clock.GetUtcNow(), cancellationToken);
        var visiveis = contexto.OrganizationsWithPermission(Permission.UserRead);
        var visivel = detalhe is not null && (visiveis is null || detalhe.Grants.Any(g => g.OrganizationId is null || visiveis.Contains(g.OrganizationId.Value)));
        return visivel ? detalhe! : throw new IdentityResourceNotFoundException("Usuário");
    }

    /// <summary>As permissões efetivas do alvo (todos os escopos) estão entre as do responsável: quem administra nunca alcança quem tem mais do que ele.</summary>
    internal static bool Covers(RequestContext contexto, UserDetail alvo) =>
        EffectivePermissions.Compute(alvo.Grants, alvo.Overrides).All(g => contexto.IsAllowed(g.Permission, g.OrganizationId));

    private static bool HoldsInAllScopes(RequestContext contexto, Permission permission, UserDetail alvo)
    {
        var escopos = alvo.Grants.Select(g => g.OrganizationId).Distinct().ToList();
        if (escopos.Count == 0)
        {
            escopos.Add(null);
        }

        return escopos.All(escopo => contexto.IsAllowed(permission, escopo));
    }

    private static bool IsTenantScopeUser(UserDetail detalhe) => detalhe.Grants.Any(g => g.OrganizationId is null);

    /// <summary>
    /// Um papel só é atribuído por quem tem TODAS as permissões dele no escopo, e papéis acima de Atendente exigem <see cref="Permission.UserPermissionManage"/>. Assim o supervisor com UserManage cadastra atendentes, e nunca administradores.
    /// </summary>
    private async Task RequireRoleAssignableAsync(RequestContext contexto, Role role, Guid? organizationId, string operation, Guid? targetUserId, CancellationToken cancellationToken)
    {
        UserOrganizationGrant.ValidateRole(role);

        var tudoCoberto = RolePermissions.Expand(role, organizationId).All(p => contexto.IsAllowed(p.Permission, p.OrganizationId));
        var basico = role is Role.User or Role.Attendant;
        if (tudoCoberto && (basico || contexto.IsAllowed(Permission.UserPermissionManage, organizationId)))
        {
            return;
        }

        await _audit.WriteAsync(new AuditEntry(operation, "user", targetUserId?.ToString(), AuditOutcome.Denied), cancellationToken);
        throw new AccessDeniedException(Permission.UserPermissionManage);
    }

    /// <summary>Um administrador essencial UTILIZÁVEL: ativo, com senha (não pendente, não federado) e com as permissões essenciais no escopo do tenant, calculadas pelas permissões EFETIVAS.</summary>
    public static bool IsUsableAdministrator(UserDetail detalhe)
    {
        if (detalhe.User.Status != UserStatus.Active
            || detalhe.User.AuthMode != UserAuthMode.Password
            || detalhe.User.PasswordHash.StartsWith('!'))
        {
            return false;
        }

        var efetivas = EffectivePermissions.Compute(detalhe.Grants, detalhe.Overrides);
        return EssentialAdministratorPermissions.All(p => efetivas.Any(g => g.Permission == p && g.OrganizationId is null));
    }

    /// <summary>Depois da mudança (já gravada, ainda dentro da trava e da transação), se quem foi tocado era um administrador utilizável e não sobrou nenhum, desfaz tudo.</summary>
    private async Task EnsureAdministratorRemainsAsync(bool touchedAnAdministrator, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!touchedAnAdministrator)
        {
            return;
        }

        var candidatos = await _store.ListTenantScopeUsersAsync(now, cancellationToken);
        if (!candidatos.Any(IsUsableAdministrator))
        {
            throw new LastAdministratorException();
        }
    }

    private async Task<PasswordResetIssued> IssueFirstAccessTokenAsync(TenantId tenantId, Guid userId, Guid? issuedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var vida = _options.FirstAccessLifetime;
        var limite = TimeSpan.FromHours(24);
        var duracao = vida <= TimeSpan.Zero ? TimeSpan.FromHours(24) : vida > limite ? limite : vida;
        var (token, hash) = ResetTokens.Generate();
        var registro = PasswordResetToken.Create(tenantId, userId, hash, now, duracao, issuedBy);
        await _auth.AddPasswordResetTokenAsync(registro, now, cancellationToken);
        return new PasswordResetIssued(token, registro.ExpiresAt);
    }

    private static void RequireUserRead(RequestContext contexto)
    {
        if (contexto.OrganizationsWithPermission(Permission.UserRead) is { Count: 0 })
        {
            throw new AccessDeniedException(Permission.UserRead);
        }
    }

    private async Task RequireVisibleOrganizationAsync(RequestContext contexto, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!contexto.IsAllowed(Permission.OrganizationRead, organizationId)
            || !await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            throw new IdentityResourceNotFoundException("Organização");
        }
    }
}
