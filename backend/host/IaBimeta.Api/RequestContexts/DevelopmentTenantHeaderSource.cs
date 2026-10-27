using System.Net;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// SOMENTE DESENVOLVIMENTO. Permite validar o fluxo localmente escolhendo o tenant pelo cabeçalho
/// <c>X-Dev-Tenant</c>. Não é autenticação e não serve para produção. Fica isolado por quatro travas:
/// 1. só é registrado se <c>Tenancy:Development:HeaderEnabled</c> for verdadeiro, e a partida FALHA se isso ocorrer
///    fora do ambiente Development (<see cref="Development.DevelopmentOnlyGuard"/>);
/// 2. só aceita requisições cuja origem de conexão é loopback, e nunca com <c>X-Forwarded-For</c> (proxy);
/// 3. o cabeçalho deve ter exatamente um valor, e o tenant precisa existir e estar ativo no registro;
/// 4. a origem do contexto fica marcada como <c>development-header</c>.
/// </summary>
internal sealed class DevelopmentTenantHeaderSource : ITenantContextSource
{
    public const string HeaderName = "X-Dev-Tenant";
    public const string Origin = "development-header";

    private readonly ITenantDirectory _diretorio;

    public DevelopmentTenantHeaderSource(ITenantDirectory diretorio)
    {
        _diretorio = diretorio;
    }

    public async Task<RequestContext?> ResolveAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var origem = http.Connection.RemoteIpAddress;
        if (origem is null || !IPAddress.IsLoopback(origem) || http.Request.Headers.ContainsKey("X-Forwarded-For"))
        {
            return null;
        }

        if (!http.Request.Headers.TryGetValue(HeaderName, out var valores) || valores.Count != 1)
        {
            return null;
        }

        var slug = valores[0];
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var tenant = await _diretorio.FindBySlugAsync(slug, cancellationToken);
        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return null;
        }

        return new RequestContext(tenant.TenantId, Origin);
    }
}
