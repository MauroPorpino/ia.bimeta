using System.Text.Json;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// Traduz a resposta HTTP do envio para o resultado normalizado do contrato (ADR 0011, A.2, itens 6 a 10, e E.4). Três regras valem acima do resto:
/// <list type="bullet">
/// <item>O desfecho vem do <c>error.code</c> do corpo (a documentação manda construir o tratamento em torno dele), nunca de <c>error.message</c>, <c>error_data</c> ou <c>title</c>, que podem ecoar dados. O código sai
/// como TEXTO de dígitos; qualquer outra coisa é descartada.</item>
/// <item>Só vira <c>Rejected*</c>, <c>WindowClosed</c> ou <c>AccountError</c> o que o provedor RECUSOU com um corpo de erro completo (ou um estado 401/403 de credencial). Tudo o que deixa dúvida sobre ter havido envio
/// (tempo esgotado, rede, 5xx, 429 sem corpo de erro completo, corpo ilegível, sucesso sem identificador) é <c>Unknown</c>: nunca reenviado automaticamente (estratégia de efeito <c>None</c>).</item>
/// <item>A documentação não fixa o estado HTTP de cada código: o estado só decide quando o código não é um dos conhecidos.</item>
/// </list>
/// </summary>
internal static class WhatsAppSendResponseReader
{
    /// <summary>
    /// Limite de taxa que atinge a CONTA inteira: 130429 (vazão do Cloud API), 80007 (limite de taxa da conta WhatsApp Business) e 4 (limite de chamadas do aplicativo). A página oficial de erros (consulta de
    /// 05/11/2026) os lista como "throttling" e manda repetir mais tarde; ela NÃO fixa o estado HTTP nem o <c>Retry-After</c> deles. Retentativa com espera (ADR 0011, A.2, item 7), e a espera vale para a conta toda (SND-2).
    /// </summary>
    private static readonly HashSet<int> AccountRateLimitCodes = new() { 130429, 80007, 4 };

    /// <summary>131056: limite do par remetente-destinatário (1 mensagem a cada 6 s para o mesmo destinatário). A página oficial diz que se pode enviar a OUTRO destinatário sem esperar: a espera vale só para o par.</summary>
    private const int RecipientRateLimitCode = 131056;

    /// <summary>131000 (falha de envio desconhecida): transitória, mas sem prova de limite de taxa.</summary>
    private const int GenericTransientCode = 131000;

    /// <summary>131047: a janela de 24 horas está fechada, só template (ADR 0011, A.2, itens 8 e 10).</summary>
    private const int WindowClosedCode = 131047;

    /// <summary>131049, 131026 e 131048: falha definitiva da mensagem (ADR 0011, A.2, item 8).</summary>
    private static readonly HashSet<int> PermanentCodes = new() { 131049, 131026, 131048 };

    /// <summary>
    /// 190 e 368 pausam a conta e alertam (ADR 0011, A.2, item 9). Os códigos 0, 3 e 10 (token inválido ou invalidado e permissão ausente) são do mesmo tipo na página oficial de códigos de erro (consulta de
    /// 05/11/2026): também são problema da CONTA, não da mensagem.
    /// </summary>
    private static readonly HashSet<int> AccountCodes = new() { 190, 368, 0, 3, 10 };

    public static ProviderSendResult Read(int status, byte[]? body, TimeSpan? retryAfter)
    {
        try
        {
            return status is >= 200 and < 300
                ? ReadSuccess(body)
                : ReadHttpError(status, body, retryAfter);
        }
        catch (InvalidOperationException)
        {
            // Um escape de substituto UTF-16 sem par no texto do provedor faz o leitor de JSON lançar: a resposta é ilegível, e a mensagem pode ter sido enviada.
            return Unknown("invalid-response");
        }
    }

    // Sucesso (HTTP 2xx)

    private static ProviderSendResult ReadSuccess(byte[]? body)
    {
        if (body is null)
        {
            return Unknown("invalid-response");
        }

        try
        {
            using var documento = JsonDocument.Parse(body);
            var raiz = documento.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object
                && raiz.TryGetProperty("messages", out var mensagens)
                && mensagens.ValueKind == JsonValueKind.Array
                && mensagens.GetArrayLength() > 0
                && mensagens[0].ValueKind == JsonValueKind.Object
                && mensagens[0].TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && IsValidMessageId(id.GetString()))
            {
                return new ProviderSendResult(ProviderSendOutcome.Accepted, id.GetString(), null);
            }
        }
        catch (JsonException)
        {
        }

