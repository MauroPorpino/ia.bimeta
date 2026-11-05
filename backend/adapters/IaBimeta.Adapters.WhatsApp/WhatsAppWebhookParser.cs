using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// Normaliza o webhook do objeto <c>whatsapp_business_account</c> (campo <c>messages</c>): <c>entry[].changes[].value</c> com <c>metadata.phone_number_id</c>, <c>contacts[]</c>, <c>messages[]</c> e
/// <c>statuses[]</c>. Regras que valem acima do resto:
/// <list type="bullet">
/// <item>Nunca lança por entrada inválida: JSON inválido ou forma desconhecida (outro objeto, sem <c>entry</c>) devolve nulo; o que falta numa mensagem ou num status descarta SÓ aquele item e o conta em
/// <see cref="ParsedWebhook.IgnoredEntries"/>, sem perder o resto do lote.</item>
/// <item>Mídia: só METADADOS (<c>media_id</c>, <c>mime_type</c>, <c>caption</c>...), em <see cref="ProviderInboundMessage.ChannelData"/>. Nada é baixado, e a <c>url</c> e o <c>sha256</c> da mídia nem são lidos.</item>
/// <item>Nada do conteúdo do provedor vira exceção, log ou código de erro; do status de falha sai só o CÓDIGO (<c>errors[0].code</c>), nunca <c>title</c>, <c>message</c> nem <c>error_data</c>.</item>
/// <item>Sem premissa de ordem nem de unicidade: a ordem do lote não é garantida pela Meta, e o reenvio pode duplicar (ADR 0011, A.2, itens 1 e 4). A deduplicação é do Channels.</item>
/// </list>
/// </summary>
internal static class WhatsAppWebhookParser
{
    private const string WhatsAppObject = "whatsapp_business_account";
    private const int MaxIdentifierLength = 512;
    private const int MaxMetadataLength = 4096;
    private const long MaxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z, o teto do DateTimeOffset.

