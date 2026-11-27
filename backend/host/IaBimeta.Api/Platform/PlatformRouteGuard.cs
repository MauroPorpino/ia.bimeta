using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace IaBimeta.Api.Platform;

/// <summary>
/// A administração da plataforma BImeta está disponível neste ambiente? (<c>Platform:Enabled</c>, ADR 0043, seção 7.) Padrão: ligada SÓ em Development; em qualquer outro ambiente, desligada, e tudo sob
/// <c>/platform</c> (inclusive o login) responde 404. Enquanto não houver 2FA (BL-28) só pode ser ligada em ambiente local ou rede administrativa protegida: esta chave NÃO substitui a proteção de rede e
/// NÃO afrouxa o <c>PublicIngressGuard</c> (que vem antes e continua deixando o ingresso público alcançar só o webhook).
/// </summary>
public sealed class PlatformOptions
{
    public const string Section = "Platform";

    public bool Enabled { get; init; }

    public static PlatformOptions From(IConfiguration configuration, IHostEnvironment environment) =>
        new() { Enabled = configuration.GetValue<bool?>($"{Section}:Enabled") ?? environment.IsDevelopment() };
}

/// <summary>Primeira cerca de disponibilidade: com a plataforma desligada, qualquer caminho sob <c>/platform</c> responde 404 sem corpo, antes da autenticação, do limite e de qualquer endpoint.</summary>
internal sealed class PlatformAvailabilityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PlatformOptions _options;

    public PlatformAvailabilityMiddleware(RequestDelegate next, PlatformOptions options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext http)
    {
        if (!_options.Enabled && PlatformRouteGuardMiddleware.IsPlatformPath(http.Request.Path))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _next(http);
    }
}

/// <summary>
/// A separação entre as duas administrações (Etapa 02, ADR 0043), na borda HTTP: a sessão da PLATAFORMA só alcança <c>/platform/*</c> e as poucas rotas da própria conta (<c>/auth/me</c>, troca de senha e logout, e <c>/health</c>);
/// a sessão de uma EMPRESA (interativa ou embutida) nunca alcança <c>/platform/*</c> (exceto o login anônimo). A autorização de cada operação continua no serviço (permissão de plataforma no tenant técnico): este filtro é a
/// primeira cerca e a mais simples de auditar.
/// </summary>
internal sealed class PlatformRouteGuardMiddleware
{
    public const string PlatformOrigin = "platform-session";

    private static readonly string[] OwnAccountPaths = { "/health", "/auth/logout", "/auth/me", "/auth/me/password" };

    private readonly RequestDelegate _next;
    private readonly PlatformOptions _options;

    public PlatformRouteGuardMiddleware(RequestDelegate next, PlatformOptions options)
    {
        _next = next;
        _options = options;
    }

    internal static bool IsPlatformPath(PathString path)
    {
        var texto = path.Value ?? string.Empty;
        return texto.Equals("/platform", StringComparison.OrdinalIgnoreCase) || texto.StartsWith("/platform/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPlatformLogin(PathString path) =>
        string.Equals((path.Value ?? string.Empty).TrimEnd('/'), "/platform/auth/login", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowedForPlatformSession(PathString path)
    {
        if (IsPlatformPath(path))
        {
            return true;
        }

        var normalizado = (path.Value ?? string.Empty).TrimEnd('/');
        return OwnAccountPaths.Contains(normalizado, StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext http, RequestContextHolder holder)
    {
        var plataforma = holder.Current is { Origin: PlatformOrigin };

        // Plataforma desligada: nem uma sessão de plataforma que tenha sobrado de antes alcança qualquer rota (404, como se a administração não existisse).
        if (plataforma && !_options.Enabled)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var proibido = plataforma
            ? !IsAllowedForPlatformSession(http.Request.Path)
            : holder.Current is not null && IsPlatformPath(http.Request.Path) && !IsPlatformLogin(http.Request.Path);

        if (proibido)
        {
            var problema = new ProblemDetails
            {
                Title = "Rota não permitida",
                Detail = plataforma ? "A sessão da plataforma não acessa o ambiente das empresas." : "A administração da plataforma não está disponível para esta sessão.",
                Status = StatusCodes.Status403Forbidden,
            };
            problema.Extensions["code"] = plataforma ? "platform-session-route-forbidden" : "platform-route-forbidden";
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(problema, options: null, contentType: "application/problem+json", cancellationToken: http.RequestAborted);
            return;
        }

        await _next(http);
    }
}
