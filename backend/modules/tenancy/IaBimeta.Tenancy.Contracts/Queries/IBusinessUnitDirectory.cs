namespace IaBimeta.Tenancy.Contracts.Queries;

/// <summary>
/// Consulta às unidades do tenant do contexto atual, para os módulos que referenciam uma unidade sem chave estrangeira
/// entre esquemas (por exemplo, o Conversations ao identificar a unidade responsável pelo atendimento).
/// </summary>
public interface IBusinessUnitDirectory
{
    /// <summary>A organização da unidade, ou <c>null</c> se ela não existe no tenant do contexto.</summary>
    /// <exception cref="IaBimeta.SharedKernel.TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<Guid?> FindOrganizationIdAsync(Guid businessUnitId, CancellationToken cancellationToken = default);
}