        // 2xx sem o identificador da mensagem: o provedor pode ter aceitado, e não há como correlacionar.
        return Unknown("invalid-response");
    }

    private static bool IsValidMessageId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 512)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    // Erro HTTP

    private static ProviderSendResult ReadHttpError(int status, byte[]? body, TimeSpan? retryAfter)
    {
        var (codigo, completo) = ReadErrorBody(body);
        var texto = codigo?.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var espera = retryAfter is { } sugerida && sugerida >= TimeSpan.Zero ? sugerida : (TimeSpan?)null;

        if (codigo is { } c)
        {
            if (AccountRateLimitCodes.Contains(c))
            {
                return new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, texto, espera, ProviderRateLimitScope.Account);
            }

            if (c == RecipientRateLimitCode)
            {
                return new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, texto, espera, ProviderRateLimitScope.Recipient);
            }

            if (c == GenericTransientCode)
            {
                return new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, texto, espera);
            }

            if (c == WindowClosedCode)
            {
                return new ProviderSendResult(ProviderSendOutcome.WindowClosed, null, texto);
            }

            if (PermanentCodes.Contains(c))
            {
                return new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, texto);
            }

            if (AccountCodes.Contains(c))
            {
                return new ProviderSendResult(ProviderSendOutcome.AccountError, null, texto);
            }
        }

        var codigoDeSaida = texto ?? $"http-{status}";

        // Credencial recusada pelo estado HTTP, sem código conhecido: erro da conta.
        if (status is 401 or 403)
        {
            return new ProviderSendResult(ProviderSendOutcome.AccountError, null, codigoDeSaida);
        }

        // 429 (Too Many Requests) COM o corpo de erro completo da Graph API, mesmo com um código que a lista não conhece: é o limitador do provedor recusando antes de processar. Vale como limite de taxa da CONTA
        // (a espera é a do Retry-After, se o provedor a mandou). 429 sem corpo de erro completo (proxy, resposta ilegível) não prova nada: fica desconhecido.
        if (status == 429 && completo)
        {
            return new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, codigoDeSaida, espera, ProviderRateLimitScope.Account);
        }

        // Os demais 4xx de validação (parâmetro inválido ou ausente, destinatário não registrado...) são recusas definitivas, mas SÓ com o corpo de erro completo do provedor. 408 (tempo) e 429 sem corpo de erro
        // completo não provam que nada foi enviado, e corpo ilegível não prova nada: ficam desconhecidos.
        if (status is >= 400 and < 500 and not (408 or 429) && completo)
        {
            return new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, codigoDeSaida);
        }

        return new ProviderSendResult(ProviderSendOutcome.Unknown, null, completo ? codigoDeSaida : (body is null ? "invalid-response" : $"http-{status}"));
    }

    /// <summary>
    /// O <c>error.code</c> (número inteiro, ou texto só de dígitos) e se o corpo é um erro COMPLETO (objeto <c>error</c> com algum campo reconhecível). Nada mais é lido: a mensagem pode ecoar dados do pedido.
    /// </summary>
    internal static (int? Code, bool Complete) ReadErrorBody(byte[]? body)
    {
        if (body is null)
        {
            return (null, false);
        }

        try
        {
            using var documento = JsonDocument.Parse(body);
            if (documento.RootElement.ValueKind != JsonValueKind.Object
                || !documento.RootElement.TryGetProperty("error", out var erro)
                || erro.ValueKind != JsonValueKind.Object)
            {
                return (null, false);
            }

            var codigo = ReadCode(erro);
            var completo = codigo is not null || erro.TryGetProperty("message", out _) || erro.TryGetProperty("type", out _);
            return (codigo, completo);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static int? ReadCode(JsonElement erro)
    {
        if (!erro.TryGetProperty("code", out var codigo))
        {
            return null;
        }

        if (codigo.ValueKind == JsonValueKind.Number && codigo.TryGetInt32(out var numero) && numero >= 0)
        {
            return numero;
        }

        if (codigo.ValueKind == JsonValueKind.String
            && codigo.GetString() is { Length: >= 1 and <= 9 } texto
            && texto.All(char.IsAsciiDigit)
            && int.TryParse(texto, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var daString))
        {
            return daString;
        }

        return null;
    }

    /// <summary>Resultado ambíguo: a mensagem PODE ter sido enviada. Só o código fixo (nunca texto do provedor).</summary>
    internal static ProviderSendResult Unknown(string code) => new(ProviderSendOutcome.Unknown, null, code);
}
