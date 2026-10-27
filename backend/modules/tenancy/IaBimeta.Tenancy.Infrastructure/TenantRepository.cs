using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Tenancy.Infrastructure;

internal sealed class TenantRepository : ITenantRepository
{
    private readonly TenancyDbContext _db;

    public TenantRepository(TenancyDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _db.Tenants.Add(tenant);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, "ix_tenants_slug"))
        {
            _db.Entry(tenant).State = EntityState.Detached;
            throw new DuplicateTenantSlugException(tenant.Slug);
        }
    }

    public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);

    public Task<Tenant?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
