using IaBimeta.Tenancy.Application;

namespace IaBimeta.Tenancy.Presentation;

/// <summary>Corpo da criação de organização. Não existe campo de tenant: o tenant vem do contexto confiável.</summary>
public sealed record CreateOrganizationRequest(string? Name);

public sealed record OrganizationResponse(Guid Id, string Name, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse From(OrganizationView view) => new(view.Id, view.Name, view.CreatedAt);
}

/// <summary>Corpo da criação de unidade. Sem campo de tenant nem de organização: a organização vem da rota e é validada.</summary>
public sealed record CreateBusinessUnitRequest(string? Name, string? Kind);

public sealed record BusinessUnitResponse(Guid Id, Guid OrganizationId, string Name, string Kind, DateTimeOffset CreatedAt)
{
    public static BusinessUnitResponse From(BusinessUnitView view) => new(view.Id, view.OrganizationId, view.Name, view.Kind.ToString(), view.CreatedAt);
}

/// <summary>Corpo da criação de setor. Sem campo de tenant nem de organização: a unidade vem da rota e é validada.</summary>
public sealed record CreateDepartmentRequest(string? Name);

public sealed record DepartmentResponse(Guid Id, Guid BusinessUnitId, string Name, DateTimeOffset CreatedAt)
{
    public static DepartmentResponse From(DepartmentView view) => new(view.Id, view.BusinessUnitId, view.Name, view.CreatedAt);
}
