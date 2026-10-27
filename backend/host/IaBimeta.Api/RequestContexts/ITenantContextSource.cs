using IaBimeta.SharedKernel;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// Fonte confiável de contexto de tenant para uma requisição HTTP (TEN-1). Cada fonte valida a sua origem e devolve
/// <c>null</c> quando não consegue estabelecer um contexto. A primeira fonte que responder vence.
/// </summary>
internal interface ITenantContextSource
{
    Task<RequestContext?> ResolveAsync(HttpContext http, CancellationToken cancellationToken);
}
