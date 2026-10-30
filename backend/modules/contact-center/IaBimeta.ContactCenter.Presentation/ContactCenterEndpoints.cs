using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.ContactCenter.Presentation;

public static class ContactCenterEndpoints
{
    /// <summary>
    /// Filas de atendimento, atendentes e as conversas em atendimento. Todas as rotas exigem o contexto confiável que o host cria
    /// a partir do token de sessão (sem ele, 401). Nenhuma rota aceita tenant nem o usuário que age em corpo, query string ou
    /// rota: o tenant e o usuário são os do contexto. Um recurso fora do alcance do usuário responde 404, e enxergar sem poder
    /// operar responde 403 (e fica na auditoria). A operação que o Conversations ainda não confirmou responde 202: repetir o
    /// pedido com o mesmo <c>requestId</c> a conclui.
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

        // Política da fila: o que fazer fora do horário e sem atendente, a contingência, a prioridade padrão e o limite por atendente.
        filas.MapPut("/{id:guid}/policy", async (Guid id, QueuePolicyRequest pedido, QueuePolicyService servico, CancellationToken ct) =>
            Results.Ok(await servico.ConfigureAsync(id, pedido.FallbackQueueId, pedido.OutOfHours, pedido.NoAgent, pedido.DefaultPriority, pedido.MaxItemsPerAgent, ct)));

        // O horário de atendimento, com o fuso horário (IANA) explícito. Trocar é por inteiro; remover faz a fila atender sempre.
        filas.MapGet("/{id:guid}/schedule", async (Guid id, QueuePolicyService servico, CancellationToken ct) =>
        {
            var horario = await servico.GetScheduleAsync(id, ct);
            return horario is null ? Results.NotFound() : Results.Ok(horario);
        });

        filas.MapPut("/{id:guid}/schedule", async (Guid id, SetScheduleRequest pedido, QueuePolicyService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetScheduleAsync(
                id,
                pedido.TimeZone,
                (pedido.Windows ?? Array.Empty<ScheduleWindowRequest>()).Select(w => new ScheduleWindowInput(w.DayOfWeek, w.Start, w.End)).ToList(),
                pedido.ClosedDates ?? Array.Empty<DateOnly>(),
                ct)));

        filas.MapDelete("/{id:guid}/schedule", async (Guid id, QueuePolicyService servico, CancellationToken ct) =>
            Results.Ok(await servico.ClearScheduleAsync(id, ct)));

        filas.MapGet("/{id:guid}/availability", async (Guid id, QueuePolicyService servico, CancellationToken ct) =>
        {
            var disponibilidade = await servico.GetAvailabilityAsync(id, ct);
            return disponibilidade is null ? Results.NotFound() : Results.Ok(disponibilidade);
        });

        // O atendente assume a próxima conversa da fila (a mais urgente e, na mesma prioridade, a mais antiga). 204 se não há nenhuma.
        filas.MapPost("/{id:guid}/claim-next", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
        {
            var resultado = await servico.ClaimNextAsync(id, pedido.RequestId, pedido.Reason, ct);
            return resultado is null ? Results.NoContent() : Resultado(resultado);
        });

        var regras = app.MapGroup("/contact-center/routing-rules").AddEndpointFilter(TratarErrosDoContactCenter);

        regras.MapPost("/", async (CreateRoutingRuleRequest pedido, RoutingRuleService servico, CancellationToken ct) =>
        {
            var regra = await servico.CreateAsync(
                pedido.OrganizationId, pedido.Name, pedido.ChannelInstanceId, pedido.BusinessUnitId, pedido.DepartmentId, pedido.QueueId, pedido.Priority, pedido.Order, ct);
            return Results.Created($"/contact-center/routing-rules/{regra.Id}", regra);
        });

        regras.MapGet("/", async (RoutingRuleService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));

        regras.MapGet("/{id:guid}", async (Guid id, RoutingRuleService servico, CancellationToken ct) =>
        {
            var regra = await servico.GetAsync(id, ct);
            return regra is null ? Results.NotFound() : Results.Ok(regra);
        });

        regras.MapPost("/{id:guid}/enable", async (Guid id, RoutingRuleService servico, CancellationToken ct) => Results.Ok(await servico.EnableAsync(id, ct)));

        regras.MapPost("/{id:guid}/disable", async (Guid id, RoutingRuleService servico, CancellationToken ct) => Results.Ok(await servico.DisableAsync(id, ct)));

