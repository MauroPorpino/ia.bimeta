using IaBimeta.SharedKernel;

namespace IaBimeta.Audit.Contracts.Commands;

/// <summary>Registro de eventos na trilha de auditoria, que só recebe inclusões (append-only).</summary>
public interface IAuditWriter
{
    /// <summary>Registra a operação do contexto atual: tenant, ator e origem vêm do <see cref="RequestContext"/>.</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registra um evento anterior à existência de contexto (a autenticação). O tenant e o ator foram resolvidos pelo
    /// Identity a partir de fonte confiável, e nunca vêm de um parâmetro da requisição.
    /// </summary>
    Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default);
}
