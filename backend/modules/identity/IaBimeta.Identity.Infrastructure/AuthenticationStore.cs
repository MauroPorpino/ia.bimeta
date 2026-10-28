using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// Operações anteriores à existência de um contexto (autenticação). É a única parte do módulo que contorna o filtro
/// global, e o faz de forma estreita: toda leitura leva o predicado explícito do tenant informado (ou busca a sessão
/// pelo hash único e não enumerável), e toda gravação roda dentro do escopo de tenant da autenticação, que o
/// <c>SaveChanges</c> continua verificando. Nunca lista nem alcança dados de outro tenant.
/// </summary>
internal sealed class AuthenticationStore : IAuthenticationStore
{
    private readonly IdentityDbContext _db;

    public AuthenticationStore(IdentityDbContext db)
    {
        _db = db;
    }

    public Task<User?> FindUserByEmailAsync(TenantId tenantId, string normalizedEmail, CancellationToken cancellationToken) =>
        _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Email == normalizedEmail, cancellationToken);

    public Task<User?> FindUserByIdAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        _db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == userId, cancellationToken);

    public Task<Session?> FindSessionByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        _db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public async Task AddSessionAsync(Session session, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(session.TenantId);
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdatePasswordHashAsync(TenantId tenantId, Guid userId, string newHash, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(tenantId);

        var usuario = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == userId, cancellationToken);
        if (usuario is null)
        {
            return;
        }

        usuario.ReplacePasswordHash(newHash);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserOrganizationGrant>> ListGrantsAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        await _db.Grants.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.UserId == userId)
            .ToListAsync(cancellationToken);

    public Task<LoginThrottle?> FindLoginThrottleAsync(TenantId tenantId, string key, CancellationToken cancellationToken) =>
        _db.LoginThrottles.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Key == key, cancellationToken);

    public async Task<bool> RecordLoginFailureAsync(TenantId tenantId, string key, DateTimeOffset now, LoginThrottlePolicy policy, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(tenantId);
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Garante a linha e a trava: falhas simultâneas do mesmo e-mail se enfileiram, e nenhuma contagem se perde.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO identity.login_throttles (tenant_id, throttle_key, failed_count, window_started_at, locked_until)
               VALUES ({tenantId.Value}, {key}, 0, {now}, NULL)
               ON CONFLICT (tenant_id, throttle_key) DO NOTHING",
            cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM identity.login_throttles WHERE tenant_id = {tenantId.Value} AND throttle_key = {key} FOR UPDATE",
            cancellationToken);

        var contagem = await _db.LoginThrottles.IgnoreQueryFilters()
            .SingleAsync(t => t.TenantId == tenantId && t.Key == key, cancellationToken);
        var bloqueou = contagem.RegisterFailure(now, policy);

        await _db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        return bloqueou;
    }

    public async Task ClearLoginThrottleAsync(TenantId tenantId, string key, CancellationToken cancellationToken)
    {
        await _db.LoginThrottles.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Key == key)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
