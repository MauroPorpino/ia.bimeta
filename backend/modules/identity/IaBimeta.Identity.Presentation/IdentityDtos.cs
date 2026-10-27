using IaBimeta.Identity.Application;

namespace IaBimeta.Identity.Presentation;

/// <summary>
/// Corpo do login. O <c>Tenant</c> (slug) só escolhe onde procurar o usuário: a identidade e o tenant do contexto
/// vêm da sessão criada, nunca deste campo.
/// </summary>
public sealed record LoginRequest(string? Tenant, string? Email, string? Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

/// <summary>Corpo da criação de usuário. Sem campo de tenant nem de organização: a organização vem da rota e é validada.</summary>
public sealed record CreateUserRequest(string? Email, string? DisplayName, string? Password, string? Role);

public sealed record UserResponse(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt)
{
    public static UserResponse From(UserView view) => new(view.Id, view.Email, view.DisplayName, view.CreatedAt);
}

public sealed record GrantRoleRequest(string? Role);

public sealed record UserAccessResponse(Guid UserId, string Email, string DisplayName, string Role, bool TenantWide)
{
    public static UserAccessResponse From(UserAccessView view) =>
        new(view.UserId, view.Email, view.DisplayName, view.Role.ToString(), view.TenantWide);
}

public sealed record GrantResponse(Guid? OrganizationId, string Role);

public sealed record CurrentUserResponse(Guid Id, string Email, string DisplayName, IReadOnlyList<GrantResponse> Grants)
{
    public static CurrentUserResponse From(CurrentUserView view) =>
        new(view.Id, view.Email, view.DisplayName, view.Grants.Select(g => new GrantResponse(g.OrganizationId, g.Role.ToString())).ToList());
}
