using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Audit.Presentation;

public static class AuditEndpoints
{
    /// <summary>
    /// Consulta da trilha de auditoria do tenant, a mais recente primeiro. Exige contexto confiável e a permissão
    /// de auditoria de escopo do tenant. Não há rota para alterar ou excluir eventos.
    /// </summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/audit-events").AddEndpointFilter(TratarErrosDaAuditoria);

        grupo.MapGet("/", async (int? take, string? operation, string? resourceId, AuditQueryService servico, CancellationToken ct) =>
        {
            var eventos = await servico.ListAsync(take, operation, resourceId, ct);
            return Results.Ok(eventos.Select(AuditEventResponse.From));
        });

        return app;
    }

    private static async ValueTask<object?> TratarErrosDaAuditoria(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        try
        {
            return await proximo(contexto);
        }
        catch (TenantContextRequiredException)
        {
            return Results.Problem(
                title: "Contexto de tenant ausente",
                detail: "Esta operação exige um contexto de tenant confiável.",
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (AccessDeniedException)
        {
            return Results.Problem(
                title: "Acesso negado",
                detail: "Você não tem permissão para consultar a auditoria do tenant.",
                statusCode: StatusCodes.Status403Forbidden);
        }
    }
}

public sealed record AuditEventResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorOrigin,
    string Operation,
    string ResourceType,
    string? ResourceId,
    string Outcome)
{
    public static AuditEventResponse From(AuditEventView evento) =>
        new(evento.Id, evento.OccurredAt, evento.ActorUserId, evento.ActorOrigin, evento.Operation, evento.ResourceType, evento.ResourceId, evento.Outcome.ToString());
}
