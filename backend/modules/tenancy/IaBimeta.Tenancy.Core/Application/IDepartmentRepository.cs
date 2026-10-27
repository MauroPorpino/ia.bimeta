using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Porta de persistência dos setores. Opera sempre dentro do tenant do contexto da operação: um setor de outro
/// tenant nunca é encontrado nem listado.
/// </summary>
public interface IDepartmentRepository
{
    /// <exception cref="DuplicateDepartmentNameException">Já existe um setor com o mesmo nome nesta unidade.</exception>
    Task AddAsync(Department department, CancellationToken cancellationToken);

    Task<Department?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Department>> ListByBusinessUnitAsync(Guid businessUnitId, CancellationToken cancellationToken);
}
