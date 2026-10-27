using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Casos de uso das unidades (matriz e filiais). Tenant e permissões vêm sempre do contexto confiável (TEN-1) e a
/// permissão é avaliada sobre a organização do recurso carregado (TEN-2): sem leitura sobre a organização ela
/// responde como inexistente; com leitura mas sem a permissão da ação, a operação é negada e auditada.
/// </summary>
public sealed class BusinessUnitService
{
    private const string ResourceType = "business-unit";

    private readonly IBusinessUnitRepository _units;
    private readonly IOrganizationRepository _organizations;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public BusinessUnitService(
        IBusinessUnitRepository units,
        IOrganizationRepository organizations,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _units = units;
        _organizations = organizations;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    /// <exception cref="TenancyResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir a estrutura da organização.</exception>
    /// <exception cref="TenancyValidationException">Dados inválidos.</exception>
    /// <exception cref="DuplicateBusinessUnitNameException">O nome já existe nesta organização.</exception>
    /// <exception cref="HeadquartersAlreadyExistsException">A organização já tem matriz.</exception>
    public async Task<BusinessUnitView> CreateAsync(Guid organizationId, string name, BusinessUnitKind kind, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.StructureManage, organizationId,
            "tenancy.business-unit.create", "organization", organizationId.ToString(), cancellationToken);

        var unidade = BusinessUnit.Create(contexto.TenantId, organizationId, name, kind, _clock.GetUtcNow());
        await _units.AddAsync(unidade, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("tenancy.business-unit.create", ResourceType, unidade.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return BusinessUnitView.From(unidade);
    }

    /// <exception cref="TenancyResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de ler a estrutura da organização.</exception>
    public async Task<IReadOnlyList<BusinessUnitView>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        await RequireVisibleOrganizationAsync(contexto, organizationId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.StructureRead, organizationId,
            "tenancy.business-unit.list", "organization", organizationId.ToString(), cancellationToken);

        var unidades = await _units.ListByOrganizationAsync(organizationId, cancellationToken);
        return unidades.Select(BusinessUnitView.From).ToList();
    }

    /// <summary>Uma unidade de outro tenant, ou de organização sem permissão de leitura, devolve <c>null</c> (TEN-2).</summary>
    public async Task<BusinessUnitView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var unidade = await _units.FindByIdAsync(id, cancellationToken);
        if (unidade is null || !contexto.IsAllowed(Permission.StructureRead, unidade.OrganizationId))
        {
            return null;
        }

        return BusinessUnitView.From(unidade);
    }

    private async Task RequireVisibleOrganizationAsync(RequestContext contexto, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!contexto.IsAllowed(Permission.OrganizationRead, organizationId)
            || await _organizations.FindByIdAsync(organizationId, cancellationToken) is null)
        {
            throw new TenancyResourceNotFoundException("Organização");
        }
    }
}
