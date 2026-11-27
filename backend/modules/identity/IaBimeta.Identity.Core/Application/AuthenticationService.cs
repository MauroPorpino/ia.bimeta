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
/// Todas as falhas de credencial viram a mesma <see cref="InvalidCredentialsException"/>, sem revelar o que existe.
/// Depois de falhas demais para o mesmo e-mail no tenant, o login fica bloqueado por um tempo (<see cref="LoginThrottle"/>):
/// o bloqueio vale para e-mail que existe e para e-mail que não existe, e enquanto durar a senha nem é verificada.
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
    private readonly ITenantOperationGate? _gate;

    public AuthenticationService(
        ITenantDirectory tenants,
        IAuthenticationStore store,
        ISessionRepository sessions,
        PasswordHasher hasher,
        IdentityOptions options,
        TimeProvider clock,
        IAuditWriter audit,
        IRequestContextAccessor context,
        ITenantOperationGate? operationGate = null)
    {
        _gate = operationGate;
        _tenants = tenants;
        _store = store;
        _sessions = sessions;
        _hasher = hasher;
        _options = options;
        _clock = clock;
        _audit = audit;
        _context = context;
    }

    private LoginThrottlePolicy Policy => new(_options.MaxFailedLogins, _options.FailedLoginWindow, _options.LockoutDuration);

    /// <exception cref="InvalidCredentialsException">Tenant, usuário ou senha não conferem (a mesma resposta para todos os casos).</exception>
    /// <exception cref="LoginThrottledException">Falhas demais para este e-mail: o login está bloqueado por um tempo.</exception>
    public Task<LoginResult> LoginAsync(string? tenantSlug, string? email, string? password, CancellationToken cancellationToken = default) =>
        LoginCoreAsync(tenantSlug, platform: false, email, password, cancellationToken);

    /// <summary>Login da administração da plataforma BImeta (ADR 0043): o tenant é SEMPRE o técnico da plataforma; sessão e permissões separadas das de qualquer empresa.</summary>
    public Task<LoginResult> PlatformLoginAsync(string? email, string? password, CancellationToken cancellationToken = default) =>
        LoginCoreAsync(PlatformTenant.Slug, platform: true, email, password, cancellationToken);

    private async Task<LoginResult> LoginCoreAsync(string? tenantSlug, bool platform, string? email, string? password, CancellationToken cancellationToken)
    {
        var tenant = await _tenants.FindBySlugAsync(tenantSlug ?? string.Empty, cancellationToken);

        // O login de empresa nunca autentica no tenant da plataforma, e o da plataforma nunca em uma empresa: a mesma recusa de tenant desconhecido.
        if (tenant is not null && (tenant.Kind == TenantKind.Platform) != platform)
        {
            tenant = null;
        }

        var tenantAtivo = tenant is { Status: TenantStatus.Active };
        var emailNormalizado = User.NormalizeEmail(email);

        // Tenant desconhecido ou suspenso e e-mail malformado não têm onde guardar contagem: quem insiste nisso é contido
        // pelo limite de taxa por origem do host, e nada disso chega a verificar senha.
        var chave = tenantAtivo ? emailNormalizado : null;

        var agora = _clock.GetUtcNow();
        LoginThrottle? contagem = null;
        if (chave is not null)
        {
            contagem = await _store.FindLoginThrottleAsync(tenant!.TenantId, chave, cancellationToken);
            if (contagem is not null && contagem.IsLockedAt(agora))
            {
                throw new LoginThrottledException(contagem.LockedUntil!.Value - agora);
            }
        }

        User? usuario = null;
        if (chave is not null)
        {
            usuario = await _store.FindUserByEmailAsync(tenant!.TenantId, chave, cancellationToken);
        }

        // Senha vazia ou além do limite nem chega ao hash (não vira custo de CPU para quem chama).
        var verificacao = PasswordPolicy.IsWithinLimits(password)
            ? _hasher.Verify(password!, usuario?.PasswordHash)
            : PasswordVerification.Failed;

        // Usuário federado (Embedded, ADR 0039) nunca entra por senha, nem que alguém grave um hash para ele: a mesma recusa de credencial.
        if (verificacao == PasswordVerification.Failed || usuario is null || usuario.AuthMode == UserAuthMode.Federated || usuario.Status != UserStatus.Active)
        {
            // A contagem vem primeiro: é a proteção, e não pode depender de a trilha de auditoria estar disponível.
            var bloqueou = chave is not null
                && await _store.RecordLoginFailureAsync(tenant!.TenantId, chave, agora, Policy, cancellationToken);

            if (tenantAtivo)
            {
                await _audit.WriteForAuthenticationAsync(
                    tenant!.TenantId,
                    usuario?.Id,
                    new AuditEntry("identity.login", "user", usuario?.Id.ToString(), AuditOutcome.Denied),
                    cancellationToken);
            }

            if (bloqueou)
            {
                // O bloqueio entra na trilha uma vez, quando acontece (e não a cada tentativa bloqueada, para o
                // atacante não encher a trilha).
                await _audit.WriteForAuthenticationAsync(
                    tenant!.TenantId,
                    usuario?.Id,
                    new AuditEntry("identity.login.lockout", "user", usuario?.Id.ToString(), AuditOutcome.Denied),
                    cancellationToken);
            }

            throw new InvalidCredentialsException();
        }

        // P1-05: a autenticação começou em "agora" (instante gravado na sessão). Antes de gravar, reconfere o estado PERSISTIDO da empresa (a leitura do início pode estar velha: o hash da senha leva tempo): suspensa ou
        // reativada depois do início da autenticação, a mesma recusa de credencial.
        if (!await TenantStillValidAsync(tenant!.TenantId, agora, cancellationToken))
        {
            throw new InvalidCredentialsException();
        }

        var (token, hash) = SessionTokens.Generate();
        var sessao = Session.Create(tenant.TenantId, usuario.Id, hash, agora, _options.SessionLifetime);
        if (!await _store.AddSessionAsync(sessao, cancellationToken))
        {
            // O usuário foi desativado entre a verificação da senha e a gravação da sessão: a mesma recusa de credencial.
            throw new InvalidCredentialsException();
        }

        // Depois da gravação, a conferência final: uma suspensão (com as revogações em lote) e a reativação podem ter acontecido entre a primeira conferência e a gravação, e a sessão nova não estaria entre as revogadas.
        // Ela é revogada na hora e o login recusado (a validação a cada requisição também a recusa: sessão iniciada antes da reativação nunca vale).
        if (!await TenantStillValidAsync(tenant.TenantId, agora, cancellationToken))
        {
            await _store.RevokeSessionAsync(tenant.TenantId, sessao.Id, _clock.GetUtcNow(), cancellationToken);

            throw new InvalidCredentialsException();
        }

        if (verificacao == PasswordVerification.SuccessRehashNeeded)
        {
            // Os parâmetros do hash melhoraram desde que a senha foi gravada: regrava com os atuais.
            await _store.UpdatePasswordHashAsync(tenant.TenantId, usuario.Id, _hasher.Hash(password!), cancellationToken);
        }

        if (contagem is not null && contagem.FailedCount > 0)
        {
            await _store.ClearLoginThrottleAsync(tenant.TenantId, chave!, cancellationToken);
        }

        await _audit.WriteForAuthenticationAsync(
            tenant.TenantId,
            usuario.Id,
            new AuditEntry("identity.login", "session", sessao.Id.ToString(), AuditOutcome.Success),
            cancellationToken);

        return new LoginResult(token, sessao.ExpiresAt);
    }

    /// <summary>A empresa continua operacional e NÃO foi reativada depois do início desta autenticação (estado persistido, sem rastreamento). Sem a porta registrada (host sem o Tenancy) vale o estado lido no início.</summary>
    private async Task<bool> TenantStillValidAsync(TenantId tenantId, DateTimeOffset authenticationStartedAt, CancellationToken cancellationToken)
    {
        if (_gate is null)
        {
            return true;
        }

        var estado = await _gate.GetAsync(tenantId, cancellationToken);
        return estado.IsOperational && (estado.OperationalSince is not { } reativadaEm || reativadaEm <= authenticationStartedAt);
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
