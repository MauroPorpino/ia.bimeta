using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>Escritor de auditoria que sempre falha, registrado no lugar do real para provar que a falha nunca vira sucesso.</summary>
internal sealed class AuditoriaQueFalha : IAuditWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("auditoria indisponível (simulada)");

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("auditoria indisponível (simulada)");
}
