using IaBimeta.Identity.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// Fonte de contexto de produção (TEN-1): usuário autenticado por token de sessão opaco, <c>Authorization: Bearer ...</c>.
/// O Identity valida a sessão no banco (existência, validade, revogação, tenant ativo) e devolve o usuário e as
/// permissões efetivas; o tenant vem da sessão, nunca da requisição. Qualquer falha devolve <c>null</c>, e o contexto
/// fica ausente (as rotas protegidas respondem 401).
/// </summary>
internal sealed class SessionTenantContextSource : ITenantContextSource
{
    public const string Origin = "session";

    private const string BearerPrefix = "Bearer ";

    private readonly SessionAuthenticator _autenticador;

    public SessionTenantContextSource(SessionAuthenticator autenticador)
    {
        _autenticador = autenticador;
    }

    public async Task<RequestContext?> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var valores = http.Request.Headers.Authorization;
        if (valores.Count != 1)
        {
            return null;
        }

        var valor = valores[0];
        if (valor is null || !valor.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var principal = await _autenticador.AuthenticateAsync(valor[BearerPrefix.Length..].Trim(), cancellationToken);
        if (principal is null)
        {
            return null;
        }

        return new RequestContext(
            principal.TenantId,
            Origin,
            new ActorPrincipal(principal.UserId, principal.SessionId, principal.Permissions));
    }
}
