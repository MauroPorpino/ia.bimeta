using System.Text.RegularExpressions;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace IaBimeta.Api.Embedded;

/// <summary>
/// A LISTA FECHADA de rotas que uma sessão embutida (ADR 0039) pode chamar. Tudo o que não está aqui responde 403 (<c>embedded-route-forbidden</c>), inclusive rota que não existe: a sessão embutida não descobre a
/// superfície da API. O que um atendente do ERP precisa é o atendimento (fila, conversa, resposta, transferência, encerramento, mídia e modelos) e o protocolo do próprio Embedded; nada de administração, estrutura,
/// usuários, canais, integrações, fluxos, conhecimento, auditoria ou senha. Ampliar esta lista exige ADR e a atualização do teste de arquitetura (<c>EmbeddedRoutePolicyTests</c>), que a compara com a lista aprovada.
/// </summary>
internal static class EmbeddedRoutePolicy
{
    /// <summary>Um padrão: o método e o caminho, com <c>{guid}</c> no lugar de um identificador.</summary>
    internal sealed record Route(string Method, string Pattern)
    {
        internal Regex Matcher { get; } = new(
            "^" + Regex.Escape(Pattern).Replace(@"\{guid}", "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Replace(@"\{ref}", "[^/]{1,300}") + "$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public override string ToString() => $"{Method} {Pattern}";
    }

    /// <summary>As rotas permitidas. A ordem é a do teste de arquitetura.</summary>
    public static IReadOnlyList<Route> Allowed { get; } = new Route[]
    {
        new("GET", "/auth/me"),
        new("POST", "/auth/logout"),

        new("GET", "/contact-center/me/queues"),
        new("GET", "/contact-center/me/presence"),
        new("PUT", "/contact-center/me/presence"),
        new("POST", "/contact-center/queues/{guid}/claim-next"),

        new("GET", "/contact-center/items"),
        new("GET", "/contact-center/items/{guid}"),
        new("GET", "/contact-center/items/{guid}/history"),
        new("GET", "/contact-center/items/{guid}/conversation"),
        new("GET", "/contact-center/items/{guid}/messages"),
        new("GET", "/contact-center/items/{guid}/messages/{guid}/attachment"),
        new("GET", "/contact-center/items/{guid}/reply-options"),
        new("POST", "/contact-center/items/{guid}/claim"),
        new("POST", "/contact-center/items/{guid}/release"),
        new("POST", "/contact-center/items/{guid}/transfer"),
        new("POST", "/contact-center/items/{guid}/return-to-ai"),
        new("POST", "/contact-center/items/{guid}/reply"),
        new("POST", "/contact-center/items/{guid}/reply-template"),
        new("POST", "/contact-center/items/{guid}/reply-media"),
        new("POST", "/contact-center/items/{guid}/close"),

        // O vínculo do cliente em foco com o contato da conversa (ADR 0041, E2.2): só a sessão FullAttendant (o subconjunto CustomerOnly abaixo não o alcança: CustomerOnly é só leitura). O {ref} é o identificador opaco do cliente.
        new("POST", "/contact-center/items/{guid}/customer-link"),
        new("DELETE", "/contact-center/items/{guid}/customer-link/{ref}"),

        // O histórico por cliente (ADR 0041, E2.3): sessão embutida com o cliente autorizado nela, nos dois modos.
        new("GET", "/contact-center/customers/{ref}/history"),

        new("GET", "/embed/v1/session"),
        new("POST", "/embed/v1/context"),
    };

    // O modo CustomerOnly (ADR 0041, D5; E2.3): a própria sessão, o resgate do contexto e SÓ LEITURA dos itens dos clientes autorizados (lista, item, conversa, mensagens, anexo e histórico por cliente). Nenhuma escrita, nenhuma
    // operação de fila, resposta, modelo, transferência, devolução à IA, encerramento, vínculo, presença nem fila geral. Cada entrada é MÉTODO + padrão e tem de estar também em Allowed; o item decide o acesso a cada
    // requisição (vínculo ativo, cliente autorizado na sessão, organização da conexão).
    private static readonly HashSet<string> CustomerOnlyRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /auth/me",
        "POST /auth/logout",
        "GET /contact-center/items",
        "GET /contact-center/items/{guid}",
        "GET /contact-center/items/{guid}/conversation",
        "GET /contact-center/items/{guid}/messages",
        "GET /contact-center/items/{guid}/messages/{guid}/attachment",
        "GET /contact-center/customers/{ref}/history",
        "GET /embed/v1/session",
        "POST /embed/v1/context",
    };

