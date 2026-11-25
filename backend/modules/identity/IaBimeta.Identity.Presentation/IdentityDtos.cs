using IaBimeta.Identity.Application;

namespace IaBimeta.Identity.Presentation;

/// <summary>
/// Corpo do login. O <c>Tenant</c> (slug) só escolhe onde procurar o usuário: a identidade e o tenant do contexto
/// vêm da sessão criada, nunca deste campo.
/// </summary>
public sealed record LoginRequest(string? Tenant, string? Email, string? Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

/// <summary>Troca da própria senha: a atual e a nova. Sem campo de usuário: o alvo é sempre o da sessão.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Uso do token de redefinição (anônimo, como o login): o <c>Tenant</c> (slug) só escolhe onde procurar o usuário; o token é o segredo.</summary>
public sealed record RedeemPasswordResetRequest(string? Tenant, string? Email, string? Token, string? NewPassword);

/// <summary>O token de redefinição EM CLARO: aparece só nesta resposta (e nada o guarda em cache, log ou auditoria).</summary>
public sealed record PasswordResetResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record RevokedSessionsResponse(int Revoked);

/// <summary>Corpo da criação de usuário. Sem campo de tenant nem de organização: a organização vem da rota e é validada.</summary>
public sealed record CreateUserRequest(string? Email, string? DisplayName, string? Password, string? Role, string? PreferredLocale = null);

public sealed record UserResponse(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt, string? PreferredLocale)
{
    public static UserResponse From(UserView view) => new(view.Id, view.Email, view.DisplayName, view.CreatedAt, view.PreferredLocale?.ToString());
}

/// <summary>Corpo da troca do idioma preferido do próprio usuário (<c>null</c> limpa). Sem campo de usuário: o alvo é o da sessão.</summary>
public sealed record SetPreferredLocaleRequest(string? Locale);

public sealed record GrantRoleRequest(string? Role);

public sealed record UserAccessResponse(Guid UserId, string Email, string DisplayName, string Role, bool TenantWide)
{
    public static UserAccessResponse From(UserAccessView view) =>
        new(view.UserId, view.Email, view.DisplayName, view.Role.ToString(), view.TenantWide);
}

public sealed record GrantResponse(Guid? OrganizationId, string Role);

public sealed record PermissionGrantResponse(string Permission, Guid? OrganizationId);

/// <param name="Permissions">As permissões EFETIVAS da sessão (perfil + ajustes individuais, com o teto do Embedded). A interface as usa para decidir o que mostrar; o servidor continua sendo a autoridade.</param>
public sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, IReadOnlyList<GrantResponse> Grants, string? PreferredLocale, IReadOnlyList<PermissionGrantResponse> Permissions)
{
    public static CurrentUserResponse From(CurrentUserView view) =>
        new(
            view.Id,
            view.Email,
            view.DisplayName,
            view.Grants.Select(g => new GrantResponse(g.OrganizationId, g.Role.ToString())).ToList(),
            view.PreferredLocale?.ToString(),
            (view.Permissions ?? Array.Empty<IaBimeta.SharedKernel.PermissionGrant>()).Select(p => new PermissionGrantResponse(p.Permission.ToString(), p.OrganizationId)).ToList());
}

// Administração de usuários (Etapa 01)

public sealed record AdmitUserRequest(string? Email, string? DisplayName, string? Role, string? PreferredLocale);

public sealed record UpdateUserRequest(string? Email, string? DisplayName);

public sealed record SetPermissionOverrideRequest(Guid? OrganizationId, string? Permission, string? Effect);

public sealed record ResetPermissionOverridesRequest(bool AllScopes, Guid? OrganizationId);

public sealed record AdmissionResponse(UserListItemView User, string Token, DateTimeOffset ExpiresAt);

public sealed record FirstAccessResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record RoleDefaultResponse(string Role, string Profile, IReadOnlyList<string> OrganizationPermissions, IReadOnlyList<string> TenantPermissions);

public sealed record PermissionCatalogResponse(IReadOnlyList<PermissionInfoResponse> Permissions, IReadOnlyList<RoleDefaultResponse> Roles);

public sealed record PermissionInfoResponse(string Permission, string Group, string Label, bool TenantOnly);
