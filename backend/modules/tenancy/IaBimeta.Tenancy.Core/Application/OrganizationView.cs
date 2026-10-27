using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>Visão de uma organização entregue à camada de apresentação (nunca a entidade).</summary>
public sealed record OrganizationView(Guid Id, string Name, DateTimeOffset CreatedAt)
{
    internal static OrganizationView From(Organization organization) =>
        new(organization.Id, organization.Name, organization.CreatedAt);
}
