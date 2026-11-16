using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Valida o token de sessão apresentado e carrega o usuário e as permissões efetivas (TEN-1). Só a sessão no banco
/// decide o tenant: o token em si é opaco e não carrega nada. Recusa token malformado, desconhecido, expirado ou
/// revogado, de tenant suspenso e de usuário que já não existe. A resposta é sempre a mesma (<c>null</c>).
/// A sessão embutida (ADR 0039) é conferida a CADA requisição contra o estado atual: conexão desativada, vínculo
/// desativado ou conta que se tornou administrativa derrubam a sessão na hora, e as permissões nunca passam do teto
/// do Embedded (<see cref="EmbeddedSessionPolicy"/>: só o atendimento, só na organização da conexão).
/// </summary>
public sealed class SessionAuthenticator
{
    private readonly IAuthenticationStore _store;
    private readonly IEmbeddedStore _embedded;
    private readonly ITenantDirectory _tenants;
    private readonly TimeProvider _clock;

    public SessionAuthenticator(IAuthenticationStore store, IEmbeddedStore embedded, ITenantDirectory tenants, TimeProvider clock)
    {
        _store = store;
        _embedded = embedded;
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

        if (sessao.Kind == SessionKind.Embedded)
        {
            return await AuthenticateEmbeddedAsync(sessao, usuario, concessoes, cancellationToken);
        }

        // Usuário federado nunca tem sessão interativa (o login por senha o recusa); se aparecer uma, não vale.
        if (usuario.AuthMode == UserAuthMode.Federated)
        {
            return null;
        }

        var permissoes = concessoes
            .SelectMany(c => RolePermissions.Expand(c.Role, c.OrganizationId))
            .Distinct()
            .ToList();

        return new AuthenticatedPrincipal(sessao.TenantId, usuario.Id, sessao.Id, permissoes);
    }

    private async Task<AuthenticatedPrincipal?> AuthenticateEmbeddedAsync(Session sessao, User usuario, IReadOnlyList<UserOrganizationGrant> concessoes, CancellationToken cancellationToken)
    {
        if (sessao.IntegrationId is not { } conexaoId || sessao.ExternalUserId is null)
        {
            return null;
        }

        var conexao = await _embedded.FindConnectionAsync(sessao.TenantId, conexaoId, forUpdate: false, cancellationToken);
        if (conexao is null || !conexao.IsActive)
        {
            return null;
        }

        var vinculo = await _embedded.FindIdentityAsync(sessao.TenantId, conexaoId, sessao.ExternalUserId, cancellationToken);
        if (vinculo is not { IsActive: true } || vinculo.UserId != usuario.Id)
        {
            return null;
        }

        // Conta que passou a ser administrativa (ou perdeu o acesso à organização) não mantém sessão embutida.
        if (!EmbeddedSessionPolicy.IsEligible(concessoes, conexao.OrganizationId, out _))
        {
            return null;
        }

        var permissoes = EmbeddedSessionPolicy.Filter(
            concessoes.SelectMany(c => RolePermissions.Expand(c.Role, c.OrganizationId)),
            conexao.OrganizationId);

        return new AuthenticatedPrincipal(sessao.TenantId, usuario.Id, sessao.Id, permissoes, SessionKind.Embedded, conexaoId);
    }
}
