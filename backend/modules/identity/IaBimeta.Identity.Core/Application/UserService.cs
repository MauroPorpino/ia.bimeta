using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Casos de uso de usuários e concessões. Tenant e permissões vêm sempre do contexto confiável (TEN-1). A
/// organização é a da rota, validada: sem leitura sobre ela, responde como inexistente; com leitura mas sem a
/// permissão da ação, a operação é negada e auditada (TEN-2).
/// </summary>
public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly IGrantRepository _grants;
    private readonly IOrganizationDirectory _organizations;
    private readonly PasswordHasher _hasher;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public UserService(
        IUserRepository users,
        IGrantRepository grants,
        IOrganizationDirectory organizations,
        PasswordHasher hasher,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _users = users;
        _grants = grants;
        _organizations = organizations;
        _hasher = hasher;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Cria um usuário e já lhe concede o papel na organização, tudo ou nada.</summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários da organização.</exception>
    /// <exception cref="IdentityValidationException">E-mail, nome, senha ou papel inválidos.</exception>
    /// <exception cref="DuplicateUserEmailException">O e-mail já existe neste tenant.</exception>
    public async Task<UserView> CreateAsync(Guid organizationId, string? email, string? displayName, string? password, Role role, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserManage, organizationId,
            "identity.user.create", "organization", organizationId.ToString(), cancellationToken);

        UserOrganizationGrant.ValidateRole(role);
        User.ValidateProfile(email, displayName);
        PasswordPolicy.Validate(password);

        var agora = _clock.GetUtcNow();
        var usuario = User.Create(contexto.TenantId, email, displayName, _hasher.Hash(password!), agora);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, organizationId, role, agora, contexto.Principal?.UserId);
        await _users.AddAsync(usuario, concessao, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("identity.user.create", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return UserView.From(usuario);
    }

    /// <summary>Concede o papel ao usuário na organização. Se já havia concessão, o papel é trocado.</summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização ou o usuário não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários da organização.</exception>
    public async Task GrantRoleAsync(Guid organizationId, Guid userId, Role role, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserManage, organizationId,
            "identity.grant.set", "user", userId.ToString(), cancellationToken);

        UserOrganizationGrant.ValidateRole(role);
        _ = await _users.FindByIdAsync(userId, cancellationToken) ?? throw new IdentityResourceNotFoundException("Usuário");

        var agora = _clock.GetUtcNow();
        var existente = await _grants.FindAsync(userId, organizationId, cancellationToken);
        if (existente is null)
        {
            await _grants.AddAsync(
                UserOrganizationGrant.Create(contexto.TenantId, userId, organizationId, role, agora, contexto.Principal?.UserId),
                cancellationToken);
        }
        else
        {
            existente.Reassign(role, agora, contexto.Principal?.UserId);
            await _grants.SaveChangesAsync(cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry("identity.grant.set", "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Revoga a concessão do usuário na organização. As sessões dele passam a ter menos permissão na próxima requisição.</summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização ou a concessão não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários da organização.</exception>
    public async Task RevokeRoleAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserManage, organizationId,
            "identity.grant.revoke", "user", userId.ToString(), cancellationToken);

        var concessao = await _grants.FindAsync(userId, organizationId, cancellationToken)
            ?? throw new IdentityResourceNotFoundException("Concessão");
        await _grants.RemoveAsync(concessao, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("identity.grant.revoke", "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <exception cref="IdentityResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de ler os usuários da organização.</exception>
    public async Task<IReadOnlyList<UserAccessView>> ListAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserRead, organizationId,
            "identity.user.list", "organization", organizationId.ToString(), cancellationToken);

        var acessos = await _grants.ListAccessAsync(organizationId, cancellationToken);
        return acessos.Select(UserAccessView.From).ToList();
    }

    /// <summary>O usuário autenticado e as suas concessões.</summary>
    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    /// <exception cref="IdentityResourceNotFoundException">O usuário da sessão já não existe.</exception>
    public async Task<CurrentUserView> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var principal = _context.GetRequired().RequirePrincipal();

        var usuario = await _users.FindByIdAsync(principal.UserId, cancellationToken)
            ?? throw new IdentityResourceNotFoundException("Usuário");
        var concessoes = await _grants.ListByUserAsync(usuario.Id, cancellationToken);

        return new CurrentUserView(
            usuario.Id,
            usuario.Email,
            usuario.DisplayName,
            concessoes.Select(c => new GrantView(c.OrganizationId, c.Role)).ToList());
    }

    /// <summary>
    /// Bootstrap do administrador do tenant, com concessão de escopo do tenant. Só um contexto de sistema (o host,
    /// no seed de desenvolvimento) pode chamar: nenhum usuário autenticado cria administradores de tenant por aqui.
    /// Idempotente: devolve <c>false</c> se o e-mail já existe.
    /// </summary>
    /// <exception cref="AccessDeniedException">O contexto é de um usuário autenticado.</exception>
    /// <exception cref="IdentityValidationException">E-mail, nome ou senha inválidos.</exception>
    public async Task<bool> EnsureTenantAdministratorAsync(string? email, string? displayName, string? password, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem)
        {
            throw new AccessDeniedException(Permission.UserManage);
        }

        var (emailNormalizado, _) = User.ValidateProfile(email, displayName);
        PasswordPolicy.Validate(password);

        if (await _users.FindByEmailAsync(emailNormalizado, cancellationToken) is not null)
        {
            return false;
        }

        var agora = _clock.GetUtcNow();
        var usuario = User.Create(contexto.TenantId, email, displayName, _hasher.Hash(password!), agora);
        var concessao = UserOrganizationGrant.Create(contexto.TenantId, usuario.Id, null, Role.OrganizationAdmin, agora, null);

        try
        {
            await _users.AddAsync(usuario, concessao, cancellationToken);
        }
        catch (DuplicateUserEmailException)
        {
            return false;
        }

        await _audit.WriteAsync(new AuditEntry("identity.tenant-admin.create", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return true;
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
