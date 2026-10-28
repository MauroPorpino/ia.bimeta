using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Conversations.Presentation;

public static class ConversationsEndpoints
{
    /// <summary>
    /// Leitura de sessões e do histórico. Todas as rotas exigem o contexto confiável que o host cria a partir do token de
    /// sessão (sem ele, 401) e a permissão de leitura de conversas sobre a organização da sessão. Nenhuma rota aceita
    /// tenant em corpo, query string ou rota, e uma sessão fora do alcance do usuário responde 404. Não há rota para
    /// escrever mensagem: a entrada vem do Channels e a saída do Agents, dentro do processo, e nunca de um usuário.
    /// </summary>
    public static IEndpointRouteBuilder MapConversationsEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/conversations/sessions").AddEndpointFilter(TratarErrosDeConversas);

        grupo.MapGet("/", async (int? take, string? cursor, string? status, IConversationQueries consultas, CancellationToken ct) =>
        {
            SessionStatus? estado = null;
            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var lido) || !Enum.IsDefined(lido))
                {
                    return Results.Problem(title: "Dados inválidos", detail: "O estado deve ser Open ou Closed.", statusCode: StatusCodes.Status400BadRequest);
                }

                estado = lido;
            }

            var pagina = await consultas.ListSessionsAsync(new ListSessionsQuery(take ?? ConversationLimits.DefaultPageSize, cursor, estado), ct);
            return Results.Ok(pagina);
        });

        grupo.MapGet("/{id:guid}", async (Guid id, IConversationQueries consultas, CancellationToken ct) =>
        {
            var sessao = await consultas.GetSessionAsync(id, ct);
            return sessao is null ? Results.NotFound() : Results.Ok(sessao);
        });

        grupo.MapGet("/{id:guid}/messages", async (Guid id, int? take, string? cursor, string? order, IConversationQueries consultas, CancellationToken ct) =>
        {
            var ordem = HistoryOrder.NewestFirst;
            if (!string.IsNullOrEmpty(order))
            {
                ordem = order.ToLowerInvariant() switch
                {
                    "desc" or "newest" => HistoryOrder.NewestFirst,
                    "asc" or "oldest" => HistoryOrder.OldestFirst,
                    _ => (HistoryOrder)0,
                };

                if (ordem == 0)
                {
                    return Results.Problem(title: "Dados inválidos", detail: "A ordem deve ser asc ou desc.", statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var pagina = await consultas.GetHistoryAsync(new GetHistoryQuery(id, take ?? ConversationLimits.DefaultPageSize, cursor, ordem), ct);
            return pagina is null ? Results.NotFound() : Results.Ok(pagina);
        });

        // O rastro de encaminhamentos entre unidades: só leitura, com a mesma regra de acesso da sessão.
        grupo.MapGet("/{id:guid}/transfers", async (Guid id, IConversationQueries consultas, CancellationToken ct) =>
        {
            var rastro = await consultas.GetTransfersAsync(id, ct);
            return rastro is null ? Results.NotFound() : Results.Ok(rastro);
        });

        return app;
    }

    private static async ValueTask<object?> TratarErrosDeConversas(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
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
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para ler conversas.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ConversationsValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
