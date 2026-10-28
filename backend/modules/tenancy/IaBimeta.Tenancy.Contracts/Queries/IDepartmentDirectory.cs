namespace IaBimeta.Tenancy.Contracts.Queries;

/// <summary>
/// Consulta aos setores do tenant do contexto atual, para os módulos que referenciam um setor sem chave estrangeira entre
/// esquemas (por exemplo, o Conversations ao identificar o setor responsável pelo atendimento).
/// </summary>
public interface IDepartmentDirectory
{
    /// <summary>A unidade do setor, ou <c>null</c> se ele não existe no tenant do contexto.</summary>
    /// <exception cref="IaBimeta.SharedKernel.TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<Guid?> FindBusinessUnitIdAsync(Guid departmentId, CancellationToken cancellationToken = default);
}
