using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Agents.Presentation;

/// <summary>A entrada de FAQ que o administrador montou a partir de uma lacuna. <c>AutoReply</c> é falso por padrão.</summary>
public sealed record PromoteKnowledgeGapBody(string? Question, IReadOnlyList<string>? Aliases, string? Answer, bool AutoReply = false);

public static class AgentsEndpoints
{
    /// <summary>
    /// A API administrativa do agente do tenant (ADR 0030): a revisão das lacunas do FAQ (listar, consultar com a pergunta e a resposta humana, descartar, enviar ao RASCUNHO) e o rascunho e a publicação da
    /// definição. Todas as rotas exigem o contexto confiável que o host cria a partir do token de sessão (sem ele, 401) e a permissão de configurar o tenant (<c>TenantSettingsManage</c>, 403 sem ela). Nenhuma
    /// rota aceita tenant em corpo, query string ou rota: o tenant é o do contexto, e uma lacuna de outro tenant responde 404. Nada é publicado por revisar: publicar é a rota <c>POST /agents/definition/publish</c>,
    /// que cria uma versão a partir do rascunho (a versão publicada nunca é alterada) e é auditada. Nenhuma resposta traz segredo.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentsEndpoints(this IEndpointRouteBuilder app)
    {
        var lacunas = app.MapGroup("/agents/knowledge-gaps").AddEndpointFilter(TratarErrosDoAgente);

        lacunas.MapGet("/", async (KnowledgeGapStatus? status, int? take, int? skip, KnowledgeGapService servico, CancellationToken ct) =>
            Results.Ok(await servico.ListAsync(status, take ?? 50, skip ?? 0, ct)));

        lacunas.MapGet("/{id:guid}", async (Guid id, KnowledgeGapService servico, CancellationToken ct) =>
        {
            var detalhe = await servico.GetAsync(id, ct);
            return detalhe is null ? Results.NotFound() : Results.Ok(detalhe);
        });

        lacunas.MapPost("/{id:guid}/dismiss", async (Guid id, KnowledgeGapService servico, CancellationToken ct) =>
        {
            var lacuna = await servico.DismissAsync(id, ct);
            return lacuna is null ? Results.NotFound() : Results.Ok(lacuna);
        });

        lacunas.MapPost("/{id:guid}/promote", async (Guid id, PromoteKnowledgeGapBody corpo, KnowledgeGapService servico, CancellationToken ct) =>
        {
            var resultado = await servico.PromoteAsync(id, new PromoteKnowledgeGapRequest(corpo.Question ?? string.Empty, corpo.Aliases, corpo.Answer ?? string.Empty, corpo.AutoReply), ct);
            return resultado is null ? Results.NotFound() : Results.Ok(resultado);
        });

        var definicao = app.MapGroup("/agents/definition").AddEndpointFilter(TratarErrosDoAgente);

        definicao.MapGet("/draft", async (AgentDefinitionService servico, CancellationToken ct) =>
        {
            var rascunho = await servico.GetDraftAsync(ct);
            return rascunho is null ? Results.NotFound() : Results.Ok(rascunho);
        });

        definicao.MapGet("/published", async (AgentDefinitionService servico, CancellationToken ct) =>
        {
            var publicada = await servico.GetPublishedAsync(ct);
            return publicada is null ? Results.NotFound() : Results.Ok(publicada);
        });

        // A publicação é uma ação explícita do administrador: o rascunho vira a versão que o agente usa, e a anterior é aposentada, numa transação, com auditoria.
        definicao.MapPost("/publish", async (AgentDefinitionService servico, CancellationToken ct) => Results.Ok(await servico.PublishAsync(ct)));

        return app;
    }

    private static async ValueTask<object?> TratarErrosDoAgente(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
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
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para configurar o agente desta empresa.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AgentsValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (KnowledgeGapConflictException ex)
        {
            return Results.Problem(
                title: "Conflito",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (AgentDefinitionConflictException ex)
        {
            return Results.Problem(
                title: "Conflito",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "definition-changed" });
        }
    }
}
