using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Casos de uso dos setores. A organização do setor é a da unidade carregada (TEN-2), nunca um parâmetro da
/// requisição, e a permissão é avaliada sobre ela.
/// </summary>
public sealed class DepartmentService
{
    private const string ResourceType = "department";

    private readonly IDepartmentRepository _departments;
    private readonly IBusinessUnitRepository _units;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public DepartmentService(
        IDepartmentRepository departments,
        IBusinessUnitRepository units,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _departments = departments;
        _units = units;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    /// <exception cref="TenancyResourceNotFoundException">A unidade não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir a estrutura da organização.</exception>
    /// <exception cref="TenancyValidationException">Dados inválidos.</exception>
    /// <exception cref="DuplicateDepartmentNameException">O nome já existe nesta unidade.</exception>
    public async Task<DepartmentView> CreateAsync(Guid businessUnitId, string name, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var unidade = await LoadVisibleUnitAsync(contexto, businessUnitId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.StructureManage, unidade.OrganizationId,
            "tenancy.department.create", "business-unit", unidade.Id.ToString(), cancellationToken);

        var setor = Department.Create(contexto.TenantId, unidade.Id, name, _clock.GetUtcNow());
        await _departments.AddAsync(setor, cancellationToken);

        await _audit.WriteAsync(new AuditEntry("tenancy.department.create", ResourceType, setor.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return DepartmentView.From(setor);
    }

    /// <exception cref="TenancyResourceNotFoundException">A unidade não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de ler a estrutura da organização.</exception>
    public async Task<IReadOnlyList<DepartmentView>> ListByBusinessUnitAsync(Guid businessUnitId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var unidade = await LoadVisibleUnitAsync(contexto, businessUnitId, cancellationToken);
        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.StructureRead, unidade.OrganizationId,
            "tenancy.department.list", "business-unit", unidade.Id.ToString(), cancellationToken);

        var setores = await _departments.ListByBusinessUnitAsync(unidade.Id, cancellationToken);
        return setores.Select(DepartmentView.From).ToList();
    }

    /// <summary>Um setor de outro tenant, ou de organização sem permissão de leitura, devolve <c>null</c> (TEN-2).</summary>
    public async Task<DepartmentView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var setor = await _departments.FindByIdAsync(id, cancellationToken);
        if (setor is null)
        {
            return null;
        }

        var unidade = await _units.FindByIdAsync(setor.BusinessUnitId, cancellationToken);
        if (unidade is null || !contexto.IsAllowed(Permission.StructureRead, unidade.OrganizationId))
        {
            return null;
        }

        return DepartmentView.From(setor);
    }

    private async Task<BusinessUnit> LoadVisibleUnitAsync(RequestContext contexto, Guid businessUnitId, CancellationToken cancellationToken)
    {
        var unidade = await _units.FindByIdAsync(businessUnitId, cancellationToken);
        if (unidade is null || !contexto.IsAllowed(Permission.OrganizationRead, unidade.OrganizationId))
        {
            throw new TenancyResourceNotFoundException("Unidade");
        }

        return unidade;
    }
}
