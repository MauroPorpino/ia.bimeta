using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>O resultado do login: o token aparece aqui uma única vez e nunca mais pode ser recuperado.</summary>
public sealed record LoginResult(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// A identidade autenticada, devolvida ao host para ele criar o <see cref="RequestContext"/>. O tenant vem da sessão
/// no banco, nunca da requisição.
/// </summary>
public sealed record AuthenticatedPrincipal(TenantId TenantId, Guid UserId, Guid SessionId, IReadOnlyList<PermissionGrant> Permissions);

public sealed record UserView(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt)
{
    internal static UserView From(User user) => new(user.Id, user.Email, user.DisplayName, user.CreatedAt);
}

/// <summary>Um usuário com o papel que tem na organização. <see cref="TenantWide"/> indica concessão de escopo do tenant.</summary>
public sealed record UserAccessView(Guid UserId, string Email, string DisplayName, Role Role, bool TenantWide)
{
    internal static UserAccessView From(UserAccess acesso) =>
        new(acesso.User.Id, acesso.User.Email, acesso.User.DisplayName, acesso.Grant.Role, acesso.Grant.OrganizationId is null);
}

public sealed record GrantView(Guid? OrganizationId, Role Role);

public sealed record CurrentUserView(Guid Id, string Email, string DisplayName, IReadOnlyList<GrantView> Grants);
