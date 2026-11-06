namespace IaBimeta.Api.Channels;

/// <summary>
/// A configuração do ingresso PÚBLICO do Api (seção <c>Channels:Webhook</c>): a porta que um túnel HTTPS (ou um proxy reverso) expõe à internet. Quando definida, essa porta serve SÓ o webhook do provedor.
/// </summary>
public sealed class PublicIngressOptions
{
    /// <summary>
    /// A porta local (por exemplo, 5156) para onde o túnel aponta. O Api precisa ESCUTAR nela (a URL do perfil de execução). Nulo: nenhum ingresso público configurado e o guarda não age (o Api só deve ser
    /// alcançado localmente).
    /// </summary>
    public int? PublicListenerPort { get; set; }
}

/// <summary>
/// O guarda do ingresso público. Um túnel entrega as requisições da internet ao Api como se viessem do loopback, e o ambiente de desenvolvimento tem o cabeçalho <c>X-Dev-Tenant</c> e as rotas <c>/simulator</c>
/// (credenciais sintéticas, escolha de tenant sem login): expor a porta de desenvolvimento INTEIRA a um túnel seria entregar isso à internet. Com <see cref="PublicIngressOptions.PublicListenerPort"/>
/// configurada, o Api só atende o que chega por essa porta se for o webhook do provedor (<c>/webhooks/whatsapp/...</c>); qualquer outro caminho responde 404, sem corpo. Defesa em profundidade: uma requisição
/// que traz o rastro de um túnel (<c>Cf-Ray</c>, <c>Cf-Connecting-Ip</c> ou <c>X-Forwarded-For</c>) também só alcança o webhook em QUALQUER porta, para o túnel apontado por engano para a porta principal não
/// expor nada além dele. O webhook em si continua protegido pela assinatura do corpo (ADR 0011).
/// </summary>
public sealed class PublicIngressGuard
{
    private static readonly PathString WebhookPrefix = new("/webhooks/whatsapp");
    private static readonly string[] TunnelHeaders = { "Cf-Ray", "Cf-Connecting-Ip", "X-Forwarded-For" };

    private readonly RequestDelegate _next;
    private readonly PublicIngressOptions _options;

    public PublicIngressGuard(RequestDelegate next, PublicIngressOptions options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext http)
    {
        if (_options.PublicListenerPort is not { } portaPublica || IsWebhook(http.Request.Path))
        {
            return _next(http);
        }

        var pelaPortaPublica = http.Connection.LocalPort == portaPublica;
        var comRastroDeTunel = TunnelHeaders.Any(h => http.Request.Headers.ContainsKey(h));
        if (pelaPortaPublica || comRastroDeTunel)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _next(http);
    }

    private static bool IsWebhook(PathString caminho) => caminho.StartsWithSegments(WebhookPrefix);
}
