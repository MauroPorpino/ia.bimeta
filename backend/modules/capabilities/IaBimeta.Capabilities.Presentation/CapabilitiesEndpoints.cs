using System.Text.Json;
using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Capabilities.Presentation;

/// <summary>O segredo da conexão: SÓ entra por aqui e nunca volta. <see cref="ExpectedRevision"/> evita sobrescrever uma alteração que a pessoa ainda não viu.</summary>
public sealed record SecretBody(Dictionary<string, string>? Values, int ExpectedRevision);

public sealed record EnabledBody(bool Enabled);

/// <summary>Os valores dos parâmetros de um teste ou de uma validação. O esquema vem da operação, nunca do corpo.</summary>
public sealed record ParametersBody(Dictionary<string, JsonElement>? Parameters);

/// <summary>Executar uma operação como pessoa (administração ou tela do produto). Quem chama é sempre <c>Operator</c>: a origem não vem do corpo.</summary>
public sealed record ExecuteBody(string? Key, Dictionary<string, JsonElement>? Parameters, string? IdempotencyKey, Guid? ServiceSessionId);

public static class CapabilitiesEndpoints
{
    /// <summary>
    /// A API do motor de integração (ADR 0035). Todas as rotas exigem o contexto confiável criado pelo host a partir do token de sessão (sem ele, 401) e a permissão do passo (403 sem ela): ler, administrar, testar,
    /// executar leitura e executar ou confirmar escrita são permissões distintas, conferidas sobre a organização DA CONEXÃO. Nenhuma rota aceita tenant em corpo, consulta ou rota: o tenant é o do contexto, e um
    /// recurso de outro tenant responde 404. Nenhuma resposta traz segredo, parâmetro guardado nem corpo de sistema externo (só os campos mapeados do resultado, marcados como não confiáveis).
    /// </summary>
    public static IEndpointRouteBuilder MapCapabilitiesEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/capabilities").AddEndpointFilter(TratarErros);

