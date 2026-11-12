using System.Text.Json;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Workflows.Presentation;

/// <summary>O corpo do editor: o grafo entra como JSON (lido de forma ESTRITA pelo servidor), nunca como objeto já confiável.</summary>
public sealed record SaveWorkflowBody(Guid OrganizationId, string? Key, string? Name, string? Description, JsonElement Graph, int? ExpectedRevision);

public sealed record ValidateBody(Guid OrganizationId, JsonElement Graph);

public sealed record SimulateBody(Guid OrganizationId, JsonElement Graph, SimulationScenario? Scenario);

public static class WorkflowsEndpoints
{
    /// <summary>
    /// A API dos fluxos do chatbot visual (ADR 0037). Todas as rotas exigem o contexto confiável do host (sem ele, 401) e a permissão do passo (403 sem ela): ler, editar, publicar e simular são permissões distintas,
    /// conferidas sobre a organização DO FLUXO. Nenhuma rota aceita tenant em corpo, consulta ou rota; um recurso de outro tenant responde 404. Nenhuma resposta traz conteúdo de cliente.
    /// </summary>
    public static IEndpointRouteBuilder MapWorkflowsEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/workflows").AddEndpointFilter(TratarErros);

        var definicoes = grupo.MapGroup("/definitions");
        definicoes.MapGet("/", async (WorkflowDefinitionService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));
        definicoes.MapGet("/{id:guid}", async (Guid id, WorkflowDefinitionService servico, CancellationToken ct) =>
            await servico.GetAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapGet("/by-key/{key}/versions", async (string key, WorkflowDefinitionService servico, CancellationToken ct) => Results.Ok(await servico.VersionsAsync(key, ct)));
        definicoes.MapPost("/", async (SaveWorkflowBody corpo, WorkflowDefinitionService servico, CancellationToken ct) =>
            Results.Ok(await servico.SaveDraftAsync(new SaveWorkflowRequest(corpo.OrganizationId, corpo.Key ?? string.Empty, corpo.Name ?? string.Empty, corpo.Description, RawOf(corpo.Graph), corpo.ExpectedRevision), ct)));
        definicoes.MapPost("/{id:guid}/publish", async (Guid id, WorkflowDefinitionService servico, CancellationToken ct) =>
            await servico.PublishAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapPost("/{id:guid}/retire", async (Guid id, WorkflowDefinitionService servico, CancellationToken ct) =>
            await servico.RetireAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());

        grupo.MapPost("/validate", async (ValidateBody corpo, WorkflowDefinitionService servico, CancellationToken ct) =>
        {
            var problemas = await servico.ValidateAsync(corpo.OrganizationId, RawOf(corpo.Graph), ct);
            return Results.Ok(new { valid = problemas.Count == 0, problems = problemas });
        });
        grupo.MapPost("/simulate", async (SimulateBody corpo, WorkflowDefinitionService servico, CancellationToken ct) =>
            Results.Ok(await servico.SimulateAsync(new SimulateRequest(corpo.OrganizationId, RawOf(corpo.Graph), corpo.Scenario ?? new SimulationScenario([])), ct)));

        // O que o editor pode oferecer no nó de integração: só as operações elegíveis para fluxos da organização.
        grupo.MapGet("/operations", async (Guid organizationId, WorkflowDefinitionService servico, CancellationToken ct) => Results.Ok(await servico.OperationsAsync(organizationId, ct)));

        return app;
    }

    private static string RawOf(JsonElement graph) => graph.ValueKind == JsonValueKind.Undefined ? string.Empty : graph.GetRawText();

    private static async ValueTask<object?> TratarErros(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        try
        {
            return await proximo(contexto);
        }
        catch (Exception ex) when (ex is TenantContextRequiredException or AuthenticationRequiredException)
        {
            return Results.Problem(title: "Autenticação necessária", detail: "Esta operação exige um usuário autenticado.", statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (AccessDeniedException)
        {
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação com fluxos.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (WorkflowsValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: "Os dados enviados não são válidos.", statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["problems"] = ex.Problems });
        }
        catch (WorkflowsConflictException ex)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: ex.Code == "rate-limited" ? StatusCodes.Status429TooManyRequests : StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }
}
