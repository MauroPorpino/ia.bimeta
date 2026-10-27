using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Tenancy.Application;

/// <summary>Implementação da consulta pública às organizações do tenant do contexto.</summary>
public sealed class OrganizationDirectory : IOrganizationDirectory
{
    private readonly IOrganizationRepository _organizations;
    private readonly IRequestContextAccessor _context;

    public OrganizationDirectory(IOrganizationRepository organizations, IRequestContextAccessor context)
    {
        _organizations = organizations;
        _context = context;
    }

    public async Task<bool> ExistsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();

        return await _organizations.FindByIdAsync(organizationId, cancellationToken) is not null;
    }
}
