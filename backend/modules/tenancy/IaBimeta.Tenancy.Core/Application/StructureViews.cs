using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>Visão de uma unidade entregue à camada de apresentação (nunca a entidade).</summary>
public sealed record BusinessUnitView(Guid Id, Guid OrganizationId, string Name, BusinessUnitKind Kind, DateTimeOffset CreatedAt)
{
    internal static BusinessUnitView From(BusinessUnit unit) =>
        new(unit.Id, unit.OrganizationId, unit.Name, unit.Kind, unit.CreatedAt);
}

/// <summary>Visão de um setor entregue à camada de apresentação (nunca a entidade).</summary>
public sealed record DepartmentView(Guid Id, Guid BusinessUnitId, string Name, DateTimeOffset CreatedAt)
{
    internal static DepartmentView From(Department department) =>
        new(department.Id, department.BusinessUnitId, department.Name, department.CreatedAt);
}
