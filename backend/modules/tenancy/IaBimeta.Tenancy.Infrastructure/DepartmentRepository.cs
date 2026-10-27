using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Tenancy.Infrastructure;

/// <summary>
/// Toda consulta passa pelo filtro global do <see cref="TenancyDbContext"/>: só enxerga o tenant do contexto e,
/// sem contexto, falha fechada (TEN-2 e TEN-3).
/// </summary>
internal sealed class DepartmentRepository : IDepartmentRepository
{
    private readonly TenancyDbContext _db;

    public DepartmentRepository(TenancyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Department department, CancellationToken cancellationToken)
    {
        _db.Departments.Add(department);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, DepartmentConfiguration.UniqueNameIndex))
        {
            _db.Entry(department).State = EntityState.Detached;
            throw new DuplicateDepartmentNameException(department.Name);
        }
    }

    public Task<Department?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Department>> ListByBusinessUnitAsync(Guid businessUnitId, CancellationToken cancellationToken) =>
        await _db.Departments.AsNoTracking()
            .Where(d => d.BusinessUnitId == businessUnitId)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
}
