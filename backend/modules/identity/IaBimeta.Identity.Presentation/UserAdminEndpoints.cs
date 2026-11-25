using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Identity.Presentation;

/// <summary>
/// Administração de usuários do tenant (Etapa 01). Nenhuma rota aceita tenant em corpo, query ou rota. As regras de elevação, de último administrador, de vagas e de habilitação vivem no <see cref="UserAdminService"/>: aqui só se traduz HTTP.
/// O token de primeiro acesso aparece só na resposta que o emite, sempre com <c>Cache-Control: no-store</c>.
/// </summary>
public static class UserAdminEndpoints
{
    public static IEndpointRouteBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var usuarios = app.MapGroup("/users").AddEndpointFilter(IdentityEndpoints.TratarErrosDoIdentity);

        usuarios.MapGet("/usage", async (UserAdminService servico, CancellationToken ct) => Results.Ok(await servico.GetUsageAsync(ct)));

        usuarios.MapGet("/permission-catalog", () => Results.Ok(BuildCatalog()));

        usuarios.MapGet("/", async (string? search, string? role, Guid? organizationId, string? status, int? page, int? pageSize, UserAdminService servico, CancellationToken ct) =>
        {
            Role? papel = null;
            if (!string.IsNullOrWhiteSpace(role))
            {
                if (!Enum.TryParse<Role>(role, ignoreCase: true, out var lido) || !Enum.IsDefined(lido))
                {
                    return IdentityEndpoints.RoleInvalidoResult();
                }

                papel = lido;
            }

            return Results.Ok(await servico.ListAsync(search, papel, organizationId, status, page ?? 1, pageSize ?? 20, ct));
        });

        usuarios.MapGet("/{userId:guid}", async (Guid userId, UserAdminService servico, CancellationToken ct) => Results.Ok(await servico.GetAsync(userId, ct)));

        usuarios.MapPut("/{userId:guid}", async (Guid userId, UpdateUserRequest requisicao, UserAdminService servico, CancellationToken ct) =>
            Results.Ok(await servico.UpdateProfileAsync(userId, requisicao.Email, requisicao.DisplayName, ct)));

        usuarios.MapPost("/{userId:guid}/deactivate", async (Guid userId, UserAdminService servico, CancellationToken ct) =>
        {
            await servico.DeactivateAsync(userId, ct);
            return Results.NoContent();
        });

        usuarios.MapPost("/{userId:guid}/reactivate", async (Guid userId, UserAdminService servico, CancellationToken ct) =>
        {
            await servico.ReactivateAsync(userId, ct);
            return Results.NoContent();
        });

        // Primeiro acesso: emite ou SUBSTITUI o token do usuário pendente (o anterior deixa de valer).
        usuarios.MapPost("/{userId:guid}/first-access", async (Guid userId, UserAdminService servico, HttpContext http, CancellationToken ct) =>
        {
            var emitido = await servico.IssueFirstAccessAsync(userId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new FirstAccessResponse(emitido.Token, emitido.ExpiresAt));
        });

        usuarios.MapDelete("/{userId:guid}/tokens", async (Guid userId, UserAdminService servico, CancellationToken ct) =>
        {
            await servico.CancelTokenAsync(userId, ct);
            return Results.NoContent();
        });

        usuarios.MapPost("/{userId:guid}/admission/cancel", async (Guid userId, UserAdminService servico, CancellationToken ct) =>
        {
            await servico.CancelAdmissionAsync(userId, ct);
            return Results.NoContent();
        });

        usuarios.MapPost("/{userId:guid}/admission/readmit", async (Guid userId, UserAdminService servico, HttpContext http, CancellationToken ct) =>
        {
            var emitido = await servico.ReadmitAsync(userId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new AdmissionResponse(emitido.User, emitido.Token, emitido.ExpiresAt));
        });

        // Permissões individuais (diferenças sobre o perfil).
        usuarios.MapPut("/{userId:guid}/permissions", async (Guid userId, SetPermissionOverrideRequest requisicao, UserAdminService servico, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Permission>(requisicao.Permission, ignoreCase: true, out var permissao) || !Enum.IsDefined(permissao)
                || !Enum.TryParse<OverrideEffect>(requisicao.Effect, ignoreCase: true, out var efeito) || !Enum.IsDefined(efeito))
            {
                return Results.Problem(title: "Dados inválidos", detail: "Informe uma permissão e um efeito (Grant ou Deny) válidos.", statusCode: StatusCodes.Status400BadRequest);
            }

            await servico.SetOverrideAsync(userId, requisicao.OrganizationId, permissao, efeito, ct);
            return Results.NoContent();
        });

        usuarios.MapDelete("/{userId:guid}/permissions", async (Guid userId, string? permission, Guid? organizationId, UserAdminService servico, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Permission>(permission, ignoreCase: true, out var permissao) || !Enum.IsDefined(permissao))
            {
                return Results.Problem(title: "Dados inválidos", detail: "Informe uma permissão válida.", statusCode: StatusCodes.Status400BadRequest);
            }

            await servico.RemoveOverrideAsync(userId, organizationId, permissao, ct);
            return Results.NoContent();
        });

        usuarios.MapPost("/{userId:guid}/permissions/reset", async (Guid userId, ResetPermissionOverridesRequest requisicao, UserAdminService servico, CancellationToken ct) =>
        {
            await servico.ResetOverridesAsync(userId, !requisicao.AllScopes, requisicao.OrganizationId, ct);
            return Results.NoContent();
        });

        // Admissão (cadastro com primeiro acesso) numa organização.
        app.MapGroup("/organizations/{organizationId:guid}/users/admissions").AddEndpointFilter(IdentityEndpoints.TratarErrosDoIdentity)
            .MapPost("/", async (Guid organizationId, AdmitUserRequest requisicao, UserAdminService servico, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<Role>(requisicao.Role, ignoreCase: true, out var papel) || !Enum.IsDefined(papel))
                {
                    return IdentityEndpoints.RoleInvalidoResult();
                }

                var admitido = await servico.AdmitAsync(organizationId, requisicao.Email, requisicao.DisplayName, papel, requisicao.PreferredLocale, ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Created($"/users/{admitido.User.Id}", new AdmissionResponse(admitido.User, admitido.Token, admitido.ExpiresAt));
            });

        return app;
    }

    private static PermissionCatalogResponse BuildCatalog()
    {
        // Os perfis funcionais (Supervisor = Manager) com as permissões padrão de cada um, por escopo.
        var perfis = new[]
        {
            (Role.OrganizationAdmin, "Administrador"),
            (Role.Manager, "Supervisor"),
            (Role.Attendant, "Atendente"),
            (Role.User, "Usuário"),
        };

        var roles = perfis.Select(p => new RoleDefaultResponse(
            p.Item1.ToString(),
            p.Item2,
            IaBimeta.Identity.Application.RolePermissions.Expand(p.Item1, Guid.Empty).Select(g => g.Permission.ToString()).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            IaBimeta.Identity.Application.RolePermissions.Expand(p.Item1, null).Select(g => g.Permission.ToString()).OrderBy(n => n, StringComparer.Ordinal).ToList())).ToList();

        return new PermissionCatalogResponse(
            PermissionCatalog.All.Select(i => new PermissionInfoResponse(i.Permission.ToString(), i.Group, i.Label, i.TenantOnly)).ToList(),
            roles);
    }
}
