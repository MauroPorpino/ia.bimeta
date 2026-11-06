using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>O token de redefinição EM CLARO, entregue uma única vez a quem o emitiu (nunca registrado em log nem em auditoria).</summary>
public sealed record PasswordResetIssued(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Senha e sessões, sobre o MESMO mecanismo de autenticação (sessões revogáveis, hash PBKDF2, bloqueio por tentativas): trocar a própria senha, redefinir senha por token de uso único e revogar sessões.
/// <list type="bullet">
/// <item>Trocar: o usuário autenticado informa a senha atual. Erro na senha atual conta no mesmo bloqueio do login. Ao trocar, as OUTRAS sessões do usuário são revogadas (a atual continua).</item>
/// <item>Redefinir: não há serviço de e-mail, então o token é emitido por um administrador (ou pelo operador, com contexto de sistema), tem expiração curta e vale uma vez. Quem o usa não está autenticado: a resposta a qualquer
/// falha (tenant, e-mail ou token errados, expirado, já usado, revogado) é a mesma, e as tentativas têm bloqueio próprio por e-mail. Ao redefinir, TODAS as sessões do usuário são revogadas.</item>
/// </list>
/// Nenhum segredo (senha, token) vai para auditoria ou exceção.
/// </summary>
public sealed class PasswordService
{
    private static readonly TimeSpan MinResetLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxResetLifetime = TimeSpan.FromHours(24);

    private readonly ITenantDirectory _tenants;
    private readonly IAuthenticationStore _store;
    private readonly IGrantRepository _grants;
    private readonly IOrganizationDirectory _organizations;
    private readonly PasswordHasher _hasher;
    private readonly IdentityOptions _options;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;
    private readonly IRequestContextAccessor _context;

    public PasswordService(
        ITenantDirectory tenants,
        IAuthenticationStore store,
        IGrantRepository grants,
        IOrganizationDirectory organizations,
        PasswordHasher hasher,
        IdentityOptions options,
        TimeProvider clock,
        IAuditWriter audit,
        IRequestContextAccessor context)
    {
        _tenants = tenants;
        _store = store;
        _grants = grants;
        _organizations = organizations;
        _hasher = hasher;
        _options = options;
        _clock = clock;
        _audit = audit;
        _context = context;
    }

    private LoginThrottlePolicy Policy => new(_options.MaxFailedLogins, _options.FailedLoginWindow, _options.LockoutDuration);

    private TimeSpan ResetLifetime => _options.PasswordResetLifetime < MinResetLifetime ? MinResetLifetime : _options.PasswordResetLifetime > MaxResetLifetime ? MaxResetLifetime : _options.PasswordResetLifetime;

    private static string ResetKey(string normalizedEmail) => "reset:" + normalizedEmail;

    // Troca pelo próprio usuário

    /// <summary>O usuário autenticado troca a própria senha, informando a atual. As outras sessões dele são revogadas; a atual continua.</summary>
    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    /// <exception cref="IdentityValidationException">Senha atual errada, nova senha fora da política ou igual à atual.</exception>
    /// <exception cref="LoginThrottledException">Erros demais na senha atual: bloqueado por um tempo (o mesmo bloqueio do login).</exception>
    public async Task ChangeOwnPasswordAsync(string? currentPassword, string? newPassword, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var principal = contexto.RequirePrincipal();
        var tenantId = contexto.TenantId;
        var agora = _clock.GetUtcNow();

        var usuario = await _store.FindUserByIdAsync(tenantId, principal.UserId, cancellationToken)
            ?? throw new IdentityResourceNotFoundException("Usuário");

        var chave = usuario.Email;
        var contagem = await _store.FindLoginThrottleAsync(tenantId, chave, cancellationToken);
        if (contagem is not null && contagem.IsLockedAt(agora))
        {
            throw new LoginThrottledException(contagem.LockedUntil!.Value - agora);
        }

        // A política da nova senha vale antes de gastar CPU com o hash da atual.
        PasswordPolicy.Validate(newPassword);

        var verificacao = PasswordPolicy.IsWithinLimits(currentPassword)
            ? _hasher.Verify(currentPassword!, usuario.PasswordHash)
            : PasswordVerification.Failed;
        if (verificacao == PasswordVerification.Failed)
        {
            var bloqueou = await _store.RecordLoginFailureAsync(tenantId, chave, agora, Policy, cancellationToken);
            await _audit.WriteAsync(new AuditEntry("identity.password.change", "user", usuario.Id.ToString(), AuditOutcome.Denied), cancellationToken);
            if (bloqueou)
            {
                await _audit.WriteAsync(new AuditEntry("identity.login.lockout", "user", usuario.Id.ToString(), AuditOutcome.Denied), cancellationToken);
            }

            throw new IdentityValidationException("A senha atual não confere.");
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            throw new IdentityValidationException("A nova senha deve ser diferente da atual.");
        }

        await _store.UpdatePasswordHashAsync(tenantId, usuario.Id, _hasher.Hash(newPassword!), cancellationToken);
        var revogadas = await _store.RevokeSessionsAsync(tenantId, usuario.Id, principal.SessionId, agora, cancellationToken);
        await _store.RevokePasswordResetTokensAsync(tenantId, usuario.Id, agora, cancellationToken);
        await _store.ClearLoginThrottleAsync(tenantId, chave, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("identity.password.change", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        if (revogadas > 0)
        {
            await _audit.WriteAsync(new AuditEntry("identity.session.revoke-others", "user", usuario.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }
    }

    // Emissão (administrador ou operador)

    /// <summary>
    /// O administrador (gestão de usuários da organização) emite o token de redefinição de um usuário que tem acesso à organização. Os tokens anteriores ainda não usados do usuário são revogados. Quem tem só gestão da
    /// organização não redefine a senha de quem tem acesso ao tenant inteiro.
    /// </summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização ou o usuário não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários, ou o alvo tem acesso ao tenant inteiro e quem pede não.</exception>
    public async Task<PasswordResetIssued> IssueResetAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserManage, organizationId,
            "identity.password.reset.issue", "user", userId.ToString(), cancellationToken);

        var acessos = await _grants.ListAccessAsync(organizationId, cancellationToken);
        var alvo = acessos.Where(a => a.User.Id == userId).ToList();
        if (alvo.Count == 0)
        {
            throw new IdentityResourceNotFoundException("Usuário");
        }

        // Quem administra só uma organização não toma a conta de quem administra o tenant (escalada de privilégio).
        var restrito = contexto.OrganizationsWithPermission(Permission.UserManage) is not null;
        if (restrito && alvo.Any(a => a.Grant.OrganizationId is null))
        {
            await _audit.WriteAsync(new AuditEntry("identity.password.reset.issue", "user", userId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(Permission.UserManage);
        }

        return await IssueAsync(contexto.TenantId, userId, contexto.Principal?.UserId, cancellationToken);
    }

    /// <summary>
    /// O OPERADOR (contexto de sistema, no comando do host) emite o token pelo e-mail, para recuperar uma conta quando nenhum administrador pode fazê-lo (por exemplo, o único administrador esqueceu a senha).
    /// </summary>
    /// <exception cref="AccessDeniedException">O contexto é de um usuário autenticado.</exception>
    /// <exception cref="IdentityResourceNotFoundException">Não há usuário com este e-mail no tenant.</exception>
    public async Task<PasswordResetIssued> IssueResetForOperatorAsync(string? email, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem)
        {
            throw new AccessDeniedException(Permission.UserManage);
        }

        var normalizado = User.NormalizeEmail(email) ?? throw new IdentityValidationException("Informe um e-mail válido.");
        var usuario = await _store.FindUserByEmailAsync(contexto.TenantId, normalizado, cancellationToken)
            ?? throw new IdentityResourceNotFoundException("Usuário");
        return await IssueAsync(contexto.TenantId, usuario.Id, null, cancellationToken);
    }

    private async Task<PasswordResetIssued> IssueAsync(TenantId tenantId, Guid userId, Guid? issuedBy, CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var (token, hash) = ResetTokens.Generate();
        var registro = PasswordResetToken.Create(tenantId, userId, hash, agora, ResetLifetime, issuedBy);
        await _store.AddPasswordResetTokenAsync(registro, agora, cancellationToken);

        // A auditoria registra QUEM emitiu e PARA QUEM, nunca o token.
        await _audit.WriteAsync(new AuditEntry("identity.password.reset.issue", "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        return new PasswordResetIssued(token, registro.ExpiresAt);
    }

    // Uso do token (anônimo)

    /// <summary>
    /// Redefine a senha com um token de uso único. Anterior a qualquer contexto, como o login: todas as falhas viram a mesma <see cref="InvalidPasswordResetException"/>, o bloqueio por tentativas é por e-mail (existindo ou
    /// não) e o custo da consulta é o mesmo para usuário existente e inexistente. Se a NOVA senha viola a política, o token não é consumido (a mensagem não depende do token).
    /// </summary>
    /// <exception cref="IdentityValidationException">A nova senha não atende à política.</exception>
    /// <exception cref="InvalidPasswordResetException">Tenant, e-mail ou token não conferem, ou o token expirou, foi usado ou revogado.</exception>
    /// <exception cref="LoginThrottledException">Tentativas demais para este e-mail.</exception>
    public async Task RedeemResetAsync(string? tenantSlug, string? email, string? token, string? newPassword, CancellationToken cancellationToken = default)
    {
        PasswordPolicy.Validate(newPassword);

        var tenant = await _tenants.FindBySlugAsync(tenantSlug ?? string.Empty, cancellationToken);
        var tenantAtivo = tenant is { Status: TenantStatus.Active };
        var emailNormalizado = User.NormalizeEmail(email);
        var chave = tenantAtivo && emailNormalizado is not null ? ResetKey(emailNormalizado) : null;
        var agora = _clock.GetUtcNow();

        if (chave is not null)
        {
            var contagem = await _store.FindLoginThrottleAsync(tenant!.TenantId, chave, cancellationToken);
            if (contagem is not null && contagem.IsLockedAt(agora))
            {
                throw new LoginThrottledException(contagem.LockedUntil!.Value - agora);
            }
        }

        User? usuario = null;
        if (chave is not null)
        {
            usuario = await _store.FindUserByEmailAsync(tenant!.TenantId, emailNormalizado!, cancellationToken);
        }

        // A consulta do token roda sempre (com um usuário "nenhum" quando não existe): o custo não revela se o e-mail existe.
        var formatoValido = ResetTokens.TryHash(token, out var hash);
        var consumiu = tenantAtivo
            && formatoValido
            && await _store.TryConsumePasswordResetTokenAsync(tenant!.TenantId, usuario?.Id ?? Guid.Empty, hash, agora, cancellationToken)
            && usuario is not null;

        if (!consumiu)
        {
            if (chave is not null)
            {
                var bloqueou = await _store.RecordLoginFailureAsync(tenant!.TenantId, chave, agora, Policy, cancellationToken);
                await _audit.WriteForAuthenticationAsync(
                    tenant.TenantId,
                    usuario?.Id,
                    new AuditEntry("identity.password.reset.redeem", "user", usuario?.Id.ToString(), AuditOutcome.Denied),
                    cancellationToken);
                if (bloqueou)
                {
                    await _audit.WriteForAuthenticationAsync(
                        tenant.TenantId,
                        usuario?.Id,
                        new AuditEntry("identity.password.reset.lockout", "user", usuario?.Id.ToString(), AuditOutcome.Denied),
                        cancellationToken);
                }
            }

            throw new InvalidPasswordResetException();
        }

        var tenantId = tenant!.TenantId;
        await _store.UpdatePasswordHashAsync(tenantId, usuario!.Id, _hasher.Hash(newPassword!), cancellationToken);
        await _store.RevokeSessionsAsync(tenantId, usuario.Id, null, agora, cancellationToken);
        await _store.RevokePasswordResetTokensAsync(tenantId, usuario.Id, agora, cancellationToken);
        await _store.ClearLoginThrottleAsync(tenantId, chave!, cancellationToken);
        await _store.ClearLoginThrottleAsync(tenantId, usuario.Email, cancellationToken);

        await _audit.WriteForAuthenticationAsync(
            tenantId,
            usuario.Id,
            new AuditEntry("identity.password.reset.redeem", "user", usuario.Id.ToString(), AuditOutcome.Success),
            cancellationToken);
    }

    // Sessões

    /// <summary>
    /// O administrador revoga TODAS as sessões ativas de um usuário da organização (por exemplo, aparelho perdido): o token deixa de valer na próxima requisição. Quem tem só a organização não revoga as de quem tem o
    /// tenant inteiro.
    /// </summary>
    /// <exception cref="IdentityResourceNotFoundException">A organização ou o usuário não existem ou não são visíveis.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir usuários, ou o alvo tem acesso ao tenant inteiro e quem pede não.</exception>
    public async Task<int> RevokeUserSessionsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.UserManage, organizationId,
            "identity.session.revoke-all", "user", userId.ToString(), cancellationToken);

        var alvo = (await _grants.ListAccessAsync(organizationId, cancellationToken)).Where(a => a.User.Id == userId).ToList();
        if (alvo.Count == 0)
        {
            throw new IdentityResourceNotFoundException("Usuário");
        }

        if (contexto.OrganizationsWithPermission(Permission.UserManage) is not null && alvo.Any(a => a.Grant.OrganizationId is null))
        {
            await _audit.WriteAsync(new AuditEntry("identity.session.revoke-all", "user", userId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(Permission.UserManage);
        }

        var revogadas = await _store.RevokeSessionsAsync(contexto.TenantId, userId, null, _clock.GetUtcNow(), cancellationToken);
        await _audit.WriteAsync(new AuditEntry("identity.session.revoke-all", "user", userId.ToString(), AuditOutcome.Success), cancellationToken);
        return revogadas;
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
