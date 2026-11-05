using System.Globalization;
using System.Text.Json;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// Traduz a resposta da leitura do número (a sonda) para o resultado normalizado. Como no envio, o desfecho vem do <c>error.code</c> do corpo, nunca do texto livre, que pode ecoar dados. A página oficial do número
/// (consulta de 05/11/2026) mostra os erros 401 (código 190, token inválido), 403 (código 200, sem permissão) e 404 (código 803, número não encontrado) e NÃO diz que permissão a leitura exige: por isso só se afirma o que
/// a resposta provou. Tudo o que deixa dúvida (rede, tempo, 5xx, corpo ilegível, resposta de outro número) é <c>Unavailable</c> ou <c>NotFound</c>, nunca <c>Ok</c>.
/// </summary>
internal static class WhatsAppProbeResponseReader
{
    private const int MaxTextLength = 120;

    public static ProviderProbeResult Read(int status, byte[]? body, string expectedAccountId)
    {
        try
        {
            return status is >= 200 and < 300 ? ReadSuccess(body, expectedAccountId) : ReadError(status, body);
        }
        catch (InvalidOperationException)
        {
            return Unavailable("invalid-response");
        }
    }

    private static ProviderProbeResult ReadSuccess(byte[]? body, string expectedAccountId)
    {
        if (body is null)
        {
            return Unavailable("invalid-response");
        }

        try
        {
            using var documento = JsonDocument.Parse(body);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || Text(raiz, "id") is not { } id)
            {
                return Unavailable("invalid-response");
            }

            // O provedor respondeu sobre OUTRO número: a combinação credencial + número não está provada.
            if (!string.Equals(id, expectedAccountId, StringComparison.Ordinal))
            {
                return new ProviderProbeResult(ProviderProbeOutcome.NotFound, null, null, null, "id-mismatch");
            }

            return new ProviderProbeResult(ProviderProbeOutcome.Ok, Text(raiz, "verified_name"), Text(raiz, "display_phone_number"), Text(raiz, "quality_rating"), null);
        }
        catch (JsonException)
        {
            return Unavailable("invalid-response");
        }
    }

    private static ProviderProbeResult ReadError(int status, byte[]? body)
    {
        var (codigo, completo) = WhatsAppSendResponseReader.ReadErrorBody(body);
        var texto = codigo?.ToString(CultureInfo.InvariantCulture);

        if (codigo is { } c)
        {
            // Token expirado ou inválido (190, 0), permissão ausente (3, 10, 200 a 299) e conta restrita ou bloqueada (368, 131031): o problema é da credencial ou da conta.
            if (c is 190 or 368 or 0 or 3 or 10 or 131031 or (>= 200 and <= 299))
            {
                return new ProviderProbeResult(ProviderProbeOutcome.CredentialRejected, null, null, null, texto);
            }

            // 803 (número não encontrado) e 100 (parâmetro inválido: o objeto não existe ou a credencial não o enxerga).
            if (c is 803 or 100)
            {
                return new ProviderProbeResult(ProviderProbeOutcome.NotFound, null, null, null, texto);
            }
        }

        var saida = texto ?? $"http-{status}";
        if (status is 401 or 403)
        {
            return new ProviderProbeResult(ProviderProbeOutcome.CredentialRejected, null, null, null, saida);
        }

        if (status == 404 && completo)
        {
            return new ProviderProbeResult(ProviderProbeOutcome.NotFound, null, null, null, saida);
        }

        return Unavailable(completo ? saida : (body is null ? "invalid-response" : $"http-{status}"));
    }

    /// <summary>Um campo de texto do provedor, para EXIBIR ao administrador: só texto, sem controle, limitado. Nulo se ausente ou de outro tipo.</summary>
    private static string? Text(JsonElement objeto, string nome)
    {
        if (!objeto.TryGetProperty(nome, out var valor) || valor.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var texto = valor.GetString();
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var limpo = new string(texto.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (limpo.Length == 0)
        {
            return null;
        }

        return limpo.Length > MaxTextLength ? limpo[..MaxTextLength] : limpo;
    }

    internal static ProviderProbeResult Unavailable(string code) => new(ProviderProbeOutcome.Unavailable, null, null, null, code);
}