    /// <summary>As rotas do subconjunto <c>CustomerOnly</c> (para o teste de arquitetura compará-las com a lista aprovada).</summary>
    internal static IReadOnlyCollection<string> CustomerOnlyList => CustomerOnlyRoutes;

    /// <summary>Se o método e o caminho estão na lista E, para a sessão <c>CustomerOnly</c>, no subconjunto que ela alcança.</summary>
    public static bool IsAllowed(string? method, string? path, bool customerOnly)
    {
        if (!IsAllowed(method, path))
        {
            return false;
        }

        if (!customerOnly)
        {
            return true;
        }

        var caminho = path!.Length > 1 ? path.TrimEnd('/') : path;
        return Allowed.Any(r => CustomerOnlyRoutes.Contains(r.ToString())
            && string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)
            && r.Matcher.IsMatch(caminho));
    }

    /// <summary>Se o método e o caminho (sem consulta) estão na lista. Sem exceção: caminho vazio, com <c>..</c>, barras duplas ou fora do padrão nega.</summary>
    public static bool IsAllowed(string? method, string? path)
    {
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path) || path.Contains("//", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var caminho = path.Length > 1 ? path.TrimEnd('/') : path;
        foreach (var rota in Allowed)
        {
            if (string.Equals(rota.Method, method, StringComparison.OrdinalIgnoreCase) && rota.Matcher.IsMatch(caminho))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Barra a sessão embutida fora da lista fechada. Roda DEPOIS de o contexto ser criado e ANTES de qualquer endpoint: a origem do contexto (<c>embedded-session</c>) é a marca. Sessão interativa e rotas sem contexto
/// passam sem mudança.
/// </summary>
internal sealed class EmbeddedRouteGuard
{
    private readonly RequestDelegate _next;

    public EmbeddedRouteGuard(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext http, RequestContextHolder holder)
    {
        if (holder.Current is { Origin: RequestContexts.SessionTenantContextSource.EmbeddedOrigin } embutido
            && !EmbeddedRoutePolicy.IsAllowed(http.Request.Method, http.Request.Path.Value, embutido.Principal?.Embedded?.CustomerOnly ?? true))
        {
            var problema = new ProblemDetails
            {
                Title = "Rota não permitida",
                Detail = "Esta rota não está disponível para a sessão embutida.",
                Status = StatusCodes.Status403Forbidden,
            };
            problema.Extensions["code"] = "embedded-route-forbidden";
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(problema, options: null, contentType: "application/problem+json", cancellationToken: http.RequestAborted);
            return;
        }

        await _next(http);
    }
}

/// <summary>Política de partição do limite de taxa das chamadas assinadas.</summary>
internal static class EmbeddedHostPolicies
{
    /// <summary>
    /// A cota anterior à autenticação é SÓ por origem de rede (E1-R): nada que o chamador escolha (como o identificador de chave do cabeçalho, que ele inventaria a cada requisição para ganhar uma cota nova) define a partição.
    /// O limite por chave conhecida (falhas) e por conexão autenticada vem depois, no Identity (<c>IntegrationRateLimiter</c>).
    /// </summary>
    public static string IntegrationPartition(HttpContext http) => "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "origem-desconhecida");
}
