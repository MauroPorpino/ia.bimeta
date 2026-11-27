using IaBimeta.Audit.Application;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Presentation;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using Microsoft.AspNetCore.Mvc;

namespace IaBimeta.Api.Platform;

internal sealed record PlatformLoginRequest(string? Email, string? Password);

internal sealed record RegisterCompanyRequest(
    string? Slug, string? Name, string? LegalName, string? Document, string? ContactName, string? ContactEmail,
    int? MaxUsers, bool? RegistrationEnabled, string? AdminEmail, string? AdminName);

internal sealed record UpdateCompanyRequest(string? Name, string? LegalName, string? Document, string? ContactName, string? ContactEmail);

internal sealed record SetLimitRequest(int? MaxUsers);

internal sealed record SetRegistrationRequest(bool? Enabled);

internal sealed record StatusChangeRequest(string? Reason, string? Confirm);

internal sealed record AdmitOperatorRequest(string? Email, string? DisplayName, string? Role);

internal sealed record UpdateOperatorRequest(string? Email, string? DisplayName);

internal sealed record SetOperatorRoleRequest(string? Role);

internal sealed record SetOperatorPermissionRequest(string? Permission, string? Effect);

internal sealed record PlatformRoleView(string Role, string Label, IReadOnlyList<string> Permissions);

internal sealed record PlatformAuditEventView(Guid Id, DateTimeOffset OccurredAt, Guid? ActorUserId, string ActorOrigin, string Operation, string ResourceType, string? ResourceId, string Outcome);

internal sealed record PlatformAuditPage(IReadOnlyList<PlatformAuditEventView> Items, int Total, int Page, int PageSize);

