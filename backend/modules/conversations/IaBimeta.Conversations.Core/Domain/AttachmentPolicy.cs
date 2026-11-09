using System.Security.Cryptography;
using System.Text;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Conversations.Domain;

/// <summary>O resultado de avaliar um arquivo baixado: aceito (com o tipo verificado, o nome saneado e o resumo) ou recusado com o código do motivo.</summary>
public sealed record AttachmentVerdict(bool Accepted, string? FailureCode, string? MimeType, string? FileName, string? Sha256)
{
    public static AttachmentVerdict Reject(string code) => new(false, code, null, null, null);
}

/// <summary>
/// A política de segurança de um arquivo de mídia recebido (ADR 0028), sem I/O: tamanho (máximo por tipo, e vazio recusado), tipo permitido por tipo de mídia, ASSINATURA do conteúdo conferida contra o tipo (um
/// arquivo não é aceito com o tipo errado), integridade contra o resumo que o provedor informou e nome saneado. Não é um antivírus (o MVP não tem mecanismo de varredura: ver <c>IAttachmentScanner</c>). O tipo
/// efetivo é o que o provedor informou ao entregar o arquivo; na falta dele, o declarado na mensagem.
/// </summary>
public static class AttachmentPolicy
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.Ordinal)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["audio/aac"] = ".aac",
        ["audio/amr"] = ".amr",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/ogg"] = ".ogg",
        ["application/pdf"] = ".pdf",
        ["text/plain"] = ".txt",
        ["application/msword"] = ".doc",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
    };

    public static AttachmentVerdict Evaluate(
        AttachmentOptions options, MessageKind kind, ReadOnlySpan<byte> content, string? providerMimeType, string? declaredMimeType, string? providerSha256, string? declaredFileName)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (content.Length == 0)
        {
            return AttachmentVerdict.Reject("empty");
        }

        if (content.Length > options.MaxBytesFor(kind))
        {
            return AttachmentVerdict.Reject("too-large");
        }

        var tipo = NormalizeMimeType(providerMimeType) ?? NormalizeMimeType(declaredMimeType);
        if (tipo is null || !options.AllowedTypesFor(kind).Contains(tipo, StringComparer.Ordinal))
        {
            return AttachmentVerdict.Reject("type-not-allowed");
        }

        if (!ContentSniffer.Matches(tipo, content))
        {
            return AttachmentVerdict.Reject("content-mismatch");
        }

        var resumo = SHA256.HashData(content);
        if (ParseSha256(providerSha256) is { } informado && !CryptographicOperations.FixedTimeEquals(informado, resumo))
        {
            return AttachmentVerdict.Reject("hash-mismatch");
        }

        return new AttachmentVerdict(true, null, tipo, FileNameFor(declaredFileName, tipo), Convert.ToHexString(resumo).ToLowerInvariant());
    }

    /// <summary>O tipo sem parâmetros (<c>audio/ogg; codecs=opus</c> vira <c>audio/ogg</c>), em minúsculas; nulo se não tem a forma <c>tipo/subtipo</c>.</summary>
    public static string? NormalizeMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        var semParametros = mimeType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (semParametros.Length is 0 or > MessageAttachment.MimeTypeMaxLength || semParametros.Count(c => c == '/') != 1)
        {
            return null;
        }

        return semParametros.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '+') ? semParametros : null;
    }

    /// <summary>O resumo SHA-256 informado pelo provedor: 64 dígitos hexadecimais ou Base64 de 32 bytes. Outro formato: nulo (sem integridade para conferir).</summary>
    public static byte[]? ParseSha256(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var limpo = texto.Trim();
        try
        {
            if (limpo.Length == 64 && limpo.All(char.IsAsciiHexDigit))
            {
                return Convert.FromHexString(limpo);
            }

            var bytes = Convert.FromBase64String(limpo);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>O nome para exibir e para o cabeçalho de download: só o último trecho, sem caractere de controle nem separador de caminho, sem caractere que um sistema de arquivos recusa e com tamanho limitado.</summary>
    public static string? SanitizeDisplayName(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
        {
            return null;
        }

        var ultimo = nome.Split('/', '\\')[^1];
        var limpo = new StringBuilder();
        foreach (var c in ultimo)
        {
            if (char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '‮' or '‭' or '‎' or '‏')
            {
                continue;
            }

            limpo.Append(c);
        }

        var texto = limpo.ToString().Trim().Trim('.').Trim();
        if (texto.Length == 0)
        {
            return null;
        }

        if (texto.Length > 120)
        {
            var extensao = Path.GetExtension(texto);
            texto = extensao.Length is > 0 and <= 10 ? texto[..(120 - extensao.Length)] + extensao : texto[..120];
        }

        return texto;
    }

    /// <summary>O nome final do arquivo: o do cliente saneado, com a extensão do tipo verificado quando ele não tem uma; sem nome, <c>arquivo</c> mais a extensão.</summary>
    public static string FileNameFor(string? declaredFileName, string verifiedMimeType)
    {
        var extensao = Extensions.TryGetValue(verifiedMimeType, out var e) ? e : string.Empty;
        var nome = SanitizeDisplayName(declaredFileName);
        if (nome is null)
        {
            return "arquivo" + extensao;
        }

        return Path.HasExtension(nome) || extensao.Length == 0 ? nome : nome + extensao;
    }

    /// <summary>A legenda do cliente: sem caractere de controle (exceto quebra de linha) e com tamanho limitado.</summary>
    public static string? SanitizeCaption(string? legenda)
    {
        if (string.IsNullOrWhiteSpace(legenda))
        {
            return null;
        }

        var limpa = new string(legenda.Where(c => !char.IsControl(c) || c is '\n').ToArray()).Trim();
        if (limpa.Length == 0)
        {
            return null;
        }

        return limpa.Length > MessageAttachment.CaptionMaxLength ? limpa[..MessageAttachment.CaptionMaxLength] : limpa;
    }
}
