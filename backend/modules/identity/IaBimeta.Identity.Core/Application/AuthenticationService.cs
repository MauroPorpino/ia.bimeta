using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Login e logout. O login roda antes de existir contexto: o tenant é localizado pelo registro de tenants a partir
/// do slug (que só escolhe onde procurar o usuário, não concede nada) e o usuário só entra se a senha conferir.
/// Todas as falhas viram a mesma <see cref="InvalidCredentialsException"/>, sem revelar o que existe.
/// </summary>
public sealed class AuthenticationService
{
    private readonly ITenantDirectory _tenants;
    private readonly IAuthenticationStore _store;
    private readonly ISessionRepository _sessions;
    private readonly PasswordHasher _hasher;
    private readonly IdentityOptions _options;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;
    private readonly IRequestContextAccessor _context;

    public AuthenticationService(
        ITenantDirectory tenants,
        IAuthenticationStore store,
        ISessionRepository sessions,
        PasswordHasher hasher,
        IdentityOptions options,
        TimeProvider clock,
        IAuditWriter audit,
        IRequestContextAccessor context)
    {
        _tenants = tenants;
        _store = store;
        _sessions = sessions;
        _hasher = hasher;
        _options = options;
        _clock = clock;
        _audit = audit;
        _context = context;
    }

    /// <exception cref="InvalidCredentialsException">Tenant, usuário ou senha não conferem (a mesma resposta para todos os casos).</exception>
    public async Task<LoginResult> LoginAsync(string? tenantSlug, string? email, string? password, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants.FindBySlugAsync(tenantSlug ?? string.Empty, cancellationToken);
        var tenantAtivo = tenant is { Status: TenantStatus.Active };
        var emailNormalizado = User.NormalizeEmail(email);

        User? usuario = null;
        if (tenantAtivo && emailNormalizado is not null)
        {
            usuario = await _store.FindUserByEmailAsync(tenant!.TenantId, emailNormalizado, cancellationToken);
        }

        // Senha vazia ou além do limite nem chega ao hash (não vira custo de CPU para quem chama).
        var verificacao = PasswordPolicy.IsWithinLimits(password)
            ? _hasher.Verify(password!, usuario?.PasswordHash)
            : PasswordVerification.Failed;

        if (verificacao == PasswordVerification.Failed || usuario is null)
        {
            if (tenantAtivo)
            {
                await _audit.WriteForAuthenticationAsync(
                    tenant!.TenantId,
                    usuario?.Id,
                    new AuditEntry("identity.login", "user", usuario?.Id.ToString(), AuditOutcome.Denied),
                    cancellationToken);
            }

            throw new InvalidCredentialsException();
        }

        var (token, hash) = SessionTokens.Generate();
        var sessao = Session.Create(tenant!.TenantId, usuario.Id, hash, _clock.GetUtcNow(), _options.SessionLifetime);
        await _store.AddSessionAsync(sessao, cancellationToken);

        if (verificacao == PasswordVerification.SuccessRehashNeeded)
        {
            // Os parâmetros do hash melhoraram desde que a senha foi gravada: regrava com os atuais.
            await _store.UpdatePasswordHashAsync(tenant.TenantId, usuario.Id, _hasher.Hash(password!), cancellationToken);
        }

        await _audit.WriteForAuthenticationAsync(
            tenant.TenantId,
            usuario.Id,
            new AuditEntry("identity.login", "session", sessao.Id.ToString(), AuditOutcome.Success),
            cancellationToken);

        return new LoginResult(token, sessao.ExpiresAt);
    }

    /// <summary>Revoga a sessão do contexto atual. Depois disso o token é recusado.</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    /// <exception cref="AuthenticationRequiredException">O contexto não tem usuário autenticado.</exception>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var principal = _context.GetRequired().RequirePrincipal();

        var sessao = await _sessions.FindByIdAsync(principal.SessionId, cancellationToken);
        if (sessao is not null && sessao.Revoke(_clock.GetUtcNow()))
        {
            await _sessions.SaveChangesAsync(cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry("identity.logout", "session", principal.SessionId.ToString(), AuditOutcome.Success), cancellationToken);
    }
}