/// <summary>
/// As rotas da administração da plataforma BImeta (Etapa 02, ADR 0043), todas sob <c>/platform</c>. O login é o único anônimo; as demais exigem uma sessão do tenant técnico da plataforma
/// (o host recusa a sessão de uma empresa nesta árvore e a da plataforma fora dela, <see cref="PlatformRouteGuardMiddleware"/>) e cada operação confere a permissão de plataforma no serviço.
/// Nenhuma rota aceita tenant no corpo: a empresa-alvo é o identificador técnico do caminho, resolvido no registro, e nenhuma rota lista usuários de empresas.
/// </summary>
internal static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/platform").AddEndpointFilter(TratarErros);

        grupo.MapPost("/auth/login", async (PlatformLoginRequest requisicao, AuthenticationService servico, HttpContext http, CancellationToken ct) =>
        {
            var resultado = await servico.PlatformLoginAsync(requisicao.Email, requisicao.Password, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new LoginResponse(resultado.Token, resultado.ExpiresAt));
        }).RequireRateLimiting(IdentityEndpoints.LoginRateLimitPolicy);

        grupo.MapGet("/overview", async (PlatformCompanyService servico, CancellationToken ct) => Results.Ok(await servico.GetOverviewAsync(ct)));

        // Empresas

        grupo.MapGet("/companies", async (string? search, string? status, int? page, int? pageSize, PlatformCompanyService servico, CancellationToken ct) =>
            Results.Ok(await servico.ListAsync(search, status, page ?? 1, pageSize ?? 20, ct)));

        grupo.MapPost("/companies", async (RegisterCompanyRequest r, PlatformCompanyService servico, HttpContext http, CancellationToken ct) =>
        {
            var registrada = await servico.RegisterAsync(r.Slug, r.Name, r.LegalName, r.Document, r.ContactName, r.ContactEmail, r.MaxUsers, r.RegistrationEnabled ?? true, r.AdminEmail, r.AdminName, ct);
            http.Response.Headers.CacheControl = "no-store"; // o token do primeiro acesso aparece só nesta resposta
            return Results.Json(registrada, statusCode: registrada.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        grupo.MapGet("/companies/{slug}", async (string slug, PlatformCompanyService servico, CancellationToken ct) => Results.Ok(await servico.GetAsync(slug, ct)));

        grupo.MapPut("/companies/{slug}", async (string slug, UpdateCompanyRequest r, PlatformCompanyService servico, CancellationToken ct) =>
            Results.Ok(await servico.UpdateCadastralAsync(slug, r.Name, r.LegalName, r.Document, r.ContactName, r.ContactEmail, ct)));

        grupo.MapPut("/companies/{slug}/quota", async (string slug, SetLimitRequest r, PlatformCompanyService servico, CancellationToken ct) =>
            Results.Ok(await servico.SetLimitAsync(slug, r.MaxUsers, ct)));

        grupo.MapPut("/companies/{slug}/enrollment", async (string slug, SetRegistrationRequest r, PlatformCompanyService servico, CancellationToken ct) =>
            r.Enabled is null
                ? Results.Problem(title: "Dados inválidos", detail: "Informe se o cadastro de novos usuários fica habilitado.", statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(await servico.SetRegistrationAsync(slug, r.Enabled.Value, ct)));

        grupo.MapPost("/companies/{slug}/first-administrator/reissue", async (string slug, PlatformCompanyService servico, HttpContext http, CancellationToken ct) =>
        {
            var emitido = await servico.ReissueFirstAdministratorAsync(slug, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(emitido);
        });

        grupo.MapPost("/companies/{slug}/suspend", async (string slug, StatusChangeRequest r, PlatformCompanyService servico, CancellationToken ct) =>
            Results.Ok(await servico.SuspendAsync(slug, r.Reason, r.Confirm, ct)));

        grupo.MapPost("/companies/{slug}/reactivate", async (string slug, StatusChangeRequest r, PlatformCompanyService servico, CancellationToken ct) =>
            Results.Ok(await servico.ReactivateAsync(slug, r.Reason, r.Confirm, ct)));

        // Operadores

        // P3: o catálogo de perfis exige sessão de operador autenticada e a permissão de consulta de operadores/permissões; a cerca de rota já barra empresas e sessão embutida, e a ausência de sessão é 401.
        grupo.MapGet("/roles", async (PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.RequireCanListRolesAsync(ct);
            return Results.Ok(new[]
            {
                Papel(Role.PlatformAdministrator, "Administrador BImeta"),
                Papel(Role.PlatformCommercial, "Operador comercial"),
                Papel(Role.PlatformViewer, "Consulta"),
            });
        });

        grupo.MapGet("/operators", async (string? search, string? status, int? page, int? pageSize, PlatformOperatorService servico, CancellationToken ct) =>
            Results.Ok(await servico.ListAsync(search, status, page ?? 1, pageSize ?? 20, ct)));

        grupo.MapPost("/operators", async (AdmitOperatorRequest r, PlatformOperatorService servico, HttpContext http, CancellationToken ct) =>
        {
            var emitido = await servico.AdmitAsync(r.Email, r.DisplayName, ParseRole(r.Role), ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(emitido, statusCode: StatusCodes.Status201Created);
        });

        grupo.MapGet("/operators/{id:guid}", async (Guid id, PlatformOperatorService servico, CancellationToken ct) => Results.Ok(await servico.GetAsync(id, ct)));

        grupo.MapPut("/operators/{id:guid}", async (Guid id, UpdateOperatorRequest r, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.UpdateProfileAsync(id, r.Email, r.DisplayName, ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        grupo.MapPost("/operators/{id:guid}/access", async (Guid id, PlatformOperatorService servico, HttpContext http, CancellationToken ct) =>
        {
            var emitido = await servico.IssueAccessAsync(id, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(emitido);
        });

        grupo.MapPost("/operators/{id:guid}/sessions/revoke", async (Guid id, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.RevokeSessionsAsync(id, ct);
            return Results.NoContent();
        });

        grupo.MapPost("/operators/{id:guid}/deactivate", async (Guid id, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.DeactivateAsync(id, ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        grupo.MapPost("/operators/{id:guid}/reactivate", async (Guid id, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.ReactivateAsync(id, ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        grupo.MapPut("/operators/{id:guid}/role", async (Guid id, SetOperatorRoleRequest r, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.SetRoleAsync(id, ParseRole(r.Role), ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        grupo.MapPut("/operators/{id:guid}/permissions", async (Guid id, SetOperatorPermissionRequest r, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.SetOverrideAsync(id, ParsePermission(r.Permission), ParseEffect(r.Effect), ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        grupo.MapDelete("/operators/{id:guid}/permissions/{permission}", async (Guid id, string permission, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.RemoveOverrideAsync(id, ParsePermission(permission), ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        grupo.MapPost("/operators/{id:guid}/permissions/reset", async (Guid id, PlatformOperatorService servico, CancellationToken ct) =>
        {
            await servico.ResetOverridesAsync(id, ct);
            return Results.Ok(await servico.GetAsync(id, ct));
        });

        // Auditoria administrativa

        grupo.MapGet("/audit", async (int? page, int? pageSize, string? operation, string? resource, string? outcome, DateTimeOffset? from, DateTimeOffset? to, AuditQueryService servico, CancellationToken ct) =>
        {
            var tamanho = pageSize ?? 25;
            var (itens, total) = await servico.ListPlatformAsync(page ?? 1, tamanho, operation, resource, outcome, from, to, ct);
            var visao = itens.Select(e => new PlatformAuditEventView(e.Id, e.OccurredAt, e.ActorUserId, e.ActorOrigin, e.Operation, e.ResourceType, e.ResourceId, e.Outcome.ToString())).ToList();
            return Results.Ok(new PlatformAuditPage(visao, total, Math.Max(1, page ?? 1), Math.Clamp(tamanho <= 0 ? 25 : tamanho, 1, AuditQueryService.MaxTake)));
        });

        return app;
    }

    private static PlatformRoleView Papel(Role role, string rotulo) =>
        new(role.ToString(), rotulo, RolePermissions.Expand(role, null).Select(g => g.Permission.ToString()).ToList());

    private static Role ParseRole(string? texto) =>
        Enum.TryParse<Role>(texto, ignoreCase: true, out var papel) && papel.IsPlatform()
            ? papel
            : throw new PlatformValidationException("Informe um perfil de plataforma válido (PlatformAdministrator, PlatformCommercial ou PlatformViewer).");

    private static Permission ParsePermission(string? texto) =>
        Enum.TryParse<Permission>(texto, ignoreCase: true, out var permissao) && PlatformPermissions.IsPlatform(permissao)
            ? permissao
            : throw new PlatformValidationException("Informe uma permissão de plataforma válida.");

    private static OverrideEffect ParseEffect(string? texto) =>
        Enum.TryParse<OverrideEffect>(texto, ignoreCase: true, out var efeito) && Enum.IsDefined(efeito)
            ? efeito
            : throw new PlatformValidationException("Informe o efeito da personalização: Grant (conceder) ou Deny (restringir).");

    private static async ValueTask<object?> TratarErros(EndpointFilterInvocationContext contexto, EndpointFilterDelegate proximo)
    {
        try
        {
            return await proximo(contexto);
        }
        catch (InvalidCredentialsException)
        {
            return Results.Problem(title: "Credenciais inválidas", detail: "E-mail ou senha não conferem.", statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (LoginThrottledException ex)
        {
            contexto.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(ex.RetryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(title: "Muitas tentativas", detail: "Muitas tentativas de login. Tente novamente mais tarde.", statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (Exception ex) when (ex is TenantContextRequiredException or AuthenticationRequiredException)
        {
            return Results.Problem(title: "Autenticação necessária", detail: "Esta operação exige um operador autenticado.", statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (AccessDeniedException)
        {
            return Results.Problem(title: "Acesso negado", detail: "Você não tem permissão para esta operação.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is TenancyResourceNotFoundException or IdentityResourceNotFoundException)
        {
            return Results.Problem(title: "Não encontrado", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (DuplicateTenantSlugException ex)
        {
            return Results.Problem(title: "Identificador em uso", detail: ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: Code("slug-in-use"));
        }
        catch (Exception ex) when (ex is TenancyValidationException or IdentityValidationException or PlatformValidationException)
        {
            return Results.Problem(title: "Dados inválidos", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UserLimitReachedException ex)
        {
            return Results.Problem(
                title: "Limite de usuários atingido",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "user-limit-reached", ["limit"] = ex.Limit, ["used"] = ex.Used });
        }
        catch (LastAdministratorException ex)
        {
            return Results.Problem(title: "Último administrador", detail: ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: Code("last-administrator"));
        }
        catch (DuplicateUserEmailException ex)
        {
            return Results.Problem(title: "Conflito", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
