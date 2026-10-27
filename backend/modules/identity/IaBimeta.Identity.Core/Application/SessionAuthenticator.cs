using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Valida o token de sessão apresentado e carrega o usuário e as permissões efetivas (TEN-1). Só a sessão no banco
/// decide o tenant: o token em si é opaco e não carrega nada. Recusa token malformado, desconhecido, expirado ou
/// revogado, de tenant suspenso e de usuário que já não existe. A resposta é sempre a mesma (<c>null</c>).
/// </summary>
public sealed class SessionAuthenticator
{
    private readonly IAuthenticationStore _store;
    private readonly ITenantDirectory _tenants;
    private readonly TimeProvider _clock;

    public SessionAuthenticator(IAuthenticationStore store, ITenantDirectory tenants, TimeProvider clock)
    {
        _store = store;
        _tenants = tenants;
        _clock = clock;
    }

    public async Task<AuthenticatedPrincipal?> AuthenticateAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!SessionTokens.TryHash(token, out var hash))
        {
            return null;
        }

        var sessao = await _store.FindSessionByTokenHashAsync(hash, cancellationToken);
        if (sessao is null || !sessao.IsActiveAt(_clock.GetUtcNow()))
        {
            return null;
        }

        var tenant = await _tenants.FindByIdAsync(sessao.TenantId, cancellationToken);
        if (tenant is not { Status: TenantStatus.Active })
        {
            return null;
        }

        var usuario = await _store.FindUserByIdAsync(sessao.TenantId, sessao.UserId, cancellationToken);
        if (usuario is null)
        {
            return null;
        }

        var concessoes = await _store.ListGrantsAsync(sessao.TenantId, usuario.Id, cancellationToken);
        var permissoes = concessoes
            .SelectMany(c => RolePermissions.Expand(c.Role, c.OrganizationId))
            .Distinct()
            .ToList();

        return new AuthenticatedPrincipal(sessao.TenantId, usuario.Id, sessao.Id, permissoes);
    }
}