        // ------------------------------------------------------------ conexões
        var conexoes = grupo.MapGroup("/connections");
        conexoes.MapGet("/", async (ConnectionService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));
        conexoes.MapGet("/{id:guid}", async (Guid id, ConnectionService servico, CancellationToken ct) =>
            await servico.GetAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        conexoes.MapPost("/", async (ConnectionRequest corpo, ConnectionService servico, CancellationToken ct) =>
        {
            var criada = await servico.CreateAsync(corpo, ct);
            return Results.Created($"/capabilities/connections/{criada.Id}", criada);
        });
        conexoes.MapPut("/{id:guid}", async (Guid id, ConnectionUpdate corpo, ConnectionService servico, CancellationToken ct) =>
            await servico.UpdateAsync(id, corpo, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        conexoes.MapPut("/{id:guid}/secret", async (Guid id, SecretBody corpo, ConnectionService servico, CancellationToken ct) =>
            await servico.SetSecretAsync(id, new ConnectionSecretRequest(corpo.Values ?? [], corpo.ExpectedRevision), ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        conexoes.MapPost("/{id:guid}/enable", async (Guid id, ConnectionService servico, CancellationToken ct) =>
            await servico.SetEnabledAsync(id, true, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        conexoes.MapPost("/{id:guid}/disable", async (Guid id, ConnectionService servico, CancellationToken ct) =>
            await servico.SetEnabledAsync(id, false, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        conexoes.MapPost("/{id:guid}/test", async (Guid id, ConnectionService servico, CancellationToken ct) =>
            await servico.TestAsync(id, ct) is { } resultado ? Results.Ok(resultado) : Results.NotFound());

        // ------------------------------------------------------------ operações
        var definicoes = grupo.MapGroup("/definitions");
        definicoes.MapGet("/", async (Guid? connectionId, DefinitionService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(connectionId, ct)));
        definicoes.MapGet("/{id:guid}", async (Guid id, DefinitionService servico, CancellationToken ct) =>
            await servico.GetAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapGet("/by-key/{key}/versions", async (string key, DefinitionService servico, CancellationToken ct) => Results.Ok(await servico.VersionsAsync(key, ct)));
        definicoes.MapPost("/", async (DefinitionRequest corpo, DefinitionService servico, CancellationToken ct) => Results.Ok(await servico.SaveDraftAsync(corpo, ct)));
        definicoes.MapPost("/{id:guid}/publish", async (Guid id, DefinitionService servico, CancellationToken ct) =>
            await servico.PublishAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapPost("/{id:guid}/retire", async (Guid id, DefinitionService servico, CancellationToken ct) =>
            await servico.RetireAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapPost("/{id:guid}/enable", async (Guid id, DefinitionService servico, CancellationToken ct) =>
            await servico.SetEnabledAsync(id, true, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapPost("/{id:guid}/disable", async (Guid id, DefinitionService servico, CancellationToken ct) =>
            await servico.SetEnabledAsync(id, false, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        definicoes.MapPost("/{id:guid}/test", async (Guid id, ParametersBody corpo, CapabilityExecutionService motor, CancellationToken ct) =>
            ResultOf(await motor.TestOperationAsync(id, corpo.Parameters ?? [], ct)));
        definicoes.MapPost("/{id:guid}/validate", async (Guid id, ParametersBody corpo, CapabilityExecutionService motor, CancellationToken ct) =>
            await motor.ValidateParametersAsync(id, corpo.Parameters ?? [], ct) is { } problemas ? Results.Ok(new { valid = problemas.Count == 0, problems = problemas }) : Results.NotFound());

        // ------------------------------------------------------------ execuções
        var execucoes = grupo.MapGroup("/executions");
        execucoes.MapGet("/", async (Guid? connectionId, string? key, CapabilityExecutionStatus? status, bool? isTest, int? take, int? skip, ExecutionHistoryService servico, CancellationToken ct) =>
            Results.Ok(await servico.ListAsync(connectionId, key, status, isTest, take ?? 50, skip ?? 0, ct)));
        execucoes.MapGet("/stats", async (int? days, ExecutionHistoryService servico, CancellationToken ct) => Results.Ok(await servico.StatsAsync(days ?? 7, ct)));
        execucoes.MapGet("/{id:guid}", async (Guid id, ExecutionHistoryService servico, CancellationToken ct) =>
            await servico.GetAsync(id, ct) is { } visao ? Results.Ok(visao) : Results.NotFound());
        execucoes.MapPost("/", async (ExecuteBody corpo, CapabilityExecutionService motor, CancellationToken ct) =>
            ResultOf(await motor.ExecuteAsync(new CapabilityInvocation(corpo.Key ?? string.Empty, corpo.Parameters ?? [], CapabilityCaller.Operator, corpo.IdempotencyKey, corpo.ServiceSessionId), ct)));
        execucoes.MapPost("/{id:guid}/confirm", async (Guid id, CapabilityExecutionService motor, CancellationToken ct) => ResultOf(await motor.ConfirmAsync(id, ct)));
        execucoes.MapPost("/{id:guid}/cancel", async (Guid id, CapabilityExecutionService motor, CancellationToken ct) => ResultOf(await motor.CancelAsync(id, ct)));

        // O que uma pessoa pode escolher executar agora (publicadas, habilitadas, liberadas ao operador e dentro das permissões dela).
        grupo.MapGet("/catalog", async (CapabilityExecutionService motor, CancellationToken ct) => Results.Ok(await motor.ListAsync(CapabilityCaller.Operator, ct)));

        return app;
    }

    /// <summary>O resultado do motor em HTTP: a recusa de negócio vira o status que a descreve; o desfecho de uma chamada que saiu (sucesso, falha, ambíguo) é 200 com o resultado no corpo; escrita pendente é 202.</summary>
    private static IResult ResultOf(CapabilityResult resultado)
    {
        var status = resultado.Status switch
        {
            CapabilityExecutionStatus.AwaitingConfirmation => StatusCodes.Status202Accepted,
            CapabilityExecutionStatus.InProgress => StatusCodes.Status409Conflict,
            CapabilityExecutionStatus.Rejected => resultado.Error?.Code switch
            {
                "not-found" => StatusCodes.Status404NotFound,
                "validation-failed" or "idempotency-required" => StatusCodes.Status400BadRequest,
                "caller-not-allowed" => StatusCodes.Status403Forbidden,
                "rate-limited" => StatusCodes.Status429TooManyRequests,
                "idempotency-conflict" or "disabled" or "not-pending" or "module-disabled" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status422UnprocessableEntity,
            },
            _ => StatusCodes.Status200OK,
        };

        return Results.Json(resultado, statusCode: status);
    }

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
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação de integração.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (CapabilitiesValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: "Os dados enviados não são válidos.", statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["problems"] = ex.Problems });
        }
        catch (CapabilitiesConflictException ex)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (CapabilitiesUnavailableException ex)
        {
            return Results.Problem(title: "Recurso indisponível", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }
}
