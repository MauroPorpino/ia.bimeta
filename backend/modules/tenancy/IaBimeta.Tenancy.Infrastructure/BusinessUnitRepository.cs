using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Tenancy.Infrastructure;

/// <summary>
/// Toda consulta passa pelo filtro global do <see cref="TenancyDbContext"/>: só enxerga o tenant do contexto e,
/// sem contexto, falha fechada (TEN-2 e TEN-3).
/// </summary>
internal sealed class BusinessUnitRepository : IBusinessUnitRepository
{
    private readonly TenancyDbContext _db;

    public BusinessUnitRepository(TenancyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(BusinessUnit unit, CancellationToken cancellationToken)
    {
        _db.BusinessUnits.Add(unit);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, BusinessUnitConfiguration.UniqueNameIndex))
        {
            _db.Entry(unit).State = EntityState.Detached;
            throw new DuplicateBusinessUnitNameException(unit.Name);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, BusinessUnitConfiguration.SingleHeadquartersIndex))
        {
            _db.Entry(unit).State = EntityState.Detached;
            throw new HeadquartersAlreadyExistsException();
        }
    }

    public Task<BusinessUnit?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.BusinessUnits.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BusinessUnit>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await _db.BusinessUnits.AsNoTracking()
            .Where(u => u.OrganizationId == organizationId)
            .OrderBy(u => u.Name)
            .ToListAsync(cancellationToken);
}
