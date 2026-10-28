using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Tenancy.Application;

/// <summary>Implementação da consulta pública às unidades do tenant do contexto.</summary>
public sealed class BusinessUnitDirectory : IBusinessUnitDirectory
{
    private readonly IBusinessUnitRepository _units;
    private readonly IRequestContextAccessor _context;

    public BusinessUnitDirectory(IBusinessUnitRepository units, IRequestContextAccessor context)
    {
        _units = units;
        _context = context;
    }

    public async Task<Guid?> FindOrganizationIdAsync(Guid businessUnitId, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();

        var unidade = await _units.FindByIdAsync(businessUnitId, cancellationToken);
        return unidade?.OrganizationId;
    }
}
