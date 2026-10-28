using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.ContactCenter.Presentation;

public static class ContactCenterEndpoints
{
    /// <summary>
    /// Filas de atendimento e atendentes de cada fila. Todas as rotas exigem o contexto confiável que o host cria a partir do
    /// token de sessão (sem ele, 401) e a permissão sobre a organização do recurso. Nenhuma rota aceita tenant em corpo, query
    /// string ou rota, e um recurso fora do alcance do usuário responde 404. O roteamento, a atribuição e o handoff IA/humano
    /// não existem aqui ainda.
    /// </summary>
    public static IEndpointRouteBuilder MapContactCenterEndpoints(this IEndpointRouteBuilder app)
    {
        var filas = app.MapGroup("/contact-center/queues").AddEndpointFilter(TratarErrosDoContactCenter);

        filas.MapPost("/", async (CreateServiceQueueRequest pedido, ServiceQueueService servico, CancellationToken ct) =>
        {
            var fila = await servico.CreateAsync(pedido.OrganizationId, pedido.BusinessUnitId, pedido.DepartmentId, pedido.Name, ct);
            return Results.Created($"/contact-center/queues/{fila.Id}", fila);
        });

        filas.MapGet("/", async (ServiceQueueService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));

        filas.MapGet("/{id:guid}", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
        {
            var fila = await servico.GetAsync(id, ct);
            return fila is null ? Results.NotFound() : Results.Ok(fila);
        });

        filas.MapPost("/{id:guid}/pause", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
            Results.Ok(await servico.PauseAsync(id, ct)));

        filas.MapPost("/{id:guid}/resume", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
            Results.Ok(await servico.ResumeAsync(id, ct)));

        filas.MapGet("/{id:guid}/members", async (Guid id, ServiceQueueService servico, CancellationToken ct) =>
        {
            var membros = await servico.ListMembersAsync(id, ct);
            return membros is null ? Results.NotFound() : Results.Ok(membros);
        });

        // Idempotente: repetir o mesmo papel não muda nada, e outro papel troca o que já existe.
        filas.MapPut("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, SetQueueMemberRequest pedido, ServiceQueueService servico, CancellationToken ct) =>
            Results.Ok(await servico.AddMemberAsync(id, userId, pedido.Role, ct)));

        filas.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, ServiceQueueService servico, CancellationToken ct) =>
        {
            await servico.RemoveMemberAsync(id, userId, ct);
            return Results.NoContent();
        });

        return app;
    }

    private static async ValueTask<object?> TratarErrosDoContactCenter(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        try
        {
            return await proximo(contexto);
        }
        catch (Exception ex) when (ex is TenantContextRequiredException or AuthenticationRequiredException)
        {
            return Results.Problem(
                title: "Autenticação necessária",
                detail: "Esta operação exige um usuário autenticado.",
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (AccessDeniedException)
        {
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação sobre filas.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ContactCenterResourceNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ContactCenterValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is QueueNameInUseException or ContactCenterConflictException)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
