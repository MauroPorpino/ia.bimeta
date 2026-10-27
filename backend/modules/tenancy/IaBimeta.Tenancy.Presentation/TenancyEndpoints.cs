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
    /// Rotas de organizações, unidades e setores. Todas exigem contexto de tenant confiável, estabelecido pelo host;
    /// sem ele respondem 401. Nenhuma rota aceita tenant em corpo, query string ou rota, e a organização de uma
    /// unidade ou setor é a do recurso carregado, nunca um parâmetro. Recurso sem permissão de leitura responde 404;
    /// com leitura mas sem a permissão da ação, 403.
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

        organizacoes.MapPost("/{organizationId:guid}/business-units", async (Guid organizationId, CreateBusinessUnitRequest requisicao, BusinessUnitService servico, CancellationToken ct) =>
        {
            if (!Enum.TryParse<BusinessUnitKind>(requisicao.Kind, ignoreCase: true, out var tipo) || !Enum.IsDefined(tipo))
            {
                return Results.Problem(title: "Dados inválidos", detail: "O tipo da unidade deve ser Headquarters ou Branch.", statusCode: StatusCodes.Status400BadRequest);
            }

            var criada = await servico.CreateAsync(organizationId, requisicao.Name ?? string.Empty, tipo, ct);
            return Results.Created($"/business-units/{criada.Id}", BusinessUnitResponse.From(criada));
        });

        organizacoes.MapGet("/{organizationId:guid}/business-units", async (Guid organizationId, BusinessUnitService servico, CancellationToken ct) =>
        {
            var lista = await servico.ListByOrganizationAsync(organizationId, ct);
            return Results.Ok(lista.Select(BusinessUnitResponse.From));
        });

        var unidades = app.MapGroup("/business-units").AddEndpointFilter(TratarErrosDoTenancy);

        unidades.MapGet("/{id:guid}", async (Guid id, BusinessUnitService servico, CancellationToken ct) =>
        {
            var unidade = await servico.GetAsync(id, ct);
            return unidade is null ? Results.NotFound() : Results.Ok(BusinessUnitResponse.From(unidade));
        });

        unidades.MapPost("/{businessUnitId:guid}/departments", async (Guid businessUnitId, CreateDepartmentRequest requisicao, DepartmentService servico, CancellationToken ct) =>
        {
            var criado = await servico.CreateAsync(businessUnitId, requisicao.Name ?? string.Empty, ct);
            return Results.Created($"/departments/{criado.Id}", DepartmentResponse.From(criado));
        });

        unidades.MapGet("/{businessUnitId:guid}/departments", async (Guid businessUnitId, DepartmentService servico, CancellationToken ct) =>
        {
            var lista = await servico.ListByBusinessUnitAsync(businessUnitId, ct);
            return Results.Ok(lista.Select(DepartmentResponse.From));
        });

        var setores = app.MapGroup("/departments").AddEndpointFilter(TratarErrosDoTenancy);

        setores.MapGet("/{id:guid}", async (Guid id, DepartmentService servico, CancellationToken ct) =>
        {
            var setor = await servico.GetAsync(id, ct);
            return setor is null ? Results.NotFound() : Results.Ok(DepartmentResponse.From(setor));
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
        catch (AccessDeniedException)
        {
            return Results.Problem(
                title: "Acesso negado",
                detail: "Você não tem permissão para esta operação.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (TenancyResourceNotFoundException ex)
        {
            return Results.Problem(title: "Não encontrado", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (TenancyValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is DuplicateOrganizationNameException or DuplicateBusinessUnitNameException
            or HeadquartersAlreadyExistsException or DuplicateDepartmentNameException)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
