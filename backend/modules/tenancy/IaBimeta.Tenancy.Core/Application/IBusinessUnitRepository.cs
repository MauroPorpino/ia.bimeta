using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Porta de persistência das unidades. Opera sempre dentro do tenant do contexto da operação: uma unidade de outro
/// tenant nunca é encontrada nem listada.
/// </summary>
public interface IBusinessUnitRepository
{
    /// <exception cref="DuplicateBusinessUnitNameException">Já existe uma unidade com o mesmo nome nesta organização.</exception>
    /// <exception cref="HeadquartersAlreadyExistsException">A organização já tem matriz.</exception>
    Task AddAsync(BusinessUnit unit, CancellationToken cancellationToken);

    Task<BusinessUnit?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<BusinessUnit>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);
}
