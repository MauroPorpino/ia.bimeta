using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Casos de uso das organizações. O tenant e as permissões vêm sempre do contexto confiável da operação (TEN-1),
/// nunca de parâmetro. Sem contexto, a operação falha fechada antes de tocar o armazenamento. Um usuário só enxerga
/// as organizações sobre as quais tem concessão (TEN-2); criar organização exige concessão de escopo do tenant.
/// </summary>
public sealed class OrganizationService
{
    private const string ResourceType = "organization";

    private readonly IOrganizationRepository _organizations;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public OrganizationService(IOrganizationRepository organizations, IRequestContextAccessor context, TimeProvider clock, IAuditWriter audit)
    {
        _organizations = organizations;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    /// <exception cref="AccessDeniedException">O usuário não tem concessão de escopo do tenant para criar organizações.</exception>
    /// <exception cref="TenancyValidationException">Nome inválido.</exception>
    /// <exception cref="DuplicateOrganizationNameException">O nome já existe neste tenant.</exception>
    public async Task<OrganizationView> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.OrganizationCreate, null, "tenancy.organization.create", ResourceType, null, cancellationToken);

        var organization = Organization.Create(contexto.TenantId, name, _clock.GetUtcNow());
        await _organizations.AddAsync(organization, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("tenancy.organization.create", ResourceType, organization.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return OrganizationView.From(organization);
    }

    /// <summary>Uma organização de outro tenant, ou sem concessão de leitura, devolve <c>null</c>, igual a uma inexistente (TEN-2).</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    public async Task<OrganizationView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var organization = await _organizations.FindByIdAsync(id, cancellationToken);
        if (organization is null || !contexto.IsAllowed(Permission.OrganizationRead, organization.Id))
        {
            return null;
        }

        return OrganizationView.From(organization);
    }

    /// <summary>A lista já sai filtrada pelo escopo efetivo do usuário (TEN-2).</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    public async Task<IReadOnlyList<OrganizationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var visiveis = contexto.OrganizationsWithPermission(Permission.OrganizationRead);

        var organizations = await _organizations.ListAsync(cancellationToken);
        return organizations
            .Where(o => visiveis is null || visiveis.Contains(o.Id))
            .Select(OrganizationView.From)
            .ToList();
    }
}
