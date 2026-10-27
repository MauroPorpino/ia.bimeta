using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Identity.Presentation;

public static class IdentityEndpoints
{
    /// <summary>
    /// Rotas de autenticação, usuários e concessões. O login é a única rota anônima. As demais exigem o contexto
    /// confiável que o host cria a partir do token de sessão (<c>Authorization: Bearer ...</c>); sem ele respondem 401.
    /// Nenhuma rota aceita tenant em corpo, query string ou rota, e a organização da rota é validada contra as
    /// concessões do usuário: sem leitura sobre ela, 404; com leitura mas sem a permissão da ação, 403.
    /// </summary>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var autenticacao = app.MapGroup("/auth").AddEndpointFilter(TratarErrosDoIdentity);

        autenticacao.MapPost("/login", async (LoginRequest requisicao, AuthenticationService servico, HttpContext http, CancellationToken ct) =>
        {
            var resultado = await servico.LoginAsync(requisicao.Tenant, requisicao.Email, requisicao.Password, ct);

            // O token aparece só nesta resposta: nada de guardar em cache de proxy ou de navegador.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new LoginResponse(resultado.Token, resultado.ExpiresAt));
        });

        autenticacao.MapPost("/logout", async (AuthenticationService servico, CancellationToken ct) =>
        {
            await servico.LogoutAsync(ct);
            return Results.NoContent();
        });

        autenticacao.MapGet("/me", async (UserService servico, CancellationToken ct) =>
            Results.Ok(CurrentUserResponse.From(await servico.GetCurrentAsync(ct))));

        var usuarios = app.MapGroup("/organizations/{organizationId:guid}/users").AddEndpointFilter(TratarErrosDoIdentity);

        usuarios.MapPost("/", async (Guid organizationId, CreateUserRequest requisicao, UserService servico, CancellationToken ct) =>
        {
            if (!TryParseRole(requisicao.Role, out var papel))
            {
                return RoleInvalido();
            }

            var criado = await servico.CreateAsync(organizationId, requisicao.Email, requisicao.DisplayName, requisicao.Password, papel, ct);
            return Results.Created($"/organizations/{organizationId}/users", UserResponse.From(criado));
        });

        usuarios.MapGet("/", async (Guid organizationId, UserService servico, CancellationToken ct) =>
        {
            var lista = await servico.ListAsync(organizationId, ct);
            return Results.Ok(lista.Select(UserAccessResponse.From));
        });

        usuarios.MapPut("/{userId:guid}/role", async (Guid organizationId, Guid userId, GrantRoleRequest requisicao, UserService servico, CancellationToken ct) =>
        {
            if (!TryParseRole(requisicao.Role, out var papel))
            {
                return RoleInvalido();
            }

            await servico.GrantRoleAsync(organizationId, userId, papel, ct);
            return Results.NoContent();
        });

        usuarios.MapDelete("/{userId:guid}/role", async (Guid organizationId, Guid userId, UserService servico, CancellationToken ct) =>
        {
            await servico.RevokeRoleAsync(organizationId, userId, ct);
            return Results.NoContent();
        });

        return app;
    }

    private static bool TryParseRole(string? texto, out Role papel) =>
        Enum.TryParse(texto, ignoreCase: true, out papel) && Enum.IsDefined(papel);

    private static IResult RoleInvalido() =>
        Results.Problem(title: "Dados inválidos", detail: "O papel deve ser User, Manager ou OrganizationAdmin.", statusCode: StatusCodes.Status400BadRequest);

    private static async ValueTask<object?> TratarErrosDoIdentity(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        try
        {
            return await proximo(contexto);
        }
        catch (InvalidCredentialsException)
        {
            return Results.Problem(title: "Credenciais inválidas", detail: "Tenant, e-mail ou senha não conferem.", statusCode: StatusCodes.Status401Unauthorized);
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
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (IdentityResourceNotFoundException ex)
        {
            return Results.Problem(title: "Não encontrado", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (IdentityValidationException ex)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DuplicateUserEmailException ex)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
