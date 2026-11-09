using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Configuração dos arquivos de mídia (<c>Conversations:Attachments</c>, ADR 0028): onde ficam guardados, o tamanho máximo e os tipos permitidos por tipo de mídia. Os padrões seguem os limites e os tipos que a
/// documentação oficial do WhatsApp lista (imagem 5 MB, áudio 16 MB; documento até 100 MB, aqui 20 MB para não carregar arquivo grande em memória); tudo é configurável, com teto absoluto de 100 MB.
/// </summary>
public sealed class AttachmentOptions
{
    /// <summary>O teto absoluto de qualquer tipo (o limite de mídia da Cloud API).</summary>
    public const long HardMaxBytes = 100L * 1024 * 1024;

    private static readonly string[] DefaultImageTypes = ["image/jpeg", "image/png", "image/webp"];

    private static readonly string[] DefaultAudioTypes = ["audio/aac", "audio/amr", "audio/mpeg", "audio/mp4", "audio/ogg"];

    private static readonly string[] DefaultDocumentTypes =
    [
        "application/pdf",
        "text/plain",
        "application/msword",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    ];

    /// <summary>
    /// A pasta do armazenamento PRIVADO do sistema de arquivos (fora de qualquer pasta servida; nenhuma URL aponta para ela). Sem valor, usa <c>LocalApplicationData/IaBimeta/attachments</c>. Api e Worker precisam apontar
    /// para a MESMA pasta (o Worker guarda, o Api serve). Trocar o armazenamento (S3, Azure Blob) é trocar a implementação de <c>IAttachmentContentStore</c>.
    /// </summary>
    public string? RootPath { get; set; }

    public long MaxImageBytes { get; set; } = 5L * 1024 * 1024;

    public long MaxAudioBytes { get; set; } = 16L * 1024 * 1024;

    public long MaxDocumentBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Tipos de imagem aceitos. Vazio ou nulo, os padrões.</summary>
    public List<string>? AllowedImageTypes { get; set; }

    public List<string>? AllowedAudioTypes { get; set; }

    public List<string>? AllowedDocumentTypes { get; set; }

    public long MaxBytesFor(MessageKind kind) => kind switch
    {
        MessageKind.Image => MaxImageBytes,
        MessageKind.Audio => MaxAudioBytes,
        MessageKind.Document => MaxDocumentBytes,
        _ => 0,
    };

    public IReadOnlyList<string> AllowedTypesFor(MessageKind kind)
    {
        var configurados = kind switch
        {
            MessageKind.Image => AllowedImageTypes,
            MessageKind.Audio => AllowedAudioTypes,
            MessageKind.Document => AllowedDocumentTypes,
            _ => null,
        };

        var padrao = kind switch
        {
            MessageKind.Image => DefaultImageTypes,
            MessageKind.Audio => DefaultAudioTypes,
            MessageKind.Document => DefaultDocumentTypes,
            _ => Array.Empty<string>(),
        };

        return configurados is { Count: > 0 }
            ? configurados.Select(t => AttachmentPolicy.NormalizeMimeType(t) ?? string.Empty).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList()
            : padrao;
    }

    /// <summary>Confere a configuração na partida: tamanhos de 1 byte ao teto e tipos num formato conhecido.</summary>
    /// <exception cref="InvalidOperationException">Algum limite ou tipo é inválido.</exception>
    public void Validate()
    {
        foreach (var (nome, valor) in new[] { ("MaxImageBytes", MaxImageBytes), ("MaxAudioBytes", MaxAudioBytes), ("MaxDocumentBytes", MaxDocumentBytes) })
        {
            if (valor is < 1 or > HardMaxBytes)
            {
                throw new InvalidOperationException($"Conversations:Attachments:{nome} deve ficar entre 1 e {HardMaxBytes} bytes.");
            }
        }

        foreach (var tipo in new[] { MessageKind.Image, MessageKind.Audio, MessageKind.Document })
        {
            var configurados = tipo switch
            {
                MessageKind.Image => AllowedImageTypes,
                MessageKind.Audio => AllowedAudioTypes,
                _ => AllowedDocumentTypes,
            };
            foreach (var texto in configurados ?? [])
            {
                var normalizado = AttachmentPolicy.NormalizeMimeType(texto);
                if (normalizado is null || !ContentSniffer.IsSupported(normalizado))
                {
                    throw new InvalidOperationException($"Conversations:Attachments: o tipo '{texto}' não é suportado para {tipo} (só os que a validação de conteúdo sabe conferir).");
                }
            }
        }
    }
}
