using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>Porta de persistência do registro de tenants (dado da plataforma, sem filtro de tenant).</summary>
public interface ITenantRepository
{
    /// <exception cref="DuplicateTenantSlugException">Já existe um tenant com o mesmo slug.</exception>
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);

    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<Tenant?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
