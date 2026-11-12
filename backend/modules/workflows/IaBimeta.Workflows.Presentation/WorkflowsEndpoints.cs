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

/// <summary>O corpo de uma ativação. O tenant nunca vem daqui; a organização é conferida contra as permissões do usuário.</summary>
public sealed record ActivationBody(Guid OrganizationId, Guid? BusinessUnitId, Guid? ChannelInstanceId, string? WorkflowKey, int Priority, int RolloutPercent, OnFailurePolicy OnFailure, int? ExpectedRevision)
{
    public FlowActivationRequest ToRequest(int? expectedRevision) =>
        new(OrganizationId, BusinessUnitId, ChannelInstanceId, WorkflowKey ?? string.Empty, Priority, RolloutPercent, OnFailure, expectedRevision);
}

/// <summary>O detalhe de uma execução, achatado para a tela (a instância e o caminho e os efeitos, sem variáveis nem conteúdo).</summary>
public sealed record FlowInstanceDetailBody(
    Guid Id, Guid SessionId, string WorkflowKey, int VersionNumber, FlowStatus Status, string? NodeId, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    IReadOnlyList<FlowStepView> Steps, IReadOnlyList<FlowEffectView> Effects)
{
    public static FlowInstanceDetailBody From(FlowInstanceDetailView d) =>
        new(d.Instance.Id, d.Instance.SessionId, d.Instance.WorkflowKey, d.Instance.VersionNumber, d.Instance.Status, d.Instance.NodeId, d.Instance.FailureCode, d.Instance.CreatedAt, d.Instance.EndedAt, d.Steps, d.Effects);
}

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

        // As ativações (ADR 0038): onde um fluxo publicado atende. Nascem desligadas; ligar exige versão publicada.
        var ativacoes = grupo.MapGroup("/activations");
        ativacoes.MapGet("/", async (FlowOperationsService servico, CancellationToken ct) => Results.Ok(await servico.ListActivationsAsync(ct)));
        ativacoes.MapPost("/", async (ActivationBody corpo, FlowOperationsService servico, CancellationToken ct) =>
        {
            var criada = await servico.CreateActivationAsync(corpo.ToRequest(null), ct);
            return Results.Created($"/workflows/activations/{criada.Id}", criada);
        });
        ativacoes.MapPut("/{id:guid}", async (Guid id, ActivationBody corpo, FlowOperationsService servico, CancellationToken ct) =>
            await servico.UpdateActivationAsync(id, corpo.ToRequest(corpo.ExpectedRevision), ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        ativacoes.MapPost("/{id:guid}/enable", async (Guid id, FlowOperationsService servico, CancellationToken ct) =>
            await servico.SetEnabledAsync(id, true, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        ativacoes.MapPost("/{id:guid}/disable", async (Guid id, FlowOperationsService servico, CancellationToken ct) =>
            await servico.SetEnabledAsync(id, false, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());

        // O histórico de execução e as métricas: só identificadores, estados, códigos e contagens (nunca conteúdo de cliente).
        var instancias = grupo.MapGroup("/instances");
        instancias.MapGet("/", async (FlowStatus? status, string? key, int? take, int? skip, FlowOperationsService servico, CancellationToken ct) =>
            Results.Ok(await servico.ListInstancesAsync(status, key, take ?? 25, skip ?? 0, ct)));
        instancias.MapGet("/{id:guid}", async (Guid id, FlowOperationsService servico, CancellationToken ct) =>
            await servico.GetInstanceAsync(id, ct) is { } visao ? Results.Ok(FlowInstanceDetailBody.From(visao)) : Results.NotFound());
        var metricas = grupo.MapGroup("/metrics");
        metricas.MapGet("/", async (int? days, FlowOperationsService servico, CancellationToken ct) => Results.Ok(await servico.MetricsAsync(days ?? 7, ct)));
        metricas.MapGet("/funnel", async (string key, int version, int? days, FlowOperationsService servico, CancellationToken ct) =>
            Results.Ok(await servico.FunnelAsync(key, version, days ?? 7, ct)));

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
