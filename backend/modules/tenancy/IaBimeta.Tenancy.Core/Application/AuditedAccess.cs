using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Application;

internal static class AuditedAccess
{
    /// <summary>
    /// Exige a permissão sobre a organização. Se for negada, a negação entra na trilha de auditoria antes de a
    /// exceção subir: tentativa de acesso sem permissão é um evento relevante.
    /// </summary>
    public static async Task RequireAsync(
        IAuditWriter audit,
        RequestContext context,
        Permission permission,
        Guid? organizationId,
        string operation,
        string resourceType,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        if (context.IsAllowed(permission, organizationId))
        {
            return;
        }

        await audit.WriteAsync(new AuditEntry(operation, resourceType, resourceId, AuditOutcome.Denied), cancellationToken);
        throw new AccessDeniedException(permission);
    }
}
