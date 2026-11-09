using System.Globalization;
using System.Text.Json;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// Lê as respostas do download de uma mídia (ADR 0028). Mesma regra do envio: o desfecho vem do estado HTTP e do <c>error.code</c> do corpo, nunca de texto do provedor, e só o que prova a recusa vira definitivo.
/// Consulta dos dados da mídia: <c>url</c>, <c>mime_type</c>, <c>sha256</c> e <c>file_size</c>. 404 é "mídia não encontrada ou endereço expirado" (documentado): a mídia (o identificador da mensagem expira em 7 dias) ou o
/// endereço (5 minutos) acabou, e o Channels não a busca de novo. 401 e 403 são a credencial. 5xx, 429 e rede são falha passageira.
/// </summary>
internal static class WhatsAppMediaResponseReader
{
    /// <summary>Os dados que a consulta devolveu (o endereço ainda por validar).</summary>
    internal sealed record Lookup(string Url, string? MimeType, string? Sha256, long? FileSize);

    /// <summary>Lê a consulta dos dados. Devolve os dados, ou o desfecho que encerra a tentativa.</summary>
    public static (Lookup? Data, ProviderMediaResult? Failure) ReadLookup(int status, byte[]? body)
    {
        if (status is >= 200 and < 300)
        {
            return ParseLookup(body) is { } dados ? (dados, null) : (null, Fail(ProviderMediaOutcome.Rejected, "invalid-media-response"));
        }

        return (null, FromStatus(status, body, "lookup"));
    }

    /// <summary>O desfecho de uma resposta de erro do conteúdo (ou da consulta), a partir do estado HTTP.</summary>
    public static ProviderMediaResult FromStatus(int status, byte[]? body, string step)
    {
        if (status is >= 300 and < 400)
        {
            return Fail(ProviderMediaOutcome.Rejected, "unexpected-redirect");
        }

        return status switch
        {
            404 or 410 => Fail(ProviderMediaOutcome.NotFound, "media-not-found"),
            401 or 403 => Fail(ProviderMediaOutcome.AccountError, "credential-rejected"),
            408 or 429 => Fail(ProviderMediaOutcome.Transient, status == 429 ? "rate-limited" : "timeout"),
            400 => ReadErrorCode(body) is { } codigo && IsAccountCode(codigo) ? Fail(ProviderMediaOutcome.AccountError, "credential-rejected") : Fail(ProviderMediaOutcome.Rejected, "provider-rejected"),
            >= 500 => Fail(ProviderMediaOutcome.Transient, "provider-error"),
            _ => Fail(ProviderMediaOutcome.Rejected, $"http-{step}-{status.ToString(CultureInfo.InvariantCulture)}"),
        };
    }

    public static ProviderMediaResult Fail(ProviderMediaOutcome outcome, string code) => new(outcome, null, null, null, code);

    private static bool IsAccountCode(int codigo) => codigo is 190 or 368 or 0 or 3 or 10;

    private static Lookup? ParseLookup(byte[]? body)
    {
        if (body is null)
        {
            return null;
        }

        try
        {
            using var documento = JsonDocument.Parse(body);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || url.GetString() is not { Length: > 0 and <= 4096 } endereco)
            {
                return null;
            }

            return new Lookup(endereco, Text(raiz, "mime_type", 127), Text(raiz, "sha256", 128), FileSize(raiz));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement objeto, string nome, int maximo) =>
        objeto.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String && valor.GetString() is { Length: > 0 } texto && texto.Length <= maximo ? texto : null;

    // O tamanho vem como número ou como texto de dígitos (a documentação o mostra como texto).
    private static long? FileSize(JsonElement objeto)
    {
        if (!objeto.TryGetProperty("file_size", out var valor))
        {
            return null;
        }

        if (valor.ValueKind == JsonValueKind.Number && valor.TryGetInt64(out var numero) && numero >= 0)
        {
            return numero;
        }

        return valor.ValueKind == JsonValueKind.String && long.TryParse(valor.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var daString) ? daString : null;
    }

    private static int? ReadErrorCode(byte[]? body) => WhatsAppSendResponseReader.ReadErrorBody(body).Code;
}