        regras.MapDelete("/{id:guid}", async (Guid id, RoutingRuleService servico, CancellationToken ct) =>
        {
            await servico.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        var central = app.MapGroup("/contact-center").AddEndpointFilter(TratarErrosDoContactCenter);

        // Simula o roteamento de uma conversa, sem mudar nada: os destinos que seriam tentados, na ordem.
        central.MapPost("/routing/preview", async (RoutingPreviewRequest pedido, RoutingRuleService servico, CancellationToken ct) =>
            Results.Ok(await servico.PreviewAsync(pedido.SessionId, ct)));

        // A presença do atendente: cada um informa a própria.
        central.MapGet("/me/presence", async (AgentPresenceService servico, CancellationToken ct) => Results.Ok(await servico.GetMineAsync(ct)));

        central.MapPut("/me/presence", async (SetPresenceRequest pedido, AgentPresenceService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetMineAsync(pedido.Status, ct)));

        // As filas em que o usuário atende: a participação dele é o acesso, mesmo sem permissão de ler as filas da organização.
        central.MapGet("/me/queues", async (ServiceQueueService servico, CancellationToken ct) => Results.Ok(await servico.ListMineAsync(ct)));

        // O pedido de atendimento humano (handoff). Quem o faz pela IA é o Agents, com contexto de sistema; um usuário que gere a
        // conversa também pode. A fila vem do pedido e passa pela mesma validação de unidade e de autorização.
        central.MapPost("/handoffs", async (RequestHandoffRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.RequestHandoffAsync(pedido.SessionId, pedido.QueueId, pedido.RequestId, pedido.Reason ?? "handoff", ct)));

        var itens = central.MapGroup("/items");

        itens.MapGet("/", async (Guid? queueId, string? status, bool? assignedToMe, int? take, QueueItemService servico, CancellationToken ct) =>
        {
            QueueItemStatus? situacao = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<QueueItemStatus>(status, ignoreCase: true, out var lida) || !Enum.IsDefined(lida))
                {
                    throw new ContactCenterValidationException("A situação do item é inválida.");
                }

                situacao = lida;
            }

            return Results.Ok(await servico.ListAsync(queueId, situacao, assignedToMe ?? false, take ?? 50, ct));
        });

        itens.MapGet("/{id:guid}", async (Guid id, QueueItemService servico, CancellationToken ct) =>
        {
            var item = await servico.GetAsync(id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        itens.MapGet("/{id:guid}/history", async (Guid id, QueueItemService servico, CancellationToken ct) =>
        {
            var historico = await servico.GetHistoryAsync(id, ct);
            return historico is null ? Results.NotFound() : Results.Ok(historico);
        });

        itens.MapGet("/{id:guid}/conversation", async (Guid id, QueueItemService servico, CancellationToken ct) =>
        {
            var sessao = await servico.GetConversationAsync(id, ct);
            return sessao is null ? Results.NotFound() : Results.Ok(sessao);
        });

        itens.MapGet("/{id:guid}/messages", async (Guid id, int? take, string? cursor, string? order, QueueItemService servico, CancellationToken ct) =>
        {
            var ordem = HistoryOrder.OldestFirst;
            if (!string.IsNullOrWhiteSpace(order) && (!Enum.TryParse(order, ignoreCase: true, out ordem) || !Enum.IsDefined(ordem)))
            {
                throw new ContactCenterValidationException("A ordem do histórico é inválida.");
            }

            var pagina = await servico.GetMessagesAsync(id, take ?? 50, cursor, ordem, ct);
            return pagina is null ? Results.NotFound() : Results.Ok(pagina);
        });

        itens.MapPost("/{id:guid}/claim", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.ClaimAsync(id, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/assign", async (Guid id, AssignItemRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.AssignAsync(id, pedido.UserId, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/release", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.ReleaseAsync(id, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/transfer", async (Guid id, TransferItemRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.TransferAsync(id, pedido.QueueId, pedido.UserId, pedido.RequestId, pedido.Reason, ct)));

        itens.MapPost("/{id:guid}/return-to-ai", async (Guid id, ItemActionRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.ReturnToAiAsync(id, pedido.RequestId, pedido.Reason, ct)));

        // Só quem supervisiona a fila muda a prioridade; fica no histórico.
        itens.MapPost("/{id:guid}/priority", async (Guid id, SetPriorityRequest pedido, QueueItemService servico, CancellationToken ct) =>
            Resultado(await servico.SetPriorityAsync(id, pedido.Priority, pedido.RequestId, pedido.Reason, ct)));

        return app;
    }

    // 202 enquanto o Conversations não confirmou: a intenção está registrada e repetir o pedido (mesmo requestId) a conclui.
    private static IResult Resultado(QueueItemOperationResult resultado) =>
        resultado.Outcome == OperationOutcome.Pending ? Results.Accepted(uri: null, value: resultado) : Results.Ok(resultado);

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
        catch (Exception ex) when (ex is AccessDeniedException or QueueAccessDeniedException)
        {
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ContactCenterResourceNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ContactCenterValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (QueueItemConflictException ex)
        {
            return Results.Problem(
                title: "Conflito",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Conflict.ToString() });
        }
        catch (ContactCenterRuleException ex)
        {
            return Results.Problem(
                title: "Regra não atendida",
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Rule.ToString() });
        }
        catch (Exception ex) when (ex is QueueNameInUseException or ContactCenterConflictException or QueueItemStateException)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
