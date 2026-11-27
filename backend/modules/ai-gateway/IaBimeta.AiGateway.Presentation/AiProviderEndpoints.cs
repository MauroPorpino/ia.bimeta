using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.AiGateway.Presentation;

/// <summary>O provedor e o modelo escolhidos como padrão. O tenant NUNCA vem no corpo: é o do contexto confiável.</summary>
public sealed record SetDefaultBody(string? ProviderId, string? ModelId);

/// <summary>A credencial enviada: só entra aqui e sai do processo cifrada; nenhuma resposta a devolve.</summary>
public sealed record SetCredentialBody(string? ApiKey)
{
    public override string ToString() => "SetCredentialBody { ApiKey = [oculto] }";
}

public sealed record ConnectivityTestBody(string? ModelId);

public sealed record GenerationTestBody(string? ModelId, bool Confirmed);

public static class AiProviderEndpoints
{
    /// <summary>
    /// A API administrativa dos provedores de IA do tenant (Etapa 05). Todas as rotas exigem o contexto confiável criado pelo host a partir do token de sessão (401 sem ele) e a permissão específica de cada operação (403, aplicada
    /// no Core). Nenhuma rota aceita tenant em corpo, query ou rota; as respostas são DTOs próprios (nunca entidades) e NUNCA trazem o valor de uma credencial, endereço de servidor, prompt nem resposta de cliente. Não existe rota para
    /// informar URL, identificador livre de modelo, preço ou limite: o tenant só escolhe entre provedores e modelos do catálogo da plataforma.
    /// </summary>
    public static IEndpointRouteBuilder MapAiProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/ai").AddEndpointFilter(TratarErrosDeIa);

        grupo.MapGet("/settings", async (AiProviderAdminService servico, CancellationToken ct) => Results.Ok(await servico.GetSettingsAsync(ct)));

        grupo.MapPut("/settings/default", async (SetDefaultBody corpo, AiProviderAdminService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetDefaultAsync(corpo.ProviderId ?? string.Empty, corpo.ModelId ?? string.Empty, ct)));

        grupo.MapPut("/providers/{providerId}/credential", async (string providerId, SetCredentialBody corpo, AiProviderAdminService servico, CancellationToken ct) =>
        {
            await servico.SetCredentialAsync(providerId, corpo.ApiKey ?? string.Empty, ct);
            return Results.NoContent();
        });

        grupo.MapDelete("/providers/{providerId}/credential", async (string providerId, AiProviderAdminService servico, CancellationToken ct) =>
            Results.Ok(await servico.RevokeCredentialAsync(providerId, ct)));

        grupo.MapPost("/providers/{providerId}/connectivity-test", async (string providerId, ConnectivityTestBody? corpo, AiProviderAdminService servico, CancellationToken ct) =>
            Results.Ok(await servico.TestConnectivityAsync(providerId, corpo?.ModelId, ct)));

        grupo.MapPost("/providers/{providerId}/generation-test", async (string providerId, GenerationTestBody corpo, AiProviderAdminService servico, CancellationToken ct) =>
            Results.Ok(await servico.TestGenerationAsync(providerId, corpo.ModelId ?? string.Empty, corpo.Confirmed, ct)));

        return app;
    }

    private static async ValueTask<object?> TratarErrosDeIa(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
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
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação de inteligência artificial.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AiAdminRefusedException ex)
        {
            if (ex.RetryAfter is { } espera)
            {
                contexto.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(espera.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var status = ex.Kind switch
            {
                AiAdminRefusalKind.NotFound => StatusCodes.Status404NotFound,
                AiAdminRefusalKind.Conflict => StatusCodes.Status409Conflict,
                AiAdminRefusalKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Problem(title: "Operação recusada", detail: ex.Message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (AiGatewayValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["code"] = "invalid" });
        }
        catch (AiConfigurationConflictException ex)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = "config-changed" });
        }
        catch (AiSecretsUnavailableException)
        {
            return Results.Problem(
                title: "Armazenamento de credenciais indisponível",
                detail: "O armazenamento seguro de credenciais não está disponível neste ambiente.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "secrets-unavailable" });
        }
    }
}
