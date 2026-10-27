using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Tenancy.Infrastructure;

/// <summary>
/// Toda consulta passa pelo filtro global do <see cref="TenancyDbContext"/>: só enxerga o tenant do contexto e,
/// sem contexto, falha fechada (TEN-2 e TEN-3).
/// </summary>
internal sealed class OrganizationRepository : IOrganizationRepository
{
    private readonly TenancyDbContext _db;

    public OrganizationRepository(TenancyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Organization organization, CancellationToken cancellationToken)
    {
        _db.Organizations.Add(organization);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, "ix_organizations_tenant_id_name"))
        {
            _db.Entry(organization).State = EntityState.Detached;
            throw new DuplicateOrganizationNameException(organization.Name);
        }
    }

    public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Organization>> ListAsync(CancellationToken cancellationToken) =>
        await _db.Organizations.AsNoTracking().OrderBy(o => o.Name).ToListAsync(cancellationToken);
}
