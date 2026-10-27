using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Casos de uso das organizações. O tenant vem sempre do contexto confiável da operação (TEN-1), nunca de
/// parâmetro. Sem contexto, a operação falha fechada antes de tocar o armazenamento.
/// Ainda não há permissão por usuário: o Identity chega na etapa 2a e passará a decidir aqui (TEN-2).
/// </summary>
public sealed class OrganizationService
{
    private readonly IOrganizationRepository _organizations;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public OrganizationService(IOrganizationRepository organizations, IRequestContextAccessor context, TimeProvider clock)
    {
        _organizations = organizations;
        _context = context;
        _clock = clock;
    }

    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    /// <exception cref="TenancyValidationException">Nome inválido.</exception>
    /// <exception cref="DuplicateOrganizationNameException">O nome já existe neste tenant.</exception>
    public async Task<OrganizationView> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var tenantId = _context.GetRequired().TenantId;

        var organization = Organization.Create(tenantId, name, _clock.GetUtcNow());
        await _organizations.AddAsync(organization, cancellationToken);
        return OrganizationView.From(organization);
    }

    /// <summary>Uma organização de outro tenant devolve <c>null</c>, igual a uma inexistente (TEN-2).</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    public async Task<OrganizationView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();

        var organization = await _organizations.FindByIdAsync(id, cancellationToken);
        return organization is null ? null : OrganizationView.From(organization);
    }

    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    public async Task<IReadOnlyList<OrganizationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();

        var organizations = await _organizations.ListAsync(cancellationToken);
        return organizations.Select(OrganizationView.From).ToList();
    }
}
