using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Contracts.Queries;

/// <summary>
/// Consulta ao registro de tenants. É uma consulta de plataforma: serve ao host para validar que o tenant existe
/// e está ativo ao criar o <see cref="RequestContext"/>, portanto roda antes de existir um contexto.
/// </summary>
public interface ITenantDirectory
{
    Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<TenantInfo?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}
