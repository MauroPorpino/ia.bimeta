using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Identity.Infrastructure;

public static class IdentityRetentionExtensions
{
    /// <summary>Registra as tarefas de expurgo TÉCNICO do Identity (sessões vencidas, tokens de redefinição encerrados e contadores de falha de login parados). Nenhuma toca usuário, concessão ou trilha.</summary>
    public static IServiceCollection AddIdentityRetention(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, SessionRetentionTask>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, PasswordResetTokenRetentionTask>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionTask, LoginThrottleRetentionTask>());
        return services;
    }
}

/// <summary>
/// Base das tarefas: roda fora de qualquer requisição, lê SEM o filtro global e com o predicado explícito da passada (uma empresa ou todas menos as excluídas), e apaga em lote, sem carregar entidade.
/// </summary>
internal abstract class IdentityRetentionTask<TEntity> : IRetentionTask
    where TEntity : class
{
    private readonly IServiceScopeFactory _scopes;

    protected IdentityRetentionTask(IServiceScopeFactory scopes, string name, RetentionCategory category)
    {
        _scopes = scopes;
        Name = name;
        Category = category;
    }

    public string Name { get; }

    public RetentionCategory Category { get; }

    public async Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var proprio = pass.TenantId is { } id ? new TenantId(id) : null;
        var excluidos = pass.ExcludedTenants.Select(g => new TenantId(g)).ToList();
        var elegiveis = Eligible(db, pass.Cutoff, proprio, excluidos);

        var total = await elegiveis.CountAsync(cancellationToken);
        var apagados = dryRun || total == 0 ? 0 : await elegiveis.ExecuteDeleteAsync(cancellationToken);
        return new RetentionTaskResult(Name, Category, pass.Label, pass.Days, total, apagados);
    }

    /// <param name="own">A empresa da passada, ou nulo para "todas menos as excluídas".</param>
    protected abstract IQueryable<TEntity> Eligible(IdentityDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded);
}

/// <summary>Sessões cujo fim (vencimento ou revogação) é anterior ao corte. Uma sessão ainda válida nunca é expurgada.</summary>
internal sealed class SessionRetentionTask : IdentityRetentionTask<Session>
{
    public SessionRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "identity.sessions", RetentionCategory.Sessions)
    {
    }

    protected override IQueryable<Session> Eligible(IdentityDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var encerradas = db.Sessions.IgnoreQueryFilters().Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff));
        return own is not null ? encerradas.Where(s => s.TenantId == own) : encerradas.Where(s => !excluded.Contains(s.TenantId));
    }
}

/// <summary>Tokens de redefinição já usados, revogados ou vencidos antes do corte. Um token ainda utilizável nunca é expurgado.</summary>
internal sealed class PasswordResetTokenRetentionTask : IdentityRetentionTask<PasswordResetToken>
{
    public PasswordResetTokenRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "identity.password_reset_tokens", RetentionCategory.PasswordResetTokens)
    {
    }

    protected override IQueryable<PasswordResetToken> Eligible(IdentityDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var encerrados = db.PasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.ExpiresAt < cutoff || (t.UsedAt != null && t.UsedAt < cutoff) || (t.RevokedAt != null && t.RevokedAt < cutoff));
        return own is not null ? encerrados.Where(t => t.TenantId == own) : encerrados.Where(t => !excluded.Contains(t.TenantId));
    }
}

/// <summary>Contadores de falha de login cuja janela terminou antes do corte e sem bloqueio vigente (nem passado do corte).</summary>
internal sealed class LoginThrottleRetentionTask : IdentityRetentionTask<LoginThrottle>
{
    public LoginThrottleRetentionTask(IServiceScopeFactory scopes)
        : base(scopes, "identity.login_throttles", RetentionCategory.LoginThrottles)
    {
    }

    protected override IQueryable<LoginThrottle> Eligible(IdentityDbContext db, DateTimeOffset cutoff, TenantId? own, List<TenantId> excluded)
    {
        var parados = db.LoginThrottles.IgnoreQueryFilters().Where(t => t.WindowStartedAt < cutoff && (t.LockedUntil == null || t.LockedUntil < cutoff));
        return own is not null ? parados.Where(t => t.TenantId == own) : parados.Where(t => !excluded.Contains(t.TenantId));
    }
}
