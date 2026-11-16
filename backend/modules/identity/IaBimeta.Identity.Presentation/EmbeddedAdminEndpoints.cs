using IaBimeta.Identity.Application;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Identity.Presentation;

/// <summary>
/// Administração das conexões de integração do Embedded (ADR 0039): só quem tem <c>EmbedManage</c> (concessão de escopo do tenant) e uma sessão INTERATIVA. Essas rotas NÃO estão na lista de rotas permitidas à sessão
/// embutida. O tenant vem do contexto confiável, nunca da rota ou do corpo. O segredo de uma chave só aparece na resposta da criação.
/// </summary>
internal static class EmbeddedAdminEndpoints
{
    public static IEndpointRouteBuilder MapEmbeddedAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var conexoes = app.MapGroup("/embedded/connections").AddEndpointFilter(IdentityEndpoints.TratarErrosDoIdentity);

        conexoes.MapPost("/", async (CreateConnectionRequest pedido, IntegrationAdminService servico, CancellationToken ct) =>
        {
            if (!TryModes(pedido.Mode, pedido.Provisioning, out var modo, out var provisionamento))
            {
                return Invalid();
            }

            var criada = await servico.CreateConnectionAsync(pedido.OrganizationId, pedido.Name, modo, provisionamento, pedido.AllowedOrigins, pedido.AllowStartConversation, ct);
            return Results.Created($"/embedded/connections/{criada.Id}", criada);
        });

        conexoes.MapGet("/", async (IntegrationAdminService servico, CancellationToken ct) => Results.Ok(await servico.ListConnectionsAsync(ct)));

        conexoes.MapGet("/{id:guid}", async (Guid id, IntegrationAdminService servico, CancellationToken ct) => Results.Ok(await servico.GetConnectionAsync(id, ct)));

        conexoes.MapPut("/{id:guid}", async (Guid id, UpdateConnectionRequest pedido, IntegrationAdminService servico, CancellationToken ct) =>
        {
            if (!TryModes(pedido.Mode, pedido.Provisioning, out var modo, out var provisionamento))
            {
                return Invalid();
            }

            return Results.Ok(await servico.UpdateConnectionAsync(id, pedido.Name, modo, provisionamento, pedido.AllowedOrigins, pedido.AllowStartConversation, ct));
        });

        conexoes.MapPost("/{id:guid}/disable", async (Guid id, IntegrationAdminService servico, CancellationToken ct) => Results.Ok(await servico.SetConnectionStatusAsync(id, ConnectionStatus.Disabled, ct)));

        conexoes.MapPost("/{id:guid}/enable", async (Guid id, IntegrationAdminService servico, CancellationToken ct) => Results.Ok(await servico.SetConnectionStatusAsync(id, ConnectionStatus.Active, ct)));

        // Chaves: o segredo aparece só aqui, na criação e na rotação. Nenhuma leitura o devolve.
        conexoes.MapPost("/{id:guid}/keys", async (Guid id, HttpContext http, IntegrationAdminService servico, CancellationToken ct) =>
        {
            var criada = await servico.CreateKeyAsync(id, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/embedded/connections/{id}/keys", criada);
        });

        conexoes.MapPost("/{id:guid}/keys/rotate", async (Guid id, RotateKeyRequest? pedido, HttpContext http, IntegrationAdminService servico, CancellationToken ct) =>
        {
            TimeSpan? carencia = pedido?.GraceHours is { } horas ? TimeSpan.FromHours(horas) : null;
            var criada = await servico.RotateKeyAsync(id, carencia, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/embedded/connections/{id}/keys", criada);
        });

        conexoes.MapGet("/{id:guid}/keys", async (Guid id, IntegrationAdminService servico, CancellationToken ct) => Results.Ok(await servico.ListKeysAsync(id, ct)));

        conexoes.MapPost("/{id:guid}/keys/{keyId}/revoke", async (Guid id, string keyId, IntegrationAdminService servico, CancellationToken ct) =>
        {
            await servico.RevokeKeyAsync(id, keyId, ct);
            return Results.NoContent();
        });

        conexoes.MapPut("/{id:guid}/identities", async (Guid id, MapIdentityRequest pedido, IntegrationAdminService servico, CancellationToken ct) =>
            Results.Ok(await servico.MapIdentityAsync(id, pedido.ExternalUserId, pedido.UserId, ct)));

        conexoes.MapGet("/{id:guid}/identities", async (Guid id, IntegrationAdminService servico, CancellationToken ct) => Results.Ok(await servico.ListIdentitiesAsync(id, ct)));

        conexoes.MapPost("/{id:guid}/identities/{identityId:guid}/disable", async (Guid id, Guid identityId, IntegrationAdminService servico, CancellationToken ct) =>
        {
            await servico.SetIdentityStatusAsync(id, identityId, ExternalIdentityStatus.Disabled, ct);
            return Results.NoContent();
        });

        conexoes.MapPost("/{id:guid}/identities/{identityId:guid}/enable", async (Guid id, Guid identityId, IntegrationAdminService servico, CancellationToken ct) =>
        {
            await servico.SetIdentityStatusAsync(id, identityId, ExternalIdentityStatus.Active, ct);
            return Results.NoContent();
        });

        conexoes.MapPost("/{id:guid}/sessions/revoke", async (Guid id, RevokeEmbeddedSessionsRequest? pedido, IntegrationAdminService servico, CancellationToken ct) =>
            Results.Ok(new { revoked = await servico.RevokeSessionsAsync(id, pedido?.ExternalUserId, ct) }));

        return app;
    }

    private static bool TryModes(string? mode, string? provisioning, out EmbeddedMode embeddedMode, out ProvisioningMode provisioningMode)
    {
        provisioningMode = default;
        embeddedMode = default;
        return Enum.TryParse(mode, ignoreCase: true, out embeddedMode) && Enum.IsDefined(embeddedMode)
            && Enum.TryParse(provisioning, ignoreCase: true, out provisioningMode) && Enum.IsDefined(provisioningMode);
    }

    private static IResult Invalid() =>
        Results.Problem(title: "Dados inválidos", detail: "O modo deve ser FullAttendant ou CustomerOnly, e o provisionamento MappedOnly ou JitAttendant.", statusCode: StatusCodes.Status400BadRequest);
}
