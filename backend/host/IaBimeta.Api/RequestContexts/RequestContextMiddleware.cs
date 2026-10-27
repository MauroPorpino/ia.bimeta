using IaBimeta.SharedKernel;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// Estabelece o <see cref="RequestContext"/> da requisição a partir das fontes confiáveis registradas. Se nenhuma
/// fonte responde, o contexto fica ausente e as operações que exigem tenant falham fechadas.
/// </summary>
internal sealed class RequestContextMiddleware
{
    // Rotas que não usam contexto: o /health não exige tenant nem depende do banco, e o login é anterior a qualquer sessão.
    private static readonly string[] RotasSemContexto = { "/health", "/auth/login" };

    private readonly RequestDelegate _next;

    public RequestContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext http, RequestContextHolder holder)
    {
        // Resolver as fontes aqui, e não como parâmetro do método, evita que uma fonte que usa o banco quebre as
        // rotas sem contexto quando o banco não está configurado.
        if (!RotasSemContexto.Any(rota => http.Request.Path.StartsWithSegments(rota)))
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
