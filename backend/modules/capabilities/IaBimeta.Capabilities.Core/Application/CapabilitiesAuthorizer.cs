using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Application;

/// <summary>
/// A permissão do motor, sempre sobre o <c>RequestContext</c> confiável e a organização da CONEXÃO (derivada do recurso carregado, nunca da requisição). Uma negativa é auditada (sem valores) antes de lançar
/// <c>AccessDeniedException</c>. Também centraliza a trilha: nenhuma entrada leva segredo, parâmetro ou texto livre (a trilha nem tem campo para isso).
/// </summary>
internal sealed class CapabilitiesAuthorizer
{
    public const string ConnectionResource = "capabilities.connection";
    public const string DefinitionResource = "capabilities.definition";
    public const string ExecutionResource = "capabilities.execution";

    private readonly IRequestContextAccessor _context;
    private readonly IAuditWriter _audit;

    public CapabilitiesAuthorizer(IRequestContextAccessor context, IAuditWriter audit)
    {
        _context = context;
        _audit = audit;
    }

    public RequestContext Context => _context.GetRequired();

    /// <summary>Exige a permissão; se faltar, audita a negativa e lança. Um contexto de sistema passa (<see cref="AccessPolicy"/>): quem não pode ser sistema confere o usuário à parte.</summary>
    public async Task RequireAsync(Permission permission, Guid? organizationId, string operation, string resourceType, Guid? resourceId, CancellationToken cancellationToken)
    {
        var contexto = Context;
        if (contexto.IsAllowed(permission, organizationId))
        {
            return;
        }

        await WriteAsync(operation, resourceType, resourceId, AuditOutcome.Denied, cancellationToken);
        throw new AccessDeniedException(permission);
    }

    /// <summary>Exige um USUÁRIO com a permissão: um contexto de sistema NUNCA passa (confirmar uma escrita, por exemplo, é sempre uma pessoa).</summary>
    public async Task RequireUserAsync(Permission permission, Guid? organizationId, string operation, string resourceType, Guid? resourceId, CancellationToken cancellationToken)
    {
        var contexto = Context;
        if (contexto.Principal is not null && contexto.IsAllowed(permission, organizationId))
        {
            return;
        }

        await WriteAsync(operation, resourceType, resourceId, AuditOutcome.Denied, cancellationToken);
        throw new AccessDeniedException(permission);
    }

    public Task WriteAsync(string operation, string resourceType, Guid? resourceId, AuditOutcome outcome, CancellationToken cancellationToken) =>
        _audit.WriteAsync(new AuditEntry(operation, resourceType, resourceId?.ToString(), outcome), cancellationToken);
}
