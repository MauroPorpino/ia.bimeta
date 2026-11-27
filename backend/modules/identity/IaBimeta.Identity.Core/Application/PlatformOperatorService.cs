using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

public sealed record OperatorView(Guid Id, string Email, string DisplayName, string Status, string? Role, bool HasLiveFirstAccessToken, DateTimeOffset CreatedAt);

public sealed record OperatorPage(IReadOnlyList<OperatorView> Items, int Total, int Page, int PageSize);

/// <summary>Uma permissão de plataforma de um operador: o que o perfil dá, o ajuste individual e o resultado.</summary>
public sealed record OperatorPermissionView(string Code, string Group, string Label, bool FromRole, string? Override, bool Effective);

public sealed record OperatorDetailView(OperatorView Operator, IReadOnlyList<OperatorPermissionView> Permissions);

public sealed record OperatorAccessIssued(OperatorView Operator, string Token, DateTimeOffset ExpiresAt, string Kind);

/// <summary>
/// Operadores da plataforma BImeta (Etapa 02, ADR 0043): usuários do tenant técnico da plataforma, com papéis de plataforma no escopo do tenant. Reaproveita as portas, a trava de
/// admissão, o token de primeiro acesso, os ajustes individuais e a revogação de sessões da Etapa 01 (sem sistema paralelo de autenticação). Toda operação exige um contexto de USUÁRIO do tenant
/// técnico com a permissão de plataforma correspondente; ninguém administra a si mesmo nem quem tem mais permissões que ele; o último administrador efetivo da plataforma é protegido sob a trava.
/// Operadores não ocupam vagas comerciais (o tenant técnico não tem política de limite).
/// </summary>
public sealed class PlatformOperatorService
{
    private static readonly Permission[] EssentialAdministratorPermissions = { Permission.PlatformOperatorManage, Permission.PlatformPermissionManage };

    private readonly IUserRepository _users;
    private readonly IGrantRepository _grants;
    private readonly IAuthenticationStore _auth;
    private readonly IUserAdminStore _store;
    private readonly ITenantDirectory _tenants;
    private readonly IdentityOptions _options;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public PlatformOperatorService(
        IUserRepository users,
        IGrantRepository grants,
        IAuthenticationStore auth,
        IUserAdminStore store,
        ITenantDirectory tenants,
        IdentityOptions options,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _users = users;
        _grants = grants;
        _auth = auth;
        _store = store;
        _tenants = tenants;
        _options = options;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    // Consultas

    public async Task<OperatorPage> ListAsync(string? search, string? status, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformOperatorManage, "platform.operator.list", null, cancellationToken);
        var pagina = Math.Max(1, page);
        var tamanho = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
        var resultado = await _store.ListUsersAsync(new UserListQuery(search, null, null, status, pagina, tamanho, null), _clock.GetUtcNow(), cancellationToken);
        return new OperatorPage(resultado.Items.Select(ToView).ToList(), resultado.Total, pagina, tamanho);
    }

    /// <summary>P3: o catálogo de perfis de plataforma só é lido por operador autenticado com a gestão de operadores (mesma autoridade da tela de operadores). Anônimo ou empresa: negado e auditado.</summary>
    public Task RequireCanListRolesAsync(CancellationToken cancellationToken = default) =>
        RequirePlatformAsync(Permission.PlatformOperatorManage, "platform.operator.roles", null, cancellationToken);

