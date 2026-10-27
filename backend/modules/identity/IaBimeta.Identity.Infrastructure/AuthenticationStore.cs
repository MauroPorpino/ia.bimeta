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
}
