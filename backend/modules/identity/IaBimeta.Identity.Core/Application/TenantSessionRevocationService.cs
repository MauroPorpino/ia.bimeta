using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Revoga todas as sessões de uma empresa (Etapa 02): é o passo do Identity da SUSPENSÃO e da REATIVAÇÃO (a reativação exige novas sessões: as anteriores não voltam a valer). Operação interna, sem rota HTTP: quem a
/// chama é o orquestrador da plataforma no host, depois de autorizar e auditar a operação. Não conhece usuários nem lê dados do cliente.
/// </summary>
public sealed class TenantSessionRevocationService
{
    private readonly IAuthenticationStore _auth;
    private readonly TimeProvider _clock;

    public TenantSessionRevocationService(IAuthenticationStore auth, TimeProvider clock)
    {
        _auth = auth;
        _clock = clock;
    }

    public Task<int> RevokeAllAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return _auth.RevokeAllSessionsAsync(tenantId, _clock.GetUtcNow(), cancellationToken);
    }
}
