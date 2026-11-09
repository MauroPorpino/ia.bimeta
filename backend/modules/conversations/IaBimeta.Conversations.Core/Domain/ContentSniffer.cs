using System.Text;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Confere se o CONTEÚDO de um arquivo é o que o tipo declarado diz (a "assinatura" do arquivo), sem biblioteca de terceiros. Não é um antivírus: só impede que um arquivo apareça com o tipo errado (por exemplo,
/// texto ou HTML com extensão e tipo de imagem). Só os tipos que sabe conferir são aceitos pela configuração.
/// </summary>
public static class ContentSniffer
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ContentTypesPart = Encoding.ASCII.GetBytes("[Content_Types].xml");

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "image/jpeg", "image/png", "image/webp",
        "audio/aac", "audio/amr", "audio/mpeg", "audio/mp4", "audio/ogg",
        "application/pdf", "text/plain", "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static bool IsSupported(string mimeType) => Supported.Contains(mimeType);

    /// <summary>O conteúdo tem a assinatura do tipo? Tipo desconhecido: falso.</summary>
    public static bool Matches(string mimeType, ReadOnlySpan<byte> c)
    {
        switch (mimeType)
        {
            case "image/jpeg":
                return c.Length >= 4 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF;
            case "image/png":
                return c.Length >= Png.Length && c[..Png.Length].SequenceEqual(Png);
            case "image/webp":
                return c.Length >= 12 && c[..4].SequenceEqual("RIFF"u8) && c.Slice(8, 4).SequenceEqual("WEBP"u8);
            case "audio/ogg":
                return c.Length >= 4 && c[..4].SequenceEqual("OggS"u8);
            case "audio/amr":
                return c.Length >= 5 && c[..5].SequenceEqual("#!AMR"u8);
            case "audio/mp4":
                return c.Length >= 12 && c.Slice(4, 4).SequenceEqual("ftyp"u8);
            case "audio/mpeg":
                return c.Length >= 3 && (c[..3].SequenceEqual("ID3"u8) || (c[0] == 0xFF && (c[1] & 0xE0) == 0xE0));
            case "audio/aac":
                // ADTS: sincronismo de 12 bits e camada 0.
                return c.Length >= 2 && c[0] == 0xFF && (c[1] & 0xF6) == 0xF0;
            case "application/pdf":
                return c[..Math.Min(c.Length, 1024)].IndexOf("%PDF-"u8) >= 0;
            case "application/msword" or "application/vnd.ms-excel" or "application/vnd.ms-powerpoint":
                return c.Length >= Ole.Length && c[..Ole.Length].SequenceEqual(Ole);
            case "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                or "application/vnd.openxmlformats-officedocument.presentationml.presentation":
                // Um pacote OOXML é um ZIP com a parte [Content_Types].xml (o nome aparece no cabeçalho local e no diretório central).
                return c.Length >= 4 && c[0] == 0x50 && c[1] == 0x4B && c[2] == 0x03 && c[3] == 0x04 && c.IndexOf(ContentTypesPart) >= 0;
            case "text/plain":
                // Texto: sem byte nulo nos primeiros 8 KB (um binário não é texto).
                return c[..Math.Min(c.Length, 8192)].IndexOf((byte)0) < 0;
            default:
                return false;
        }
    }
}
