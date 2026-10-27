using IaBimeta.SharedKernel;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// Estabelece o <see cref="RequestContext"/> da requisição a partir das fontes confiáveis registradas. Se nenhuma
/// fonte responde, o contexto fica ausente e as operações que exigem tenant falham fechadas.
/// </summary>
internal sealed class RequestContextMiddleware
{
    private readonly RequestDelegate _next;

    public RequestContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext http, RequestContextHolder holder)
    {
        // O /health não exige tenant nem depende do banco: as fontes nem são resolvidas. Resolvê-las aqui, e não como
        // parâmetro do método, evita que uma fonte que usa o banco quebre o /health quando o banco não está configurado.
        if (!http.Request.Path.StartsWithSegments("/health"))
        {
            foreach (var fonte in http.RequestServices.GetServices<ITenantContextSource>())
            {
                var contexto = await fonte.ResolveAsync(http, http.RequestAborted);
                if (contexto is not null)
                {
                    holder.Set(contexto);
                    break;
                }
            }
        }

        await _next(http);
    }
}
