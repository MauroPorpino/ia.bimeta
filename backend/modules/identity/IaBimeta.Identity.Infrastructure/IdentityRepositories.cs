using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// Toda consulta passa pelo filtro global do <see cref="IdentityDbContext"/>: só enxerga o tenant do contexto e,
/// sem contexto, falha fechada (TEN-2 e TEN-3).
/// </summary>
internal sealed class UserRepository : IUserRepository
{
    private readonly IdentityDbContext _db;

    public UserRepository(IdentityDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(User user, UserOrganizationGrant initialGrant, CancellationToken cancellationToken)
    {
        _db.Users.Add(user);
        _db.Grants.Add(initialGrant);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, UserConfiguration.UniqueEmailIndex))
        {
            _db.Entry(initialGrant).State = EntityState.Detached;
            _db.Entry(user).State = EntityState.Detached;
            throw new DuplicateUserEmailException();
        }
    }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
}

internal sealed class GrantRepository : IGrantRepository
{
    private readonly IdentityDbContext _db;

    public GrantRepository(IdentityDbContext db)
    {
        _db = db;
    }

    public Task<UserOrganizationGrant?> FindAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken) =>
        _db.Grants.FirstOrDefaultAsync(g => g.UserId == userId && g.OrganizationId == organizationId, cancellationToken);

    public async Task AddAsync(UserOrganizationGrant grant, CancellationToken cancellationToken)
    {
        _db.Grants.Add(grant);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public async Task RemoveAsync(UserOrganizationGrant grant, CancellationToken cancellationToken)
    {
        _db.Grants.Remove(grant);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserOrganizationGrant>> ListByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.Grants.AsNoTracking().Where(g => g.UserId == userId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<UserAccess>> ListAccessAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var consulta =
            from g in _db.Grants.AsNoTracking()
            join u in _db.Users.AsNoTracking() on g.UserId equals u.Id
            where g.OrganizationId == organizationId || g.OrganizationId == null
            orderby u.Email
            select new { Usuario = u, Concessao = g };

        var linhas = await consulta.ToListAsync(cancellationToken);
        return linhas.Select(l => new UserAccess(l.Usuario, l.Concessao)).ToList();
    }
}

internal sealed class SessionRepository : ISessionRepository
{
    private readonly IdentityDbContext _db;

    public SessionRepository(IdentityDbContext db)
    {
        _db = db;
    }

    public Task<Session?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Sessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
