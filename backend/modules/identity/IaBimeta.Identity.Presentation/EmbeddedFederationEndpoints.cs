using System.Text.Json;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Identity.Presentation;

/// <summary>
/// O protocolo de federação do Embedded (ADR 0039, <c>/embed/v1</c>). Três famílias:
/// 1. Chamadas assinadas do SERVIDOR do ERP (<c>launch-tickets</c>, <c>context-tickets</c>, <c>sessions/revoke</c>): autenticadas por HMAC sobre o corpo bruto, sem sessão de usuário. O tenant vem da chave. Não criam
///    <c>RequestContext</c> de usuário nem de sistema: os serviços recebem o tenant explícito.
/// 2. A troca do ticket no iframe (<c>exchange</c>): anônima, com o verifier; limite de taxa por origem.
/// 3. Rotas da sessão embutida (<c>context</c>, <c>session</c>): contexto confiável criado pelo host a partir do token.
/// Toda recusa de autenticação é a mesma resposta; nada de ticket, token ou segredo vai a log.
/// </summary>
internal static class EmbeddedFederationEndpoints
{

    /// <summary>Corpo máximo das chamadas assinadas e da troca.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapEmbeddedFederationEndpoints(this IEndpointRouteBuilder app)
    {
        var embed = app.MapGroup("/embed/v1").AddEndpointFilter(IdentityEndpoints.TratarErrosDoIdentity);

        embed.MapPost("/launch-tickets", async (HttpContext http, IntegrationRequestAuthenticator autenticador, EmbeddedTicketService tickets, CancellationToken ct) =>
        {
            var (integracao, corpo) = await AuthenticateAsync(http, autenticador, ct);
            var pedido = Parse<LaunchTicketRequest>(corpo);
            var resultado = await tickets.IssueLaunchTicketAsync(
                integracao, new IssueLaunchTicketRequest(pedido.ExternalUserId, pedido.DisplayName, pedido.Challenge, pedido.ParentOrigin, pedido.Customer?.Ref), ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ticket = resultado.Ticket, expiresAt = resultado.ExpiresAt }, statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting(EmbeddedRateLimits.IntegrationPolicy);

        embed.MapPost("/context-tickets", async (HttpContext http, IntegrationRequestAuthenticator autenticador, EmbeddedTicketService tickets, CancellationToken ct) =>
        {
            var (integracao, corpo) = await AuthenticateAsync(http, autenticador, ct);
            var pedido = Parse<ContextTicketRequest>(corpo);
            var resultado = await tickets.IssueContextTicketAsync(integracao, pedido.ExternalUserId, pedido.Customer?.Ref, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ticket = resultado.Ticket, expiresAt = resultado.ExpiresAt }, statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting(EmbeddedRateLimits.IntegrationPolicy);

        embed.MapPost("/sessions/revoke", async (HttpContext http, IntegrationRequestAuthenticator autenticador, EmbeddedTicketService tickets, CancellationToken ct) =>
        {
            var (integracao, corpo) = await AuthenticateAsync(http, autenticador, ct);
            var pedido = Parse<RevokeByIntegrationRequest>(corpo);
            var revogadas = await tickets.RevokeSessionsAsync(integracao, pedido.ExternalUserId, ct);
            return Results.Ok(new { revoked = revogadas });
        }).RequireRateLimiting(EmbeddedRateLimits.IntegrationPolicy);

        embed.MapPost("/exchange", async (ExchangeRequest pedido, HttpContext http, EmbeddedExchangeService troca, CancellationToken ct) =>
        {
            var resultado = await troca.ExchangeAsync(pedido.Ticket, pedido.Verifier, pedido.ParentOrigin, ct);

            // O token aparece só nesta resposta: nada de guardar em cache de proxy ou de navegador.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                token = resultado.Token,
                expiresAt = resultado.ExpiresAt,
                mode = resultado.Mode,
                organizationId = resultado.OrganizationId,
                customerRef = resultado.CustomerRef,
                user = new { id = resultado.User.UserId, displayName = resultado.User.DisplayName },
            });
        }).RequireRateLimiting(EmbeddedRateLimits.ExchangePolicy);

        // Daqui para baixo, a sessão embutida (Bearer) cria o contexto; a lista de rotas permitidas do host as admite.
        embed.MapPost("/context", async (RedeemContextRequest pedido, EmbeddedExchangeService troca, CancellationToken ct) =>
            Results.Ok(new { customerRef = await troca.RedeemContextTicketAsync(pedido.Ticket, ct) }));

        embed.MapGet("/session", async (EmbeddedExchangeService troca, CancellationToken ct) =>
            await troca.GetCurrentSessionAsync(ct) is { } sessao ? Results.Ok(sessao) : Results.NotFound());

        return app;
    }

    private static async Task<(AuthenticatedIntegration Integration, byte[] Body)> AuthenticateAsync(HttpContext http, IntegrationRequestAuthenticator autenticador, CancellationToken ct)
    {
        var corpo = await ReadBodyAsync(http, ct);
        var cabecalhos = http.Request.Headers;
        var requisicao = new IntegrationSignedRequest(
            Single(cabecalhos["X-IaB-Key-Id"]),
            Single(cabecalhos["X-IaB-Timestamp"]),
            Single(cabecalhos["X-IaB-Nonce"]),
            Single(cabecalhos["X-IaB-Signature"]),
            http.Request.Method,
            http.Request.Path.Value ?? string.Empty,
            corpo);
        return (await autenticador.AuthenticateAsync(requisicao, ct), corpo);
    }

    /// <summary>Um cabeçalho repetido é recusado (devolve <c>null</c>): a assinatura não pode ser ambígua.</summary>
    private static string? Single(Microsoft.Extensions.Primitives.StringValues valores) => valores.Count == 1 ? valores[0] : null;

    private static async Task<byte[]> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is { } tamanho && tamanho > MaxBodyBytes)
        {
            throw new IdentityValidationException("Corpo grande demais.");
        }

        using var memoria = new MemoryStream();
        var buffer = new byte[2048];
        int lidos;
        while ((lidos = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (memoria.Length + lidos > MaxBodyBytes)
            {
                throw new IdentityValidationException("Corpo grande demais.");
            }

            memoria.Write(buffer, 0, lidos);
        }

        return memoria.ToArray();
    }

    private static T Parse<T>(byte[] corpo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(corpo, JsonOptions) ?? throw new IdentityValidationException("Corpo inválido.");
        }
        catch (JsonException)
        {
            throw new IdentityValidationException("Corpo inválido.");
        }
    }
}
