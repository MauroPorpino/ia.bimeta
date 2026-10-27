using IaBimeta.Tenancy.Application;

namespace IaBimeta.Tenancy.Presentation;

/// <summary>Corpo da criação de organização. Não existe campo de tenant: o tenant vem do contexto confiável.</summary>
public sealed record CreateOrganizationRequest(string? Name);

public sealed record OrganizationResponse(Guid Id, string Name, DateTimeOffset CreatedAt)
{
    public static OrganizationResponse From(OrganizationView view) => new(view.Id, view.Name, view.CreatedAt);
}