    public async Task<OperatorDetailView> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await RequirePlatformAsync(Permission.PlatformOperatorManage, "platform.operator.read", userId.ToString(), cancellationToken);
        var detalhe = await LoadAsync(userId, cancellationToken);
        return ToDetail(detalhe);
    }

    // Cadastro e acesso

    public async Task<OperatorAccessIssued> AdmitAsync(string? email, string? displayName, Role role, CancellationToken cancellationToken = default)
    {
        var contexto = await RequirePlatformAsync(Permission.PlatformOperatorManage, "platform.operator.admit", null, cancellationToken);
        await RequireRoleAssignableAsync(contexto, role, "platform.operator.admit", null, cancellationToken);

        User.ValidateProfile(email, displayName);
        var agora = _clock.GetUtcNow();
        var usuario = User.CreatePending(contexto.TenantId, email, displayName, agora);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, null, role, agora, contexto.Principal?.UserId);

        var emitido = await _store.RunExclusiveAsync(async ct =>
        {
            await _users.AddAsync(usuario, concessao, ct);
            return await IssueTokenAsync(contexto.TenantId, usuario.Id, contexto.Principal?.UserId, _clock.GetUtcNow(), ct);
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("platform.operator.admit", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        var detalhe = await LoadAsync(usuario.Id, cancellationToken);
        return new OperatorAccessIssued(ToView(detalhe), emitido.Token, emitido.ExpiresAt, "first-access");
    }

    /// <summary>
    /// Emite o acesso do operador: primeiro acesso para quem ainda não concluiu a admissão, ou redefinição para quem já está ativo (substitui o token anterior e revoga as sessões abertas). Nunca para inativo ou cancelado.
    /// </summary>
    public async Task<OperatorAccessIssued> IssueAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.access.issue";

        var emitido = await ExclusiveAsync(Permission.PlatformOperatorManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformOperatorManage, operacao, ct);
            if (detalhe.User.Status is not (UserStatus.Pending or UserStatus.Active))
            {
                throw new IdentityValidationException("O acesso só pode ser emitido para um operador ativo ou com admissão pendente. Reative o operador antes.");
            }

            var agora = _clock.GetUtcNow();
            var tipo = detalhe.User.Status == UserStatus.Pending ? "first-access" : "access-reset";
            var token = await IssueTokenAsync(contexto.TenantId, userId, contexto.Principal?.UserId, agora, ct);
            if (detalhe.User.Status == UserStatus.Active)
            {
                await _auth.RevokeSessionsAsync(contexto.TenantId, userId, null, agora, ct);
            }

            return (token, tipo);
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao + "." + emitido.tipo, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        return new OperatorAccessIssued(ToView(await LoadAsync(userId, cancellationToken)), emitido.token.Token, emitido.token.ExpiresAt, emitido.tipo);
    }

    public async Task RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.sessions.revoke";
        await ExclusiveAsync(Permission.PlatformOperatorManage, operacao, userId.ToString(), async ct =>
        {
            _ = await RequireAdministrableAsync(contexto, userId, Permission.PlatformOperatorManage, operacao, ct);
            await _auth.RevokeSessionsAsync(contexto.TenantId, userId, null, _clock.GetUtcNow(), ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    public async Task<OperatorView> UpdateProfileAsync(Guid userId, string? email, string? displayName, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.update";
        await ExclusiveAsync(Permission.PlatformOperatorManage, operacao, userId.ToString(), async ct =>
        {
            _ = await RequireAdministrableAsync(contexto, userId, Permission.PlatformOperatorManage, operacao, ct);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Operador");
            usuario.UpdateProfile(email, displayName);
            await _users.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        return ToView(await LoadAsync(userId, cancellationToken));
    }

    public async Task DeactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.deactivate";
        var agora = _clock.GetUtcNow();
        await ExclusiveAsync(Permission.PlatformOperatorManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformOperatorManage, operacao, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);
            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Operador");
            usuario.Deactivate(agora);
            await _users.SaveChangesAsync(ct);
            await _auth.RevokeSessionsAsync(contexto.TenantId, userId, null, agora, ct);
            await _auth.RevokePasswordResetTokensAsync(contexto.TenantId, userId, agora, ct);
            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    public async Task ReactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.reactivate";
        var agora = _clock.GetUtcNow();
        await ExclusiveAsync(Permission.PlatformOperatorManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformOperatorManage, operacao, ct);
            if (detalhe.User.Status != UserStatus.Inactive)
            {
                throw new IdentityValidationException("Só um operador inativo pode ser reativado.");
            }

            var usuario = await _users.FindForUpdateAsync(userId, ct) ?? throw new IdentityResourceNotFoundException("Operador");
            usuario.Activate(agora);
            await _users.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    // Perfil e permissões individuais

    public async Task SetRoleAsync(Guid userId, Role role, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.role.set";
        var agora = _clock.GetUtcNow();
        await ExclusiveAsync(Permission.PlatformPermissionManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformPermissionManage, operacao, ct);
            await RequireRoleAssignableAsync(contexto, role, operacao, userId, ct);
            var eraAdministrador = IsUsableAdministrator(detalhe);

            var existente = await _grants.FindAsync(userId, null, ct);
            if (existente is null)
            {
                await _grants.AddAsync(UserOrganizationGrant.Create(contexto.TenantId, userId, null, role, agora, contexto.Principal?.UserId), ct);
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

    public async Task SetOverrideAsync(Guid userId, Permission permission, OverrideEffect effect, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.permission.set";
        var agora = _clock.GetUtcNow();
        await ExclusiveAsync(Permission.PlatformPermissionManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformPermissionManage, operacao, ct);
            if (!PlatformPermissions.IsPlatform(permission))
            {
                throw new IdentityValidationException("Esta permissão não pode ser personalizada para um operador.");
            }

            if (!detalhe.Grants.Any(g => g.OrganizationId is null && g.Role.IsPlatform()))
            {
                throw new IdentityValidationException("O operador não tem perfil de plataforma.");
            }

            // Só concede o que o responsável tem (a restrição não dá privilégio; a cobertura do alvo já foi conferida).
            if (effect == OverrideEffect.Grant && !contexto.IsAllowed(permission, null))
            {
                await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Denied), ct);
                throw new AccessDeniedException(permission);
            }

            var eraAdministrador = IsUsableAdministrator(detalhe);
            var existente = await _store.FindOverrideAsync(userId, null, permission, ct);
            if (existente is null)
            {
                await _store.AddOverrideAsync(UserPermissionOverride.Create(contexto.TenantId, userId, null, permission, effect, agora, contexto.Principal?.UserId), ct);
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

    public async Task RemoveOverrideAsync(Guid userId, Permission permission, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.permission.remove";
        var agora = _clock.GetUtcNow();
        await ExclusiveAsync(Permission.PlatformPermissionManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformPermissionManage, operacao, ct);

            // P1-01: remover uma restrição pode CONCEDER uma permissão que o responsável não tem. As permissões efetivas RESULTANTES precisam estar inteiramente cobertas pela autoridade do responsável, antes de gravar.
            var restantes = detalhe.Overrides.Where(o => !(o.OrganizationId is null && o.Permission == permission)).ToList();
            await RequireResultCoveredAsync(contexto, EffectivePermissions.Compute(detalhe.Grants, restantes), permission, userId, operacao, ct);

            var eraAdministrador = IsUsableAdministrator(detalhe);
            var existente = await _store.FindOverrideAsync(userId, null, permission, ct);
            if (existente is not null)
            {
                await _store.RemoveOverrideAsync(existente, ct);
            }

            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    public async Task ResetOverridesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        const string operacao = "platform.operator.permission.reset";
        var agora = _clock.GetUtcNow();
        await ExclusiveAsync(Permission.PlatformPermissionManage, operacao, userId.ToString(), async ct =>
        {
            var detalhe = await RequireAdministrableAsync(contexto, userId, Permission.PlatformPermissionManage, operacao, ct);

            // P1-01: restaurar o perfil descarta TODAS as restrições do alvo e pode conceder o que o responsável não tem. As permissões efetivas resultantes (só o perfil) precisam estar cobertas, antes de gravar.
            await RequireResultCoveredAsync(contexto, EffectivePermissions.Compute(detalhe.Grants, Array.Empty<UserPermissionOverride>()), null, userId, operacao, ct);

            var eraAdministrador = IsUsableAdministrator(detalhe);
            await _store.RemoveOverridesAsync(userId, null, ct);
            await EnsureAdministratorRemainsAsync(eraAdministrador, agora, ct);
            return true;
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(operacao, "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    // Provisionamento do primeiro administrador da plataforma (comando operacional do host, contexto de SISTEMA do tenant técnico)

    /// <summary>
    /// O primeiro administrador da plataforma: usuário pendente (marca de administrador inicial) com token de primeiro acesso (uso único, até 24 h, mostrado uma vez). Repetido para o mesmo e-mail enquanto ele
    /// não concluiu o primeiro acesso, reemite o token. Recusa se a plataforma já tem um administrador ativo ou se o e-mail é de outra conta. Sem credencial padrão: a senha nasce no primeiro acesso.
    /// </summary>
    public async Task<FirstAdministratorIssued> ProvisionFirstPlatformAdministratorAsync(string? email, string? displayName, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem || await _tenants.FindByIdAsync(contexto.TenantId, cancellationToken) is not { Kind: TenantKind.Platform })
        {
            throw new AccessDeniedException(Permission.PlatformOperatorManage);
        }

        var (emailNormalizado, _) = User.ValidateProfile(email, displayName);
        var agora = _clock.GetUtcNow();
        var usuario = User.CreatePending(contexto.TenantId, email, displayName, agora, initialAdministrator: true);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, null, Role.PlatformAdministrator, agora, null);

        var emitido = await _store.RunExclusiveAsync(async ct =>
        {
            var agoraNaTrava = _clock.GetUtcNow();
            var existente = await _users.FindByEmailAsync(emailNormalizado, ct);
            if (existente is not null)
            {
                if (!existente.IsInitialAdministrator || existente.Status != UserStatus.Pending)
                {
                    throw new IdentityValidationException("O administrador da plataforma já concluiu o primeiro acesso, ou este e-mail não é o do administrador inicial.");
                }

                return (usuario: existente, reemitido: true, token: await IssueTokenAsync(contexto.TenantId, existente.Id, null, agoraNaTrava, ct));
            }

            var candidatos = await _store.ListTenantScopeUsersAsync(agoraNaTrava, ct);
            if (candidatos.Any(u => u.User.Status != UserStatus.Cancelled))
            {
                throw new IdentityValidationException("A plataforma já tem operadores. Novos operadores são cadastrados por um administrador da plataforma.");
            }

            await _users.AddAsync(usuario, concessao, ct);
            return (usuario, reemitido: false, token: await IssueTokenAsync(contexto.TenantId, usuario.Id, null, agoraNaTrava, ct));
        }, cancellationToken);

        await _audit.WriteAsync(new AuditEntry(emitido.reemitido ? "platform.admin.first-access.reissue" : "platform.admin.provision", "user", emitido.usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new FirstAdministratorIssued(emitido.token.Token, emitido.token.ExpiresAt, emitido.reemitido);
    }

    // Regras comuns

    /// <summary>A verificação do tenant técnico consulta outro módulo (outra conexão): é feita ANTES da trava, nunca dentro dela (sem segura-e-espera de conexões).</summary>
    private async Task<T> ExclusiveAsync<T>(Permission permission, string operation, string? resourceId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await RequirePlatformAsync(permission, operation, resourceId, cancellationToken);
        return await _store.RunExclusiveAsync(action, cancellationToken);
    }

    /// <summary>Contexto de USUÁRIO do tenant técnico da plataforma e a permissão. Qualquer outro (sistema, empresa, sessão embutida) é negado e a negação é auditada.</summary>
    private async Task<RequestContext> RequirePlatformAsync(Permission permission, string operation, string? resourceId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var plataforma = contexto.Principal is not null
            && PlatformPermissions.IsPlatform(permission)
            && await _tenants.FindByIdAsync(contexto.TenantId, cancellationToken) is { Kind: TenantKind.Platform };

        if (!plataforma || !contexto.IsAllowed(permission, null))
        {
            await _audit.WriteAsync(new AuditEntry(operation, "user", resourceId, AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(permission);
        }

        return contexto;
    }

    private async Task<UserDetail> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        await _store.GetDetailAsync(userId, _clock.GetUtcNow(), cancellationToken) ?? throw new IdentityResourceNotFoundException("Operador");

    /// <summary>
    /// A autoridade do responsável como está PERSISTIDA agora (relida do banco, sem rastreamento): vazia se ele já não está ativo ou não existe. Chamada DENTRO da trava: a sessão pode ter sido emitida, e a permissão
    /// conferida na borda, antes de uma revogação concorrente que a trava só deixou confirmar depois (P1-01 da 2.ª revisão). A autoridade efetiva é a INTERSEÇÃO entre o contexto original e este estado.
    /// </summary>
    private async Task<IReadOnlyList<PermissionGrant>> FreshAuthorityAsync(RequestContext contexto, CancellationToken cancellationToken)
    {
        if (contexto.Principal?.UserId is not { } responsavel)
        {
            return Array.Empty<PermissionGrant>();
        }

        var detalhe = await _store.GetDetailAsync(responsavel, _clock.GetUtcNow(), cancellationToken);
        return detalhe is null || detalhe.User.Status != UserStatus.Active
            ? Array.Empty<PermissionGrant>()
            : EffectivePermissions.Compute(detalhe.Grants, detalhe.Overrides);
    }

    private static bool HasAuthority(RequestContext contexto, IReadOnlyList<PermissionGrant> fresca, Permission permission, Guid? organizationId = null) =>
        contexto.IsAllowed(permission, organizationId) && fresca.Any(a => a.Permission == permission && (a.OrganizationId is null || a.OrganizationId == organizationId));

    /// <summary>Sem autoadministração e sem alcançar quem tem mais permissões que o responsável (todas as permissões efetivas do alvo estão entre as do responsável). Chamada dentro da trava: a permissão exigida e a cobertura valem pelo estado PERSISTIDO do responsável.</summary>
    private async Task<UserDetail> RequireAdministrableAsync(RequestContext contexto, Guid userId, Permission permission, string operation, CancellationToken cancellationToken)
    {
        var detalhe = await LoadAsync(userId, cancellationToken);
        var fresca = await FreshAuthorityAsync(contexto, cancellationToken);
        var proprio = contexto.Principal?.UserId == userId;
        var cobre = EffectivePermissions.Compute(detalhe.Grants, detalhe.Overrides).All(g => HasAuthority(contexto, fresca, g.Permission, g.OrganizationId));
        if (proprio || !cobre || !HasAuthority(contexto, fresca, permission))
        {
            await _audit.WriteAsync(new AuditEntry(operation, "user", userId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(permission);
        }

        return detalhe;
    }

    /// <summary>
    /// As permissões efetivas que o alvo TERIA depois da alteração precisam estar todas entre as do responsável (sem elevação de privilégio), e o responsável precisa AINDA ter a autoridade da operação
    /// (<see cref="Permission.PlatformPermissionManage"/>) no estado persistido. Roda DENTRO da trava, com a releitura do responsável; vale a INTERSEÇÃO com o contexto original. Recusa audita Denied e não grava nada.
    /// </summary>
    private async Task RequireResultCoveredAsync(
        RequestContext contexto, IReadOnlyList<PermissionGrant> resultado, Permission? denied, Guid targetUserId, string operation, CancellationToken cancellationToken)
    {
        var fresca = await FreshAuthorityAsync(contexto, cancellationToken);
        var coberto = HasAuthority(contexto, fresca, Permission.PlatformPermissionManage)
            && resultado.All(g => HasAuthority(contexto, fresca, g.Permission, g.OrganizationId));
        if (!coberto)
        {
            await _audit.WriteAsync(new AuditEntry(operation, "user", targetUserId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(denied ?? Permission.PlatformPermissionManage);
        }
    }
    /// <summary>Só papéis da plataforma, e só por quem tem TODAS as permissões do papel; o papel de administrador exige também <see cref="Permission.PlatformPermissionManage"/>.</summary>
    private async Task RequireRoleAssignableAsync(RequestContext contexto, Role role, string operation, Guid? targetUserId, CancellationToken cancellationToken)
    {
        var tudoCoberto = role.IsPlatform() && RolePermissions.Expand(role, null).All(p => contexto.IsAllowed(p.Permission, null));
        var exigePermissao = role == Role.PlatformAdministrator;
        if (tudoCoberto && (!exigePermissao || contexto.IsAllowed(Permission.PlatformPermissionManage, null)) && contexto.IsAllowed(Permission.PlatformPermissionManage, null))
        {
            return;
        }

        await _audit.WriteAsync(new AuditEntry(operation, "user", targetUserId?.ToString(), AuditOutcome.Denied), cancellationToken);
        throw new AccessDeniedException(Permission.PlatformPermissionManage);
    }

    public static bool IsUsableAdministrator(UserDetail detalhe)
    {
        if (detalhe.User.Status != UserStatus.Active || detalhe.User.AuthMode != UserAuthMode.Password || detalhe.User.PasswordHash.StartsWith('!'))
        {
            return false;
        }

        var efetivas = EffectivePermissions.Compute(detalhe.Grants, detalhe.Overrides);
        return EssentialAdministratorPermissions.All(p => efetivas.Any(g => g.Permission == p && g.OrganizationId is null));
    }

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

    private async Task<PasswordResetIssued> IssueTokenAsync(TenantId tenantId, Guid userId, Guid? issuedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var duracao = _options.FirstAccessLifetime <= TimeSpan.Zero || _options.FirstAccessLifetime > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : _options.FirstAccessLifetime;
        var (token, hash) = ResetTokens.Generate();
        var registro = PasswordResetToken.Create(tenantId, userId, hash, now, duracao, issuedBy);
        await _auth.AddPasswordResetTokenAsync(registro, now, cancellationToken);
        return new PasswordResetIssued(token, registro.ExpiresAt);
    }

    private static OperatorView ToView(UserDetail d) =>
        new(d.User.Id, d.User.Email, d.User.DisplayName, d.DisplayStatus, d.Grants.FirstOrDefault(g => g.OrganizationId is null)?.Role.ToString(), d.HasLiveFirstAccessToken, d.User.CreatedAt);

    private static OperatorDetailView ToDetail(UserDetail d)
    {
        var efetivas = EffectivePermissions.Compute(d.Grants, d.Overrides).Select(g => g.Permission).ToHashSet();
        var doPerfil = d.Grants.Where(g => g.OrganizationId is null).SelectMany(g => RolePermissions.Expand(g.Role, null)).Select(p => p.Permission).ToHashSet();
        var permissoes = PermissionCatalog.Platform.Select(p =>
        {
            var ajuste = d.Overrides.FirstOrDefault(o => o.OrganizationId is null && o.Permission == p.Permission);
            return new OperatorPermissionView(p.Permission.ToString(), p.Group, p.Label, doPerfil.Contains(p.Permission), ajuste?.Effect.ToString(), efetivas.Contains(p.Permission));
        }).ToList();
        return new OperatorDetailView(ToView(d), permissoes);
    }
}
