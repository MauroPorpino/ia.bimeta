using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

/// <summary>Auditoria em memória: guarda o que os casos de uso registraram.</summary>
internal sealed class AuditoriaEmMemoria : IAuditWriter
{
    public List<AuditEntry> Eventos { get; } = new();

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Eventos.Add(entry);
        return Task.CompletedTask;
    }

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Eventos.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class OrganizacoesEmMemoria : IOrganizationRepository
{
    public List<Organization> Itens { get; } = new();

    public Task AddAsync(Organization organization, CancellationToken cancellationToken)
    {
        Itens.Add(organization);
        return Task.CompletedTask;
    }

    public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Itens.FirstOrDefault(o => o.Id == id));

    public Task<IReadOnlyList<Organization>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Organization>>(Itens.ToList());
}

internal sealed class UnidadesEmMemoria : IBusinessUnitRepository
{
    public List<BusinessUnit> Itens { get; } = new();

    public Task AddAsync(BusinessUnit unit, CancellationToken cancellationToken)
    {
        Itens.Add(unit);
        return Task.CompletedTask;
    }

    public Task<BusinessUnit?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Itens.FirstOrDefault(u => u.Id == id));

    public Task<IReadOnlyList<BusinessUnit>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BusinessUnit>>(Itens.Where(u => u.OrganizationId == organizationId).ToList());
}

internal sealed class SetoresEmMemoria : IDepartmentRepository
{
    public List<Department> Itens { get; } = new();

    public Task AddAsync(Department department, CancellationToken cancellationToken)
    {
        Itens.Add(department);
        return Task.CompletedTask;
    }

    public Task<Department?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Itens.FirstOrDefault(d => d.Id == id));

    public Task<IReadOnlyList<Department>> ListByBusinessUnitAsync(Guid businessUnitId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Department>>(Itens.Where(d => d.BusinessUnitId == businessUnitId).ToList());
}

/// <summary>Monta contextos de usuário com permissões explícitas, como o host faria a partir de uma sessão.</summary>
internal static class Contextos
{
    public static RequestContext DeSistema(TenantId tenant) => new(tenant, "teste");

    public static RequestContext DeUsuario(TenantId tenant, params PermissionGrant[] permissoes) =>
        new(tenant, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), permissoes));
}
