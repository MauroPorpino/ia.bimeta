using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Tenancy.Application;

/// <summary>Implementação da consulta pública ao registro de tenants.</summary>
public sealed class TenantDirectory : ITenantDirectory
{
    private readonly ITenantRepository _tenants;

    public TenantDirectory(ITenantRepository tenants)
    {
        _tenants = tenants;
    }

    public async Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalizado = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizado.Length == 0)
        {
            return null;
        }

        var tenant = await _tenants.FindBySlugAsync(normalizado, cancellationToken);
        return tenant?.ToInfo();
    }

    public async Task<TenantInfo?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var tenant = await _tenants.FindByIdAsync(tenantId, cancellationToken);
        return tenant?.ToInfo();
    }
}
