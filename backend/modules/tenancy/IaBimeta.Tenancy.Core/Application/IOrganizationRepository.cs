using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Porta de persistência das organizações. Opera sempre dentro do tenant do contexto da operação: uma organização
/// de outro tenant nunca é encontrada nem listada.
/// </summary>
public interface IOrganizationRepository
{
    /// <exception cref="DuplicateOrganizationNameException">Já existe uma organização com o mesmo nome neste tenant.</exception>
    Task AddAsync(Organization organization, CancellationToken cancellationToken);

    Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Organization>> ListAsync(CancellationToken cancellationToken);
}
