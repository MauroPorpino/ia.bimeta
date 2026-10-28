using IaBimeta.Channels.Application;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Channels.Presentation;

public static class ChannelsEndpoints
{
    /// <summary>
    /// Cadastro de contas, números (instâncias de canal), unidades autorizadas, padrões e credenciais. Todas as rotas exigem o
    /// contexto confiável que o host cria a partir do token de sessão (sem ele, 401) e a permissão sobre a organização do
    /// recurso. Nenhuma rota aceita tenant em corpo, query string ou rota, e um recurso fora do alcance do usuário responde
    /// 404. Nenhuma resposta traz valor de credencial: a credencial é só de escrita. O webhook da Meta, o Receiver e o envio
    /// não existem aqui (BOUNDARIES.md PRV-2).
    /// </summary>
    public static IEndpointRouteBuilder MapChannelsEndpoints(this IEndpointRouteBuilder app)
    {
        var contas = app.MapGroup("/channels/accounts").AddEndpointFilter(TratarErrosDeCanais);

        contas.MapPost("/", async (CreateChannelAccountRequest pedido, ChannelAccountService servico, CancellationToken ct) =>
        {
            var conta = await servico.CreateAsync(pedido.OrganizationId, pedido.Provider, pedido.ProviderAccountId, pedido.Name, ct);
            return Results.Created($"/channels/accounts/{conta.Id}", conta);
        });

        contas.MapGet("/", async (ChannelAccountService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));

        contas.MapGet("/{id:guid}", async (Guid id, ChannelAccountService servico, CancellationToken ct) =>
        {
            var conta = await servico.GetAsync(id, ct);
            return conta is null ? Results.NotFound() : Results.Ok(conta);
        });

        // Só escrita: define (ou gira) a credencial e devolve os metadados, nunca o valor.
        contas.MapPut("/{id:guid}/secrets/{kind}", async (Guid id, string kind, SetChannelSecretRequest pedido, ChannelAccountService servico, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ChannelSecretKind>(kind, ignoreCase: true, out var papel) || !Enum.IsDefined(papel))
            {
                return Results.Problem(title: "Dados inválidos", detail: "O papel da credencial é inválido.", statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(await servico.SetSecretAsync(id, papel, pedido.Value, ct));
        });

        var instancias = app.MapGroup("/channels/instances").AddEndpointFilter(TratarErrosDeCanais);

        instancias.MapPost("/", async (RegisterChannelInstanceRequest pedido, ChannelInstanceService servico, CancellationToken ct) =>
        {
            var instancia = await servico.RegisterAsync(pedido.AccountId, pedido.Name, pedido.PhoneNumberId, pedido.DisplayPhoneNumber, pedido.DefaultLocale, ct);
            return Results.Created($"/channels/instances/{instancia.Id}", instancia);
        });

        instancias.MapGet("/", async (ChannelInstanceService servico, CancellationToken ct) => Results.Ok(await servico.ListAsync(ct)));

        instancias.MapGet("/{id:guid}", async (Guid id, ChannelInstanceService servico, CancellationToken ct) =>
        {
            var instancia = await servico.GetAsync(id, ct);
            return instancia is null ? Results.NotFound() : Results.Ok(instancia);
        });

        // Associação entre o número e a unidade: idempotente (repetir não muda nada) e auditada.
        instancias.MapPut("/{id:guid}/authorized-units/{unitId:guid}", async (Guid id, Guid unitId, ChannelInstanceService servico, CancellationToken ct) =>
            Results.Ok(await servico.AuthorizeUnitAsync(id, unitId, ct)));

        instancias.MapDelete("/{id:guid}/authorized-units/{unitId:guid}", async (Guid id, Guid unitId, ChannelInstanceService servico, CancellationToken ct) =>
            Results.Ok(await servico.RevokeUnitAsync(id, unitId, ct)));

        instancias.MapPut("/{id:guid}/defaults", async (Guid id, SetChannelDefaultsRequest pedido, ChannelInstanceService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetDefaultsAsync(id, pedido.DefaultBusinessUnitId, pedido.DefaultDepartmentId, ct)));

        instancias.MapPut("/{id:guid}/default-locale", async (Guid id, SetChannelDefaultLocaleRequest pedido, ChannelInstanceService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetDefaultLocaleAsync(id, pedido.DefaultLocale, ct)));

        instancias.MapPost("/{id:guid}/activate", async (Guid id, ChannelInstanceService servico, CancellationToken ct) =>
            Results.Ok(await servico.ActivateAsync(id, ct)));

        instancias.MapPost("/{id:guid}/suspend", async (Guid id, ChannelInstanceService servico, CancellationToken ct) =>
            Results.Ok(await servico.SuspendAsync(id, ct)));

        return app;
    }

    private static async ValueTask<object?> TratarErrosDeCanais(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
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
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação sobre canais.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ChannelsResourceNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ChannelsValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is ChannelIdentifierInUseException or ChannelsConflictException)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ChannelSecretsNotConfiguredException)
        {
            // O detalhe (qual chave falta) é de operação: não vai na resposta ao cliente.
            return Results.Problem(
                title: "Armazenamento de credenciais indisponível",
                detail: "O armazenamento de credenciais não está configurado neste ambiente.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
