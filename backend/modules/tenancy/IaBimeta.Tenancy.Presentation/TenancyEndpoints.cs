using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Tenancy.Presentation;

public static class TenancyEndpoints
{
    /// <summary>
    /// Rotas de organizações. Todas exigem contexto de tenant confiável, estabelecido pelo host; sem ele
    /// respondem 401. Nenhuma rota aceita tenant em corpo, query string ou rota.
    /// </summary>
    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder app)
    {
        var organizacoes = app.MapGroup("/organizations").AddEndpointFilter(TratarErrosDoTenancy);

        organizacoes.MapPost("/", async (CreateOrganizationRequest requisicao, OrganizationService servico, CancellationToken ct) =>
        {
            var criada = await servico.CreateAsync(requisicao.Name ?? string.Empty, ct);
            return Results.Created($"/organizations/{criada.Id}", OrganizationResponse.From(criada));
        });

        organizacoes.MapGet("/", async (OrganizationService servico, CancellationToken ct) =>
        {
            var lista = await servico.ListAsync(ct);
            return Results.Ok(lista.Select(OrganizationResponse.From));
        });

        organizacoes.MapGet("/{id:guid}", async (Guid id, OrganizationService servico, CancellationToken ct) =>
        {
            var organizacao = await servico.GetAsync(id, ct);
            return organizacao is null ? Results.NotFound() : Results.Ok(OrganizationResponse.From(organizacao));
        });

        return app;
    }

    private static async ValueTask<object?> TratarErrosDoTenancy(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
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
        catch (TenancyValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DuplicateOrganizationNameException ex)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
