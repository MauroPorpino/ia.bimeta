using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>A situação das vagas do tenant: o que o tenant PODE consultar (nunca alterar). <see cref="Limit"/> nulo é ilimitado (exceção explícita).</summary>
public sealed record UsageView(
    int? Limit,
    bool RegistrationEnabled,
    int Active,
    int Inactive,
    int Pending,
    int Expired,
    int Cancelled,
    int SeatsUsed,
    int? Available,
    bool OverLimit,
    int Excess);

/// <summary>Uma permissão num escopo: se vem do perfil, se há ajuste individual (<c>Grant</c> ou <c>Deny</c>) e se vale (efetiva).</summary>
public sealed record PermissionEntryView(Permission Permission, Guid? OrganizationId, bool InProfile, string? Override, bool Effective);

public sealed record UserListItemView(
    Guid Id,
    string Email,
    string DisplayName,
    string Status,
    string AuthMode,
    IReadOnlyList<GrantView> Grants,
    DateTimeOffset CreatedAt,
    bool Customized);

public sealed record UserDetailView(
    Guid Id,
    string Email,
    string DisplayName,
    string Status,
    string AuthMode,
    IReadOnlyList<GrantView> Grants,
    DateTimeOffset CreatedAt,
    bool Customized,
    bool HasLiveFirstAccessToken,
    IReadOnlyList<PermissionEntryView> Permissions);

public sealed record UserPageView(IReadOnlyList<UserListItemView> Items, int Total, int Page, int PageSize);

/// <summary>A admissão pronta: o usuário pendente e o token de primeiro acesso EM CLARO (mostrado uma única vez a quem admitiu).</summary>
public sealed record AdmissionIssued(UserListItemView User, string Token, DateTimeOffset ExpiresAt);

public static class UserAdminViewMapper
{
    internal static UserListItemView ToListItem(UserDetail d) =>
        new(d.User.Id, d.User.Email, d.User.DisplayName, d.DisplayStatus, d.User.AuthMode.ToString(),
            d.Grants.Select(g => new GrantView(g.OrganizationId, g.Role)).ToList(), d.User.CreatedAt, d.Overrides.Count > 0);

    internal static UserDetailView ToDetail(UserDetail d)
    {
        var efetivas = EffectivePermissions.Compute(d.Grants, d.Overrides).ToHashSet();
        var entradas = new List<PermissionEntryView>();
        foreach (var concessao in d.Grants)
        {
            var doPerfil = RolePermissions.Expand(concessao.Role, concessao.OrganizationId).Select(p => p.Permission).ToHashSet();
            var ajustes = d.Overrides.Where(o => o.OrganizationId == concessao.OrganizationId).ToDictionary(o => o.Permission);
            foreach (var info in PermissionCatalog.All)
            {
                if (!PermissionCatalog.IsValidInScope(info.Permission, concessao.OrganizationId))
                {
                    continue;
                }

                var noPerfil = doPerfil.Contains(info.Permission);
                ajustes.TryGetValue(info.Permission, out var ajuste);
                // TODAS as permissões válidas para o escopo entram: a que o perfil dá (herdada), a que o perfil NÃO dá (pode ser concedida individualmente), a concedida e a retirada; "efetiva" diz se vale.

                entradas.Add(new PermissionEntryView(
                    info.Permission, concessao.OrganizationId, noPerfil, ajuste?.Effect.ToString(), efetivas.Contains(new PermissionGrant(info.Permission, concessao.OrganizationId))));
            }
        }

        var item = ToListItem(d);
        return new UserDetailView(item.Id, item.Email, item.DisplayName, item.Status, item.AuthMode, item.Grants, item.CreatedAt, item.Customized, d.HasLiveFirstAccessToken, entradas);
    }
}
