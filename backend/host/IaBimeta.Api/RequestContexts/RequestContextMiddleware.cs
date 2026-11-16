using IaBimeta.SharedKernel;

namespace IaBimeta.Api.RequestContexts;

/// <summary>
/// Estabelece o <see cref="RequestContext"/> da requisição a partir das fontes confiáveis registradas. Se nenhuma
/// fonte responde, o contexto fica ausente e as operações que exigem tenant falham fechadas.
/// </summary>
internal sealed class RequestContextMiddleware
{
    // Rotas que não usam contexto: o /health não exige tenant nem depende do banco, o login é anterior a qualquer sessão e o webhook do provedor é anônimo (o Receiver cria o contexto do tenant pela URL, depois de
    // resolver a conta: nunca por cabeçalho nem por sessão).
    // O Embedded (ADR 0039): as chamadas assinadas do servidor do ERP (autenticadas por HMAC, o tenant vem da chave) e a troca do ticket no iframe (anônima, com o verifier) são anteriores a qualquer sessão. As demais rotas
    // /embed/v1 (context, session) usam o contexto normal da sessão embutida.
    private static readonly string[] CaminhosSemContexto =
    {
        "/health", "/auth/login",
        "/embed/v1/launch-tickets", "/embed/v1/context-tickets", "/embed/v1/sessions/revoke", "/embed/v1/exchange",
    };

    // O webhook do provedor tem o identificador do canal no caminho: único prefixo da lista. Os demais são EXATOS (E1-R): "/health/x", "/auth/login/x" ou "/embed/v1/exchange/x" não ganham a dispensa de contexto.
    private const string PrefixoDoWebhook = "/webhooks/";

    /// <summary>Se a rota é uma das que dispensam o contexto de usuário. Caminho exato (sem barra final), sem diferenciar maiúsculas; nada de prefixo, exceto o do webhook.</summary>
    internal static bool SemContexto(PathString caminho)
    {
        var texto = caminho.Value ?? string.Empty;
        if (texto.StartsWith(PrefixoDoWebhook, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizado = texto.Length > 1 ? texto.TrimEnd('/') : texto;
        return CaminhosSemContexto.Contains(normalizado, StringComparer.OrdinalIgnoreCase);
    }

    private readonly RequestDelegate _next;

    public RequestContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext http, RequestContextHolder holder)
    {
        // Resolver as fontes aqui, e não como parâmetro do método, evita que uma fonte que usa o banco quebre as
        // rotas sem contexto quando o banco não está configurado.
        if (!SemContexto(http.Request.Path))
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
