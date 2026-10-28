using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Tenancy.Application;

/// <summary>Implementação da consulta pública aos setores do tenant do contexto.</summary>
public sealed class DepartmentDirectory : IDepartmentDirectory
{
    private readonly IDepartmentRepository _departments;
    private readonly IRequestContextAccessor _context;

    public DepartmentDirectory(IDepartmentRepository departments, IRequestContextAccessor context)
    {
        _departments = departments;
        _context = context;
    }

    public async Task<Guid?> FindBusinessUnitIdAsync(Guid departmentId, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();

        var setor = await _departments.FindByIdAsync(departmentId, cancellationToken);
        return setor?.BusinessUnitId;
    }
}