    private static readonly Regex ProviderTypePattern = new("^[a-z0-9_]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedWebhook? Parse(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object
                || Text(raiz, "object") != WhatsAppObject
                || !raiz.TryGetProperty("entry", out var entradas)
                || entradas.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var mensagens = new List<ProviderInboundMessage>();
            var statuses = new List<ProviderStatusUpdate>();
            var ignorados = 0;

            foreach (var entrada in entradas.EnumerateArray())
            {
                if (entrada.ValueKind != JsonValueKind.Object || !entrada.TryGetProperty("changes", out var mudancas) || mudancas.ValueKind != JsonValueKind.Array)
                {
                    ignorados++;
                    continue;
                }

                foreach (var mudanca in mudancas.EnumerateArray())
                {
                    ignorados += ParseChange(mudanca, mensagens, statuses);
                }
            }

            return new ParsedWebhook(mensagens, statuses, ignorados);
        }
    }

    /// <summary>Lê uma mudança e devolve quantos itens reconheceu e descartou.</summary>
    private static int ParseChange(JsonElement mudanca, List<ProviderInboundMessage> mensagens, List<ProviderStatusUpdate> statuses)
    {
        // Outros campos do aplicativo (modelos, qualidade, conta...) são reconhecidos e descartados.
        if (mudanca.ValueKind != JsonValueKind.Object
            || Text(mudanca, "field") != "messages"
            || !mudanca.TryGetProperty("value", out var valor)
            || valor.ValueKind != JsonValueKind.Object)
        {
            return 1;
        }

        var listaDeMensagens = ArrayOf(valor, "messages");
        var listaDeStatuses = ArrayOf(valor, "statuses");
        if (listaDeMensagens is null && listaDeStatuses is null)
        {
            return 1;
        }

        var quantidade = (listaDeMensagens?.GetArrayLength() ?? 0) + (listaDeStatuses?.GetArrayLength() ?? 0);

        // Sem o número de origem (Phone Number ID) nada do lote é roteável: tudo descartado e contado.
        var numero = valor.TryGetProperty("metadata", out var metadados) && metadados.ValueKind == JsonValueKind.Object ? Identifier(metadados, "phone_number_id") : null;
        if (numero is null)
        {
            return Math.Max(1, quantidade);
        }

        var ignorados = 0;
        var nomes = ReadContacts(valor);

        if (listaDeMensagens is { } lista)
        {
            foreach (var item in lista.EnumerateArray())
            {
                var mensagem = ParseMessage(item, numero, nomes);
                if (mensagem is null)
                {
                    ignorados++;
                }
                else
                {
                    mensagens.Add(mensagem);
                }
            }
        }

        if (listaDeStatuses is { } listaStatuses)
        {
            foreach (var item in listaStatuses.EnumerateArray())
            {
                var status = ParseStatus(item, numero);
                if (status is null)
                {
                    ignorados++;
                }
                else
                {
                    statuses.Add(status);
                }
            }
        }

        return ignorados;
    }

    /// <summary>O nome de exibição por <c>wa_id</c> (o primeiro de cada contato vale).</summary>
    private static Dictionary<string, string> ReadContacts(JsonElement valor)
    {
        var nomes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ArrayOf(valor, "contacts") is not { } contatos)
        {
            return nomes;
        }

        foreach (var contato in contatos.EnumerateArray())
        {
            if (contato.ValueKind != JsonValueKind.Object
                || Identifier(contato, "wa_id") is not { } waId
                || !contato.TryGetProperty("profile", out var perfil)
                || perfil.ValueKind != JsonValueKind.Object
                || Text(perfil, "name") is not { } nome
                || string.IsNullOrWhiteSpace(nome)
                || nome.Length > MaxIdentifierLength)
            {
                continue;
            }

            nomes.TryAdd(waId, nome);
        }

        return nomes;
    }

    // Mensagens

    private static ProviderInboundMessage? ParseMessage(JsonElement item, string numero, Dictionary<string, string> nomes)
    {
        if (item.ValueKind != JsonValueKind.Object
            || Identifier(item, "id") is not { } id
            || Identifier(item, "from") is not { } origem
            || !TryTimestamp(item, out var instante)
            || Text(item, "type") is not { } tipo)
        {
            return null;
        }

        var nome = nomes.TryGetValue(origem, out var encontrado) ? encontrado : null;

        switch (tipo)
        {
            case "text":
                if (!item.TryGetProperty("text", out var texto) || texto.ValueKind != JsonValueKind.Object || Text(texto, "body") is not { } corpo)
                {
                    return null;
                }

                return new ProviderInboundMessage(numero, id, origem, nome, instante, ProviderMessageKind.Text, corpo);

            case "audio" or "voice":
                return Media(item, tipo, ProviderMessageKind.Audio, numero, id, origem, nome, instante);
            case "image":
                return Media(item, tipo, ProviderMessageKind.Image, numero, id, origem, nome, instante);
            case "document":
                return Media(item, tipo, ProviderMessageKind.Document, numero, id, origem, nome, instante);
            case "video":
                return Media(item, tipo, ProviderMessageKind.Video, numero, id, origem, nome, instante);

            default:
                // Sem tratamento no MVP (figurinha, localização, contato, reação, interativo, tipo não suportado...): vira conteúdo indisponível, com o nome do tipo para o atendente e a métrica.
                var tipoSeguro = ProviderTypePattern.IsMatch(tipo) ? tipo : "unknown";
                return new ProviderInboundMessage(numero, id, origem, nome, instante, ProviderMessageKind.Other, null, new Dictionary<string, string> { ["provider_type"] = tipoSeguro });
        }
    }

    /// <summary>Uma mensagem de mídia: só os metadados. O <c>id</c> da mídia é obrigatório (sem ele não há o que o atendente consulte); o resto é opcional.</summary>
    private static ProviderInboundMessage? Media(JsonElement item, string chave, ProviderMessageKind tipo, string numero, string id, string origem, string? nome, DateTimeOffset instante)
    {
        if (!item.TryGetProperty(chave, out var midia) || midia.ValueKind != JsonValueKind.Object || Identifier(midia, "id") is not { } mediaId)
        {
            return null;
        }

        var dados = new Dictionary<string, string>(StringComparer.Ordinal) { ["media_id"] = mediaId };
        AddMetadata(dados, "mime_type", Text(midia, "mime_type"));

        // A legenda (e o nome do arquivo, no documento) são texto do cliente: seguem como METADADO e a legenda também como texto da mensagem, que é onde o contrato a espera.
        var legenda = Text(midia, "caption");
        var textoDaLegenda = !string.IsNullOrEmpty(legenda) && legenda.Length <= MaxMetadataLength ? legenda : null;
        AddMetadata(dados, "caption", textoDaLegenda);

        if (tipo == ProviderMessageKind.Document)
        {
            AddMetadata(dados, "filename", Text(midia, "filename"));
        }

        if (tipo == ProviderMessageKind.Audio && midia.TryGetProperty("voice", out var voz) && voz.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            dados["voice"] = voz.ValueKind == JsonValueKind.True ? "true" : "false";
        }

        return new ProviderInboundMessage(numero, id, origem, nome, instante, tipo, textoDaLegenda, dados);
    }

    private static void AddMetadata(Dictionary<string, string> dados, string chave, string? valor)
    {
        if (!string.IsNullOrEmpty(valor) && valor.Length <= MaxMetadataLength)
        {
            dados[chave] = valor;
        }
    }

    // Status

    private static ProviderStatusUpdate? ParseStatus(JsonElement item, string numero)
    {
        if (item.ValueKind != JsonValueKind.Object
            || Identifier(item, "id") is not { } id
            || !TryTimestamp(item, out var instante)
            || Status(Text(item, "status")) is not { } status)
        {
            return null;
        }

        // O dado opaco que o Channels mandou no envio; se não for texto, é como se o provedor não o tivesse devolvido.
        var dadoOpaco = Text(item, "biz_opaque_callback_data");
        if (string.IsNullOrEmpty(dadoOpaco) || dadoOpaco.Length > MaxMetadataLength)
        {
            dadoOpaco = null;
        }

        return new ProviderStatusUpdate(numero, id, status, instante, dadoOpaco, ReadErrorCode(item));
    }

    /// <summary>
    /// <c>played</c> (voz tocada) e qualquer estado desconhecido não têm equivalente no contrato do MVP: o item é reconhecido e descartado (o estado <c>read</c> do WhatsApp já cobre a leitura de texto e mídia).
    /// </summary>
    private static ProviderDeliveryStatus? Status(string? estado) => estado switch
    {
        "sent" => ProviderDeliveryStatus.Sent,
        "delivered" => ProviderDeliveryStatus.Delivered,
        "read" => ProviderDeliveryStatus.Read,
        "failed" => ProviderDeliveryStatus.Failed,
        _ => null,
    };

    /// <summary>Só o CÓDIGO de <c>errors[0]</c> (número inteiro ou texto de dígitos), como texto: nunca <c>title</c>, <c>message</c> nem <c>error_data</c>, que podem ecoar dados.</summary>
    private static string? ReadErrorCode(JsonElement status)
    {
        if (ArrayOf(status, "errors") is not { } erros || erros.GetArrayLength() == 0 || erros[0].ValueKind != JsonValueKind.Object || !erros[0].TryGetProperty("code", out var codigo))
        {
            return null;
        }

        if (codigo.ValueKind == JsonValueKind.Number && codigo.TryGetInt32(out var numero) && numero >= 0)
        {
            return numero.ToString(CultureInfo.InvariantCulture);
        }

        return codigo.ValueKind == JsonValueKind.String && codigo.GetString() is { Length: >= 1 and <= 9 } texto && texto.All(char.IsAsciiDigit) ? texto : null;
    }

    // Auxiliares

    /// <summary>O instante em segundos Unix: texto de dígitos (a forma documentada: <c>"timestamp": "1749416383"</c>) ou número inteiro, positivo e dentro do intervalo de <see cref="DateTimeOffset"/>.</summary>
    private static bool TryTimestamp(JsonElement objeto, out DateTimeOffset instante)
    {
        instante = default;
        if (!objeto.TryGetProperty("timestamp", out var valor))
        {
            return false;
        }

        long segundos;
        switch (valor.ValueKind)
        {
            case JsonValueKind.String:
                if (!long.TryParse(Text(objeto, "timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out segundos))
                {
                    return false;
                }

                break;
            case JsonValueKind.Number:
                if (!valor.TryGetInt64(out segundos))
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        if (segundos is <= 0 or > MaxUnixSeconds)
        {
            return false;
        }

        instante = DateTimeOffset.FromUnixTimeSeconds(segundos);
        return true;
    }

    private static JsonElement? ArrayOf(JsonElement objeto, string nome) =>
        objeto.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.Array ? valor : null;

    /// <summary>Um identificador do provedor: texto não vazio, até 512 caracteres, sem espaço nem controle.</summary>
    private static string? Identifier(JsonElement objeto, string nome)
    {
        var texto = Text(objeto, nome);
        if (string.IsNullOrEmpty(texto) || texto.Length > MaxIdentifierLength)
        {
            return null;
        }

        foreach (var c in texto)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return null;
            }
        }

        return texto;
    }

    /// <summary>O texto de uma propriedade. Nulo se falta, não é texto, ou tem um escape de substituto UTF-16 sem par (o leitor lança): só aquele item é descartado.</summary>
    private static string? Text(JsonElement objeto, string nome)
    {
        if (!objeto.TryGetProperty(nome, out var valor) || valor.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return valor.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
