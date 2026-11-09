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

        return BuildSend(corpo, request.ExternalAccountId, request.AccessToken, versionedBase);
    }

    /// <summary>O pedido de envio de uma mensagem em JSON (<c>POST {base}{versão}/{phone_number_id}/messages</c>: texto livre, modelo e a mensagem que usa um arquivo já subido).</summary>
    private static HttpRequestMessage BuildSend(JsonObject corpo, string accountId, string accessToken, Uri versionedBase) =>
        BuildPost(new Uri(versionedBase, $"{accountId}/messages"), new StringContent(corpo.ToJsonString(), Encoding.UTF8, "application/json"), accessToken);

    /// <summary>O ÚNICO lugar que monta um POST (o envio da mensagem e a subida do arquivo, ADR 0031): o token vai só no cabeçalho deste pedido.</summary>
    private static HttpRequestMessage BuildPost(Uri endereco, HttpContent conteudo, string accessToken)
    {
        var mensagem = new HttpRequestMessage(HttpMethod.Post, endereco) { Content = conteudo };
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return mensagem;
    }

    /// <summary>O teto do arquivo enviado: o maior limite da Cloud API (documento, 100 MB). O limite de cada tipo é do Conversations, que o aplica antes; aqui é só a última defesa.</summary>
    internal const long MaxMediaUploadBytes = 100L * 1024 * 1024;

    private static readonly Regex MimePattern = new("^[a-z0-9][a-z0-9.+-]*/[a-z0-9][a-z0-9.+-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int MaxMediaCaptionCharacters = 1024;

    /// <summary>
    /// O que o adaptador prova errado no pedido de arquivo ANTES de chamar o provedor (nada é subido nem enviado, e nenhum valor é repetido): token, número, destinatário, tipo de mídia, tipo do conteúdo, tamanho,
    /// nome, legenda (áudio não tem) e dado opaco. Nulo se o pedido está em ordem.
    /// </summary>
    public static ProviderSendResult? ValidateMediaSend(ProviderMediaSendRequest request)
    {
        if (!IsHeaderSafeToken(request.AccessToken))
        {
            return Refuse(ProviderSendOutcome.AccountError, "credential-invalid-format");
        }

        if (request.ExternalAccountId is null || !AccountIdPattern.IsMatch(request.ExternalAccountId))
        {
            return Refuse(ProviderSendOutcome.AccountError, "invalid-account-id");
        }

        if (request.To is null || !RecipientPattern.IsMatch(request.To))
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, "invalid-recipient");
        }

        if (request.Kind is not (ProviderMessageKind.Image or ProviderMessageKind.Audio or ProviderMessageKind.Document))
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, "media-kind-invalid");
        }

        if (request.MimeType is null || !MimePattern.IsMatch(request.MimeType) || request.MimeType.Length > 127)
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, "media-type-invalid");
        }

        if (request.Content is not { Length: > 0 } conteudo || conteudo.Length > MaxMediaUploadBytes)
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, "media-size-invalid");
        }

        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255 || request.FileName.Any(char.IsControl))
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, "media-name-invalid");
        }

        if (request.Caption is not null && (request.Kind == ProviderMessageKind.Audio || CountCharacters(request.Caption) > MaxMediaCaptionCharacters))
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, request.Kind == ProviderMessageKind.Audio ? "caption-not-supported" : "caption-too-long");
        }

        if (request.OpaqueCallbackData is { Length: > MaxCallbackDataCharacters })
        {
            return Refuse(ProviderSendOutcome.RejectedPermanent, "callback-data-too-long");
        }

        return null;
    }

    /// <summary>
    /// A subida do arquivo (<c>POST {base}{versão}/{phone_number_id}/media</c>, formulário com <c>messaging_product</c>, <c>type</c> e <c>file</c>; consulta da documentação de 09/11/2026). O pedido já passou por
    /// <see cref="ValidateMediaSend"/>. O nome do arquivo na subida é sempre ASCII e genérico (o nome que o cliente vê vai na mensagem do documento), para nunca depender da codificação de cabeçalho.
    /// </summary>
    public static HttpRequestMessage CreateMediaUpload(ProviderMediaSendRequest request, Uri versionedBase)
    {
        var formulario = new MultipartFormDataContent();
        formulario.Add(new StringContent("whatsapp"), "messaging_product");
        formulario.Add(new StringContent(request.MimeType), "type");
        var arquivo = new ByteArrayContent(request.Content);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue(request.MimeType);
        formulario.Add(arquivo, "file", "upload" + UploadExtension(request.MimeType));
        return BuildPost(new Uri(versionedBase, $"{request.ExternalAccountId}/media"), formulario, request.AccessToken);
    }

    /// <summary>A mensagem que usa o arquivo já subido: <c>image</c>/<c>document</c> com <c>id</c> e legenda (o documento leva também o nome) e <c>audio</c> só com o <c>id</c>.</summary>
    public static HttpRequestMessage CreateMediaMessage(ProviderMediaSendRequest request, string mediaId, Uri versionedBase)
    {
        var (tipo, objeto) = request.Kind switch
        {
            ProviderMessageKind.Image => ("image", new JsonObject { ["id"] = mediaId }),
            ProviderMessageKind.Audio => ("audio", new JsonObject { ["id"] = mediaId }),
            _ => ("document", new JsonObject { ["id"] = mediaId, ["filename"] = Safe(request.FileName) }),
        };

        if (!string.IsNullOrWhiteSpace(request.Caption))
        {
            objeto["caption"] = Safe(request.Caption);
        }

        var corpo = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = request.To,
            ["type"] = tipo,
            [tipo] = objeto,
        };

        if (!string.IsNullOrEmpty(request.OpaqueCallbackData))
        {
            corpo["biz_opaque_callback_data"] = Safe(request.OpaqueCallbackData);
        }

        return BuildSend(corpo, request.ExternalAccountId, request.AccessToken, versionedBase);
    }

    private static string UploadExtension(string mimeType) => mimeType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "audio/aac" => ".aac",
        "audio/amr" => ".amr",
        "audio/mpeg" => ".mp3",
        "audio/mp4" => ".m4a",
        "audio/ogg" => ".ogg",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        "application/msword" => ".doc",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.ms-powerpoint" => ".ppt",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        _ => ".bin",
    };

    /// <summary>O identificador do arquivo subido, como a Cloud API o devolve, é conferido (mesma regra do identificador da mídia recebida) antes de entrar na mensagem.</summary>
    internal static bool IsValidMediaId(string? id) => id is not null && MediaIdPattern.IsMatch(id);

    /// <summary>O nome do modelo: o que a Meta aceita (letras minúsculas, números e sublinhado). Entra no corpo JSON, não na URL, mas o formato é o do cadastro.</summary>
    private static readonly Regex TemplateNamePattern = new("^[a-z0-9_]{1,512}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LanguagePattern = new("^[a-z]{2,3}(_[A-Za-z]{2,4})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>O teto de parâmetros por modelo (o mesmo do cadastro) e o de cada valor de texto: 1024 no corpo, 60 no cabeçalho, 512 no botão (a Meta conta o valor, não o texto final).</summary>
    internal const int MaxTemplateParameters = 28;

    private const int MaxTemplateParameterCharacters = 1024;

    /// <summary>
    /// O pedido de envio de um modelo (<c>type: "template"</c>): nome, idioma e os componentes com os parâmetros de TEXTO (cabeçalho, corpo e botão de link). Parâmetros nomeados levam o <c>parameter_name</c>. O que o
    /// adaptador prova errado antes de chamar a Meta (formato do nome ou do idioma, parâmetro vazio, com quebra de linha ou grande demais) é recusa definitiva, sem enviar e sem repetir o valor.
    /// </summary>
    public static HttpRequestMessage? CreateTemplate(ProviderTemplateSendRequest request, Uri versionedBase, out ProviderSendResult? refusal)
    {
        refusal = null;

        if (!IsHeaderSafeToken(request.AccessToken))
        {
            refusal = Refuse(ProviderSendOutcome.AccountError, "credential-invalid-format");
            return null;
        }

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

        if (request.TemplateName is null || !TemplateNamePattern.IsMatch(request.TemplateName) || request.Language is null || !LanguagePattern.IsMatch(request.Language))
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "template-invalid");
            return null;
        }

        if (request.OpaqueCallbackData is { Length: > MaxCallbackDataCharacters })
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "callback-data-too-long");
            return null;
        }

        var parametros = request.Parameters ?? Array.Empty<ProviderTemplateParameter>();
        if (parametros.Count > MaxTemplateParameters
            || parametros.Any(p => p is null || !IsValidParameterValue(p.Value) || p.Index < 0 || p.Component is not ("header" or "body" or "button")))
        {
            refusal = Refuse(ProviderSendOutcome.RejectedPermanent, "template-parameter-invalid");
            return null;
        }

        var componentes = new JsonArray();
        foreach (var grupo in new[] { "header", "body" })
        {
            var doGrupo = parametros.Where(p => p.Component == grupo).OrderBy(p => p.Index).ToList();
            if (doGrupo.Count > 0)
            {
                componentes.Add(new JsonObject
                {
                    ["type"] = grupo,
                    ["parameters"] = new JsonArray(doGrupo.Select(TextParameter).ToArray<JsonNode?>()),
                });
            }
        }

        // O botão de link leva o índice do botão (como texto, na referência da Meta) e o complemento do endereço.
        foreach (var botao in parametros.Where(p => p.Component == "button").OrderBy(p => p.Index))
        {
            componentes.Add(new JsonObject
            {
                ["type"] = "button",
                ["sub_type"] = "url",
                ["index"] = botao.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["parameters"] = new JsonArray(TextParameter(botao)),
            });
        }

        var modelo = new JsonObject
        {
            ["name"] = request.TemplateName,
            ["language"] = new JsonObject { ["code"] = request.Language },
        };
        if (componentes.Count > 0)
        {
            modelo["components"] = componentes;
        }

        var corpo = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = request.To,
            ["type"] = "template",
            ["template"] = modelo,
        };

        if (!string.IsNullOrEmpty(request.OpaqueCallbackData))
        {
            corpo["biz_opaque_callback_data"] = Safe(request.OpaqueCallbackData);
        }

        return BuildSend(corpo, request.ExternalAccountId, request.AccessToken, versionedBase);
    }

    private static JsonObject TextParameter(ProviderTemplateParameter parametro)
    {
        var json = new JsonObject { ["type"] = "text" };
        if (!string.IsNullOrEmpty(parametro.Name))
        {
            json["parameter_name"] = parametro.Name;
        }

        json["text"] = Safe(parametro.Value);
        return json;
    }

    /// <summary>O valor de um parâmetro de texto: não vazio, sem controle (a Meta recusa quebra de linha e tabulação) e dentro do tamanho.</summary>
    private static bool IsValidParameterValue(string? valor) =>
        !string.IsNullOrWhiteSpace(valor) && CountCharacters(valor) <= MaxTemplateParameterCharacters && !valor.Any(char.IsControl);

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

        return BuildGet(new Uri(versionedBase, $"{request.ExternalAccountId}?fields=id,display_phone_number,verified_name,quality_rating"), request.AccessToken, "application/json");
    }

    /// <summary>O identificador da mídia no caminho da URL: só letras, dígitos e <c>_.-</c> (nada que mude o caminho).</summary>
    private static readonly Regex MediaIdPattern = new("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A consulta dos dados da mídia (ADR 0028): <c>GET {base}{versão}/{media-id}</c>, com o token SÓ neste cabeçalho. Nulo se o identificador ou o token estão fora do formato (nada é pedido).</summary>
    public static HttpRequestMessage? CreateMediaLookup(ProviderMediaRequest request, Uri versionedBase, out ProviderMediaResult? refusal)
    {
        refusal = null;
        if (!IsHeaderSafeToken(request.AccessToken))
        {
            refusal = new ProviderMediaResult(ProviderMediaOutcome.AccountError, null, null, null, "credential-invalid-format");
            return null;
        }

        if (request.ProviderMediaId is null || !MediaIdPattern.IsMatch(request.ProviderMediaId))
        {
            refusal = new ProviderMediaResult(ProviderMediaOutcome.Rejected, null, null, null, "invalid-media-id");
            return null;
        }

        return BuildGet(new Uri(versionedBase, request.ProviderMediaId), request.AccessToken, "application/json");
    }

    /// <summary>O download do conteúdo no endereço que o provedor devolveu (já validado pelo adaptador), com o token no cabeçalho como a documentação exige.</summary>
    public static HttpRequestMessage CreateMediaDownload(ProviderMediaRequest request, Uri contentAddress) => BuildGet(contentAddress, request.AccessToken, "*/*");

    /// <summary>O ÚNICO lugar que monta um GET (a sonda do número e as duas etapas da mídia): o token vai só no cabeçalho DESTA requisição.</summary>
    private static HttpRequestMessage BuildGet(Uri address, string accessToken, string accept)
    {
        var mensagem = new HttpRequestMessage(HttpMethod.Get, address);
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
