using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Development;

/// <summary>
/// SOMENTE DESENVOLVIMENTO, e SIMULADOR. Estas rotas chamam, em nome do Channels e do Agents, os comandos do Conversations
/// para que o núcleo possa ser exercitado à mão: simulam a chegada de uma mensagem do cliente, um turno de IA e a resposta.
/// NÃO há WhatsApp nem modelo de IA aqui, e nada disto é integração real: toda resposta leva o cabeçalho
/// <c>X-Simulator: true</c>. Só existem se <c>Tenancy:Development:SimulatorEnabled</c> estiver ligado, e a partida falha
/// fora do ambiente Development (<see cref="DevelopmentOnlyGuard"/>). Funcionam com o contexto de sistema do cabeçalho
/// <c>X-Dev-Tenant</c>: uma sessão de usuário nunca chama estes comandos (<see cref="Permission.SystemOperation"/>).
/// </summary>
internal static class SimulatorEndpoints
{
    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/simulator").AddEndpointFilter(MarcarETratar);

        grupo.MapPost("/inbound", async (SimulatedInbound corpo, IConversationIngestion ingestao, CancellationToken ct) =>
        {
            var comando = new IngestInboundMessageCommand(
                corpo.ChannelInstanceId ?? SimulatedChannel.Id,
                corpo.ContactRef ?? string.Empty,
                corpo.ContactName,
                corpo.OrganizationId,
                corpo.BusinessUnitId,
                corpo.ProviderEventId ?? $"simulado-{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                MessageKind.Text,
                corpo.Text,
                ParseLocale(corpo.ContactLocale),
                ParseLocale(corpo.MessageLocale),
                corpo.DepartmentId,
                ParseLocale(corpo.ChannelDefaultLocale),
                corpo.InitialHandlingMode ?? HandlingMode.AiAutonomous);
            return Results.Ok(await ingestao.IngestInboundMessageAsync(comando, ct));
        });

        grupo.MapPost("/turns/begin", async (BeginTurnCommand comando, ITurnCommands turnos, CancellationToken ct) =>
            Results.Ok(await turnos.BeginTurnAsync(comando, ct)));

        grupo.MapPost("/turns/reply", async (RegisterAiReplyCommand comando, ITurnCommands turnos, CancellationToken ct) =>
            Results.Ok(await turnos.RegisterAiReplyAsync(comando, ct)));

        grupo.MapPost("/turns/end", async (EndTurnCommand comando, ITurnCommands turnos, CancellationToken ct) =>
            Results.Ok(await turnos.EndTurnAsync(comando, ct)));

        grupo.MapPost("/turns/cancel", async (CancelTurnCommand comando, ITurnCommands turnos, CancellationToken ct) =>
            Results.Ok(await turnos.CancelTurnAsync(comando, ct)));

        grupo.MapPost("/handling/transition", async (TransitionHandlingModeCommand comando, IHandlingCommands atendimento, CancellationToken ct) =>
            Results.Ok(await atendimento.TransitionHandlingModeAsync(comando, ct)));

        grupo.MapPost("/delivery", async (RecordDeliveryStatusCommand comando, IConversationIngestion ingestao, CancellationToken ct) =>
            Results.Ok(await ingestao.RecordDeliveryStatusAsync(comando, ct)));

        // Idioma: o contexto de sistema relata a escolha do próprio cliente (origem CustomerChoice).
        grupo.MapPost("/handling/locale", async (SetConversationLocaleCommand comando, IHandlingCommands atendimento, CancellationToken ct) =>
            Results.Ok(await atendimento.SetConversationLocaleAsync(comando, ct)));

        grupo.MapPost("/handling/contact-locale", async (SetContactPreferredLocaleCommand comando, IHandlingCommands atendimento, CancellationToken ct) =>
            Results.Ok(await atendimento.SetContactPreferredLocaleAsync(comando, ct)));

        // Encaminhamento entre unidades (filiais) e setores, com rastro. No simulador quem pede é o contexto de sistema.
        grupo.MapPost("/handling/transfer", async (TransferSessionCommand comando, IHandlingCommands atendimento, CancellationToken ct) =>
            Results.Ok(await atendimento.TransferSessionAsync(comando, ct)));

        // Uma tradução guardada à parte, como um serviço de tradução futuro faria. Não há tradução aqui: o texto vem pronto.
        grupo.MapPost("/translations", async (RecordMessageTranslationCommand comando, IConversationIngestion ingestao, CancellationToken ct) =>
            Results.Ok(await ingestao.RecordMessageTranslationAsync(comando, ct)));

        // Os eventos publicados (dublê em memória, não durável) do tenant do contexto, para ver o que o Agents e o Channels receberiam.
        grupo.MapGet("/events", (InMemoryEventPublisher eventos, IRequestContextAccessor contexto) =>
        {
            var tenant = contexto.GetRequired().TenantId;
            return Results.Ok(eventos.Published.Where(e => e.TenantId == tenant).Select(e => new { e.Type, e.Version, e.OrderingKey, e.OccurredAt, e.Payload }));
        });

        return app;
    }

    /// <summary>Lê o idioma BCP 47 do corpo (vazio é "não informado"); um idioma mal formado vira 400 pelo filtro do grupo.</summary>
    private static Locale? ParseLocale(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        return Locale.TryParse(texto, out var locale)
            ? locale
            : throw new ConversationsValidationException("O idioma deve ser um identificador BCP 47 bem formado, como pt-BR, en-US, es-ES ou fr-FR.");
    }

    private static async ValueTask<object?> MarcarETratar(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        contexto.HttpContext.Response.Headers["X-Simulator"] = "true";

        try
        {
            return await proximo(contexto);
        }
        catch (Exception ex) when (ex is TenantContextRequiredException or AuthenticationRequiredException)
        {
            return Results.Problem(title: "Contexto ausente", detail: "O simulador exige o cabeçalho X-Dev-Tenant.", statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (AccessDeniedException)
        {
            return Results.Problem(title: "Acesso negado", detail: "Estes comandos são de máquina: só o contexto de sistema do simulador os chama.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ConversationsValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ChannelUnitNotAuthorizedException ex)
        {
            // O simulador usa a mesma autorização real do Channels: para criar uma sessão com unidade, o número precisa existir no
            // tenant (POST /channels/...) e ter a unidade entre as autorizadas. Não há atalho de desenvolvimento para isso.
            return Results.Problem(title: "Unidade não autorizada para o canal", detail: ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}

/// <summary>A instância de canal simulada (usada quando o corpo não informa uma): um identificador fixo, que não corresponde a nenhum número real.</summary>
internal static class SimulatedChannel
{
    public static readonly ChannelInstanceId Id = new(new Guid("00000000-0000-7000-8000-000000000001"));
}

internal sealed record SimulatedInbound(
    ChannelInstanceId? ChannelInstanceId,
    string? ContactRef,
    string? ContactName,
    Guid OrganizationId,
    Guid? BusinessUnitId,
    string? Text,
    string? ProviderEventId,
    string? ContactLocale = null,
    string? MessageLocale = null,
    Guid? DepartmentId = null,
    string? ChannelDefaultLocale = null,
    HandlingMode? InitialHandlingMode = null);
