using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

internal static class TenantMapping
{
    public static TenantInfo ToInfo(this Tenant tenant) => new(tenant.Id, tenant.Slug, tenant.Name, tenant.Status);
}
