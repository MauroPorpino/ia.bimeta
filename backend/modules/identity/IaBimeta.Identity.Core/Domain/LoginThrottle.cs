using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>Parâmetros do bloqueio temporário de login.</summary>
public sealed record LoginThrottlePolicy(int MaxFailedAttempts, TimeSpan FailureWindow, TimeSpan LockoutDuration);

/// <summary>
/// Contagem de falhas de login por e-mail dentro de um tenant (TEN-3: a chave é <c>(tenant_id, throttle_key)</c>, então
/// o mesmo e-mail em outro tenant tem contagem própria). A chave é o e-mail normalizado informado, exista o usuário ou
/// não: assim o bloqueio não revela se o e-mail existe. O bloqueio é temporário, para que quem erra a senha de outra
/// pessoa não a deixe sem acesso para sempre.
/// </summary>
public sealed class LoginThrottle : ITenantOwned
{
    public const int KeyMaxLength = 254;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private LoginThrottle(TenantId tenantId, string key, int failedCount, DateTimeOffset windowStartedAt, DateTimeOffset? lockedUntil)
    {
        TenantId = tenantId;
        Key = key;
        FailedCount = failedCount;
        WindowStartedAt = windowStartedAt;
        LockedUntil = lockedUntil;
    }

    public TenantId TenantId { get; }

    public string Key { get; }

    public int FailedCount { get; private set; }

    public DateTimeOffset WindowStartedAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public static LoginThrottle Create(TenantId tenantId, string key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return new LoginThrottle(tenantId, key, 0, now, null);
    }

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } ate && now < ate;

    /// <summary>
    /// Registra uma falha de senha. Devolve <c>true</c> quando esta falha causa o bloqueio. Um bloqueio já vencido e uma
    /// janela vencida zeram a contagem antes de contar a nova falha.
    /// </summary>
    public bool RegisterFailure(DateTimeOffset now, LoginThrottlePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (LockedUntil is { } ate && now >= ate)
        {
            FailedCount = 0;
            LockedUntil = null;
            WindowStartedAt = now;
        }
        else if (LockedUntil is null && now - WindowStartedAt >= policy.FailureWindow)
        {
            FailedCount = 0;
            WindowStartedAt = now;
        }

        if (IsLockedAt(now))
        {
            // Já bloqueado: a falha não estende o bloqueio, para que insistir não o prolongue indefinidamente.
            return false;
        }

        FailedCount++;
        if (FailedCount < policy.MaxFailedAttempts)
        {
            return false;
        }

        LockedUntil = now + policy.LockoutDuration;
        return true;
    }
}
