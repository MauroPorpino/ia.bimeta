namespace IaBimeta.Tenancy.Contracts.Queries;

/// <summary>
/// Consulta às organizações do tenant do contexto atual, para os módulos que referenciam uma organização sem ter
/// chave estrangeira entre esquemas (por exemplo, o Identity ao conceder acesso). Só informa a existência dentro do
/// tenant do contexto: quem pergunta decide a permissão.
/// </summary>
public interface IOrganizationDirectory
{
    /// <exception cref="IaBimeta.SharedKernel.TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<bool> ExistsAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
