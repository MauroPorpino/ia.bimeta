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
            "^" + Regex.Escape(Pattern).Replace(@"\{guid}", "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}") + "$",
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

        new("GET", "/embed/v1/session"),
        new("POST", "/embed/v1/context"),
    };

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
        if (holder.Current is { Origin: RequestContexts.SessionTenantContextSource.EmbeddedOrigin } && !EmbeddedRoutePolicy.IsAllowed(http.Request.Method, http.Request.Path.Value))
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
    /// <summary>A cota é por identificador de chave informado; sem cabeçalho (ou lixo), cai na origem de rede.</summary>
    public static string IntegrationPartition(HttpContext http)
    {
        var chave = http.Request.Headers["X-IaB-Key-Id"].ToString();
        return chave.Length is > 0 and <= 64 ? "k:" + chave : "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "origem-desconhecida");
    }
}
