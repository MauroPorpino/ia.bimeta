using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// Monta o pedido HTTP de envio de texto livre à Cloud API (<c>POST {base}{versão}/{phone_number_id}/messages</c>). O token da conta vai SÓ no cabeçalho <c>Authorization</c> DESTE pedido: nunca num cabeçalho
/// padrão do <see cref="HttpClient"/> compartilhado, então duas contas diferentes nunca se misturam. Tudo o que o adaptador consegue provar errado ANTES de chamar o provedor é recusado aqui, sem enviar nada
/// e sem repetir o valor recusado (o token, o texto do cliente e o destinatário nunca voltam em resultado, código de erro, mensagem de exceção nem log).
/// </summary>
internal static class WhatsAppSendRequestFactory
{
    /// <summary>O máximo de caracteres do corpo do texto (página oficial de mensagens de texto: "Maximum 4096 characters"). Contados em pontos de código, para não recusar o que a Meta aceita.</summary>
    internal const int MaxTextCharacters = 4096;

    /// <summary>O máximo de caracteres do <c>biz_opaque_callback_data</c> (ADR 0011, A.2, item 11).</summary>
    internal const int MaxCallbackDataCharacters = 512;

    private static readonly Regex AccountIdPattern = new("^[0-9]{1,30}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RecipientPattern = new("^[0-9]{5,20}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>O pedido pronto, ou nulo com a recusa (nada foi enviado ao provedor).</summary>
    public static HttpRequestMessage? Create(ProviderSendRequest request, Uri versionedBase, out ProviderSendResult? refusal)
    {
        refusal = null;

        // O token é ASCII visível, sem espaço nem controle: qualquer outra coisa nunca vai a um cabeçalho (injeção de cabeçalho) e nunca é repetida. É problema da CONTA (credencial), não da mensagem.
        if (!IsHeaderSafeToken(request.AccessToken))
        {
            refusal = Refuse(ProviderSendOutcome.AccountError, "credential-invalid-format");
            return null;
        }

        // O Phone Number ID é numérico e entra no caminho da URL: qualquer outra coisa seria injeção de caminho. Também é problema da conta (rota ou cadastro errado).
        if (request.ExternalAccountId is null || !AccountIdPattern.IsMatch(request.ExternalAccountId))
        {
            refusal = Refuse(ProviderSendOutcome.AccountError, "invalid-account-id");
            return null;
        }

        if (request.To is null || !RecipientPattern.IsMatch(request.To))
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "invalid-recipient");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "text-empty");
            return null;
        }

        if (CountCharacters(request.Text) > MaxTextCharacters)
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "text-too-long");
            return null;
        }

        if (request.OpaqueCallbackData is { Length: > MaxCallbackDataCharacters })
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "callback-data-too-long");
            return null;
        }

        var corpo = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = request.To,
            ["type"] = "text",
            ["text"] = new JsonObject
            {
                ["preview_url"] = false,
                ["body"] = Safe(request.Text),
            },
        };

        // O dado opaco que o provedor devolve nos status (a correlação com a nossa mensagem). Ausente, o campo não vai.
        if (!string.IsNullOrEmpty(request.OpaqueCallbackData))
        {
            corpo["biz_opaque_callback_data"] = Safe(request.OpaqueCallbackData);
        }

        var endereco = new Uri(versionedBase, $"{request.ExternalAccountId}/messages");
        var mensagem = new HttpRequestMessage(HttpMethod.Post, endereco)
        {
            Content = new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AccessToken);
        return mensagem;
    }

    /// <summary>
    /// A sonda: <c>GET {base}{versão}/{phone_number_id}?fields=id,display_phone_number,verified_name,quality_rating</c>, só leitura. Os campos são os que a referência oficial do número lista como padrão. O token vai só no
    /// cabeçalho deste pedido. Token ou número fora do formato: nulo, sem chamar o provedor.
    /// </summary>
    public static HttpRequestMessage? CreateProbe(ProviderProbeRequest request, Uri versionedBase)
    {
        if (!IsHeaderSafeToken(request.AccessToken) || request.ExternalAccountId is null || !AccountIdPattern.IsMatch(request.ExternalAccountId))
        {
            return null;
        }

        var endereco = new Uri(versionedBase, $"{request.ExternalAccountId}?fields=id,display_phone_number,verified_name,quality_rating");
        var mensagem = new HttpRequestMessage(HttpMethod.Get, endereco);
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AccessToken);
        return mensagem;
    }

    private static ProviderSendResult Refuse(ProviderSendOutcome outcome, string code) => new(outcome, null, code);

    internal static bool IsHeaderSafeToken(string? valor)
    {
        if (string.IsNullOrEmpty(valor) || valor.Length > 4096)
        {
            return false;
        }

        foreach (var c in valor)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    private static int CountCharacters(string texto)
    {
        var total = 0;
        for (var i = 0; i < texto.Length; i++)
        {
            if (char.IsHighSurrogate(texto[i]) && i + 1 < texto.Length && char.IsLowSurrogate(texto[i + 1]))
            {
                i++;
            }

            total++;
        }

        return total;
    }

    /// <summary>
    /// O texto como o JSON o aceita: um substituto UTF-16 sem par (que o cliente pode ter mandado) faria o serializador lançar. Vira U+FFFD, como o resto da plataforma trata texto inválido, e o envio segue.
    /// </summary>
    internal static string Safe(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        StringBuilder? corrigido = null;
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (char.IsHighSurrogate(c) && i + 1 < texto.Length && char.IsLowSurrogate(texto[i + 1]))
            {
                corrigido?.Append(c).Append(texto[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                corrigido ??= new StringBuilder(texto, 0, i, texto.Length);
                corrigido.Append('\uFFFD');
                continue;
            }

            corrigido?.Append(c);
        }

        return corrigido?.ToString() ?? texto;
    }
}
