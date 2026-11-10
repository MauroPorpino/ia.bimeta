using System.Security.Cryptography;
using System.Text;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Conversations.Domain;

/// <summary>O resultado de avaliar o arquivo que o atendente quer enviar: aceito (com o tipo REAL detectado no conteúdo, o tipo de mídia, o nome e a legenda saneados e o resumo) ou recusado com o código do motivo.</summary>
public sealed record OutboundAttachmentVerdict(bool Accepted, string? FailureCode, MessageKind Kind, string? MimeType, string? FileName, string? Caption, string? Sha256, bool ScanClean = false)
{
    public static OutboundAttachmentVerdict Reject(string code) => new(false, code, MessageKind.Other, null, null, null, null);
}

/// <summary>
/// A política de segurança do arquivo que o ATENDENTE envia ao cliente (ADR 0031), sem I/O. Ao contrário do que o cliente manda (onde o provedor declara o tipo), aqui quem declara é o navegador do atendente, então
/// nada do que ele informa é confiado: o tipo é o que o CONTEÚDO é (assinatura, estrutura do pacote Office, codificação do Ogg), o tipo de mídia (imagem, áudio ou documento) sai do tipo detectado, o tipo e a
/// extensão informados só desempatam (Word, Excel e PowerPoint antigos são a mesma assinatura) e, se contradizem o conteúdo, o arquivo é recusado. Só entra o que a Cloud API envia (JPEG e PNG; AAC, AMR, MP3, M4A e
/// Ogg Opus mono; PDF, texto, Word, Excel e PowerPoint), dentro das listas de <see cref="AttachmentOptions"/>. Não é um antivírus (ver <c>IAttachmentScanner</c>). Motivos fixos: <c>empty</c>, <c>too-large</c>,
/// <c>type-not-allowed</c>, <c>content-mismatch</c>, <c>audio-codec-unsupported</c>, <c>caption-not-supported</c>, <c>caption-too-long</c>.
/// </summary>
public static class OutboundAttachmentPolicy
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "image/jpeg", "image/png",
        "audio/aac", "audio/amr", "audio/mpeg", "audio/mp4", "audio/ogg",
        "application/pdf", "text/plain",
        "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    // A extensão de cada tipo (a que o nome final carrega) e as extensões que o atendente costuma usar para o mesmo tipo.
    private static readonly Dictionary<string, string[]> ExtensionsByType = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = [".jpg", ".jpeg"],
        ["image/png"] = [".png"],
        ["audio/aac"] = [".aac"],
        ["audio/amr"] = [".amr"],
        ["audio/mpeg"] = [".mp3"],
        ["audio/mp4"] = [".m4a", ".mp4"],
        ["audio/ogg"] = [".ogg", ".opus", ".oga"],
        ["application/pdf"] = [".pdf"],
        ["text/plain"] = [".txt"],
        ["application/msword"] = [".doc"],
        ["application/vnd.ms-excel"] = [".xls"],
        ["application/vnd.ms-powerpoint"] = [".ppt"],
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = [".docx"],
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = [".xlsx"],
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = [".pptx"],
    };

    private static readonly string[] OleTypes = ["application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint"];

    // Marcas do cabeçalho "ftyp" de um arquivo MP4 de áudio. Qualquer outra (imagem HEIC, AVIF, vídeo 3GP) não é áudio.
    private static readonly string[] AudioBrands = ["M4A ", "M4B ", "mp42", "mp41", "isom", "iso2"];

    /// <summary>Os tipos que o MVP sabe ENVIAR (um subconjunto do que sabe receber).</summary>
    public static bool IsSupported(string mimeType) => Supported.Contains(mimeType);

    public static OutboundAttachmentVerdict Evaluate(AttachmentOptions options, ReadOnlySpan<byte> content, string? declaredMimeType, string? fileName, string? caption)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (content.Length == 0)
        {
            return OutboundAttachmentVerdict.Reject("empty");
        }

        // O teto absoluto antes de qualquer análise: nada acima do maior limite configurado é examinado.
        if (content.Length > new[] { options.MaxImageBytes, options.MaxAudioBytes, options.MaxDocumentBytes }.Max())
        {
            return OutboundAttachmentVerdict.Reject("too-large");
        }

        var tipo = Detect(content, AttachmentPolicy.NormalizeMimeType(declaredMimeType), ExtensionOf(fileName), out var falha);
        if (tipo is null)
        {
            return OutboundAttachmentVerdict.Reject(falha ?? "type-not-allowed");
        }

        var kind = KindOf(tipo);
        if (content.Length > options.MaxBytesFor(kind))
        {
            return OutboundAttachmentVerdict.Reject("too-large");
        }

        if (!options.AllowedTypesFor(kind).Contains(tipo, StringComparer.Ordinal))
        {
            return OutboundAttachmentVerdict.Reject("type-not-allowed");
        }

        // A legenda: áudio não a tem (a recusa é explícita, nunca um descarte silencioso) e as demais cabem em 1024 caracteres (recusa em vez de cortar o que o atendente escreveu).
        var legenda = AttachmentPolicy.SanitizeCaption(caption);
        if (legenda is not null && kind == MessageKind.Audio)
        {
            return OutboundAttachmentVerdict.Reject("caption-not-supported");
        }

        if (caption is not null && caption.Trim().Length > MessageAttachment.CaptionMaxLength)
        {
            return OutboundAttachmentVerdict.Reject("caption-too-long");
        }

        var resumo = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new OutboundAttachmentVerdict(true, null, kind, tipo, FinalName(fileName, tipo), legenda, resumo);
    }

    /// <summary>O tipo de mídia (imagem, áudio ou documento) de um tipo detectado.</summary>
    public static MessageKind KindOf(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.Ordinal) ? MessageKind.Image
        : mimeType.StartsWith("audio/", StringComparison.Ordinal) ? MessageKind.Audio
        : MessageKind.Document;

    /// <summary>
    /// O tipo REAL do conteúdo, ou nulo com o motivo. O conteúdo decide; o tipo declarado e a extensão só desempatam o que a assinatura não separa e, se apontam para OUTRO tipo conhecido, o arquivo é recusado
    /// (<c>content-mismatch</c>): um PDF com nome de imagem, ou um pacote do Word declarado como planilha, não passa.
    /// </summary>
    private static string? Detect(ReadOnlySpan<byte> c, string? declared, string? extension, out string? failure)
    {
        failure = null;
        string? detectado;

        if (ContentSniffer.Matches("image/jpeg", c))
        {
            detectado = "image/jpeg";
        }
        else if (ContentSniffer.Matches("image/png", c))
        {
            detectado = "image/png";
        }
        else if (ContentSniffer.Matches("application/pdf", c))
        {
            detectado = "application/pdf";
        }
        else if (ContentSniffer.Matches("audio/ogg", c))
        {
            if (!IsMonoOpus(c))
            {
                failure = "audio-codec-unsupported";
                return null;
            }

            detectado = "audio/ogg";
        }
        else if (ContentSniffer.Matches("audio/amr", c))
        {
            detectado = "audio/amr";
        }
        else if (ContentSniffer.Matches("audio/mp4", c))
        {
            if (!IsAudioOnlyMp4(c))
            {
                // Vídeo, imagem HEIC e afins compartilham o cabeçalho: aqui só entra áudio.
                failure = "content-mismatch";
                return null;
            }

            detectado = "audio/mp4";
        }
        else if (ContentSniffer.Matches("application/msword", c))
        {
            // Word, Excel e PowerPoint antigos são o mesmo contêiner: o tipo declarado ou a extensão escolhem, e sem nenhum dos dois não há como saber.
            detectado = PickOle(declared, extension);
            if (detectado is null)
            {
                failure = "type-not-allowed";
                return null;
            }
        }
        else if (IsZipPackage(c))
        {
            detectado = OfficePackageType(c);
            if (detectado is null)
            {
                failure = "type-not-allowed";
                return null;
            }
        }
        else if (IsAdts(c))
        {
            detectado = "audio/aac";
        }
        else if (ContentSniffer.Matches("audio/mpeg", c))
        {
            detectado = "audio/mpeg";
        }
        else if (ContentSniffer.Matches("text/plain", c))
        {
            // Texto só se o nome diz que é texto: um script ou uma página com extensão de outra coisa não é "documento de texto".
            if (extension is not null && extension != ".txt")
            {
                failure = "content-mismatch";
                return null;
            }

            detectado = "text/plain";
        }
        else
        {
            failure = "type-not-allowed";
            return null;
        }

        if (!Supported.Contains(detectado))
        {
            failure = "type-not-allowed";
            return null;
        }

        // Consistência: o que o navegador disse e a extensão do nome não podem apontar para outro tipo conhecido.
        if (declared is not null && Supported.Contains(declared) && declared != detectado)
        {
            failure = "content-mismatch";
            return null;
        }

        if (extension is not null && ExtensionsByType.Any(par => par.Key != detectado && par.Value.Contains(extension)) && !ExtensionsByType[detectado].Contains(extension))
        {
            failure = "content-mismatch";
            return null;
        }

        return detectado;
    }

    private static string? PickOle(string? declared, string? extension)
    {
        if (declared is not null && OleTypes.Contains(declared, StringComparer.Ordinal))
        {
            return declared;
        }

        return extension switch
        {
            ".doc" => "application/msword",
            ".xls" => "application/vnd.ms-excel",
            ".ppt" => "application/vnd.ms-powerpoint",
            _ => null,
        };
    }

    private static bool IsZipPackage(ReadOnlySpan<byte> c) => c.Length >= 4 && c[0] == 0x50 && c[1] == 0x4B && c[2] == 0x03 && c[3] == 0x04;

    /// <summary>O pacote do Office pela estrutura: tem de ser OOXML (<c>[Content_Types].xml</c>) e ter UMA pasta própria (<c>word/</c>, <c>xl/</c> ou <c>ppt/</c>). Qualquer outro ZIP não é documento.</summary>
    private static string? OfficePackageType(ReadOnlySpan<byte> c)
    {
        if (!ContentSniffer.Matches("application/vnd.openxmlformats-officedocument.wordprocessingml.document", c))
        {
            return null;
        }

        var palavra = c.IndexOf("word/"u8) >= 0;
        var planilha = c.IndexOf("xl/"u8) >= 0;
        var apresentacao = c.IndexOf("ppt/"u8) >= 0;
        return (palavra, planilha, apresentacao) switch
        {
            (true, false, false) => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            (false, true, false) => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            (false, false, true) => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => null,
        };
    }

    /// <summary>ADTS: o sincronismo de 12 bits e a camada 0 (o que separa o AAC do MP3, que tem a camada 1, 2 ou 3).</summary>
    private static bool IsAdts(ReadOnlySpan<byte> c) => c.Length >= 2 && c[0] == 0xFF && (c[1] & 0xF6) == 0xF0;

    /// <summary>A Cloud API só aceita Ogg com a codificação Opus e entrada mono: confere o cabeçalho <c>OpusHead</c> (versão, número de canais) no começo do arquivo.</summary>
    private static bool IsMonoOpus(ReadOnlySpan<byte> c)
    {
        var janela = c[..Math.Min(c.Length, 512)];
        var posicao = janela.IndexOf("OpusHead"u8);
        return posicao >= 0 && janela.Length > posicao + 9 && janela[posicao + 9] == 1;
    }

    /// <summary>MP4 de áudio: a marca do <c>ftyp</c> é de áudio e nenhuma faixa tem o tratador de vídeo (<c>hdlr</c> com <c>vide</c>).</summary>
    private static bool IsAudioOnlyMp4(ReadOnlySpan<byte> c)
    {
        if (c.Length < 12)
        {
            return false;
        }

        var temMarcaDeAudio = false;
        foreach (var marca in AudioBrands)
        {
            if (c.Slice(8, 4).SequenceEqual(Encoding.ASCII.GetBytes(marca)))
            {
                temMarcaDeAudio = true;
                break;
            }
        }

        if (!temMarcaDeAudio)
        {
            return false;
        }

        var resto = c;
        var deslocamento = 0;
        while (true)
        {
            var i = resto.IndexOf("hdlr"u8);
            if (i < 0)
            {
                return true;
            }

            var tipoDoTratador = deslocamento + i + 4 + 8;
            if (tipoDoTratador + 4 <= c.Length && c.Slice(tipoDoTratador, 4).SequenceEqual("vide"u8))
            {
                return false;
            }

            deslocamento += i + 4;
            resto = c[deslocamento..];
        }
    }

    private static string? ExtensionOf(string? fileName)
    {
        var nome = AttachmentPolicy.SanitizeDisplayName(fileName);
        if (nome is null)
        {
            return null;
        }

        var extensao = Path.GetExtension(nome).ToLowerInvariant();
        return extensao.Length is > 1 and <= 10 ? extensao : null;
    }

    /// <summary>
    /// O nome final: o do atendente saneado, com a extensão do tipo REAL quando ele não tem uma (ou tem uma desconhecida). A extensão de outro tipo conhecido já foi recusada na detecção, então o nome nunca
    /// promete um tipo que o conteúdo não é.
    /// </summary>
    private static string FinalName(string? fileName, string mimeType)
    {
        var extensoes = ExtensionsByType[mimeType];
        var nome = AttachmentPolicy.SanitizeDisplayName(fileName);
        if (nome is null)
        {
            return "arquivo" + extensoes[0];
        }

        if (extensoes.Contains(Path.GetExtension(nome).ToLowerInvariant()))
        {
            return nome;
        }

        // Sem extensão (ou com uma que não é de nenhum tipo conhecido, como "contrato 2024.01"): acrescenta a do tipo real, mantendo o que o atendente escreveu.
        var final = nome + extensoes[0];
        return final.Length > 120 ? nome[..(120 - extensoes[0].Length)] + extensoes[0] : final;
    }
}
