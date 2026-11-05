using IaBimeta.Channels.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Channels;

/// <summary>
/// O ponto de entrada HTTP do webhook do provedor (ADR 0011, seção C; BOUNDARIES.md §12.2). É anônimo (o provedor não tem sessão): quem autentica é a ASSINATURA do corpo, com o segredo do aplicativo da conta.
/// O host faz só o que é do host: limita o tamanho do corpo, lê os BYTES brutos (a assinatura vale sobre eles), resolve a conta pelo identificador opaco da URL, cria o contexto de tenant (TEN-1: o tenant vem do
/// banco, pela URL, nunca do conteúdo) e traduz o desfecho em HTTP. Nenhuma chamada a outro módulo, à IA ou ao provedor acontece aqui (ARQ-19). Respostas sem detalhe: 404 identificador desconhecido, 401 assinatura
/// inválida, 400 corpo inválido, 413 corpo grande demais, 503 sem adaptador; 200 só DEPOIS do commit no Inbox (uma falha de gravação é 5xx, e o provedor reenvia).
/// </summary>
internal static class ChannelWebhookEndpoints
{
    /// <summary>O teto do corpo: admite o lote do provedor com folga e recusa o que não é webhook.</summary>
    public const int MaxBodyBytes = 1_048_576;

    private const string Origin = "channels-receiver";
    private const string SignatureHeader = "X-Hub-Signature-256";

    public static IEndpointRouteBuilder MapChannelWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/webhooks/whatsapp");

        // O pedido de verificação do cadastro do webhook (GET com desafio).
        grupo.MapGet("/{webhookId:guid}", async (Guid webhookId, HttpContext http, ChannelWebhookService servico, RequestContextHolder contexto, CancellationToken ct) =>
        {
            if (await servico.ResolveAsync(webhookId, ct) is not { } alvo)
            {
                return Results.NotFound();
            }

            contexto.Set(new RequestContext(alvo.TenantId, Origin));
            var consulta = http.Request.Query;
            var resposta = await servico.AnswerVerificationAsync(alvo, consulta["hub.mode"], consulta["hub.verify_token"], consulta["hub.challenge"], ct);
            return resposta is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Text(resposta, "text/plain");
        });

        grupo.MapPost("/{webhookId:guid}", async (Guid webhookId, HttpContext http, ChannelWebhookService servico, RequestContextHolder contexto, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is > MaxBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var corpo = await ReadBodyAsync(http.Request.Body, ct);
            if (corpo is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (await servico.ResolveAsync(webhookId, ct) is not { } alvo)
            {
                return Results.NotFound();
            }

            contexto.Set(new RequestContext(alvo.TenantId, Origin));
            var resultado = await servico.ReceiveAsync(alvo, corpo, http.Request.Headers[SignatureHeader], ct);
            return resultado.Outcome switch
            {
                WebhookReceiveOutcome.Accepted => Results.Ok(),
                WebhookReceiveOutcome.InvalidSignature => Results.StatusCode(StatusCodes.Status401Unauthorized),
                WebhookReceiveOutcome.InvalidPayload => Results.StatusCode(StatusCodes.Status400BadRequest),
                _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            };
        });

        return app;
    }

    /// <summary>Lê o corpo até o teto. Passou dele: nulo (413), sem ler o resto.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream corpo, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bloco = new byte[16 * 1024];
        int lidos;
        while ((lidos = await corpo.ReadAsync(bloco, cancellationToken)) > 0)
        {
            if (buffer.Length + lidos > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(bloco, 0, lidos);
        }

        return buffer.ToArray();
    }
}
