using IaBimeta.Audit.Domain;

namespace IaBimeta.Audit.Application;

/// <summary>
/// Porta de persistência da trilha. Só inclui e consulta: não há alteração nem exclusão, e o banco também as recusa.
/// A consulta opera sempre dentro do tenant do contexto.
/// </summary>
public interface IAuditRepository
{
    /// <summary>Inclui no tenant do contexto da operação.</summary>
    Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>Inclui no tenant do próprio evento, para eventos anteriores à existência de contexto (autenticação).</summary>
    Task AddForAuthenticationAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken);
}

public sealed record AuditQuery(int Take, string? Operation, string? ResourceId);
