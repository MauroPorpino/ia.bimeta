using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// O arquivo de uma mensagem de mídia do cliente (áudio, imagem ou documento), um por mensagem (ADR 0028). Nasce <see cref="AttachmentStatus.Pending"/> junto da mensagem, com o que o canal declarou; o Channels
/// busca o arquivo no provedor e o Conversations o valida e o guarda em armazenamento privado (<see cref="StorageKey"/> é opaca: nunca sai do módulo). Sai de <c>Pending</c> uma única vez: para
/// <c>Available</c>, <c>Unavailable</c> ou <c>Rejected</c>. Nome e legenda são texto do cliente: ficam saneados.
/// </summary>
public sealed class MessageAttachment : ITenantOwned
{
    public const int ProviderMediaIdMaxLength = 256;
    public const int MimeTypeMaxLength = 127;
    public const int FileNameMaxLength = 255;
    public const int CaptionMaxLength = 1024;
    public const int FailureCodeMaxLength = 64;
    public const int StorageKeyMaxLength = 200;

    private MessageAttachment()
    {
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; } = null!;

    public Guid MessageId { get; private set; }

    public Guid SessionId { get; private set; }

    public MessageKind Kind { get; private set; }

    public AttachmentStatus Status { get; private set; }

    /// <summary>O identificador da mídia no provedor: só para o job de busca; nunca vai a uma visão.</summary>
    public string ProviderMediaId { get; private set; } = string.Empty;

    /// <summary>O tipo que o provedor declarou na mensagem (não verificado).</summary>
    public string? DeclaredMimeType { get; private set; }

    /// <summary>O tipo VERIFICADO contra o conteúdo; só com o arquivo disponível.</summary>
    public string? MimeType { get; private set; }

    public string? FileName { get; private set; }

    public string? Caption { get; private set; }

    public long? SizeBytes { get; private set; }

    /// <summary>O SHA-256 do que foi guardado (hexadecimal minúsculo).</summary>
    public string? Sha256 { get; private set; }

    /// <summary>A chave opaca do conteúdo no armazenamento privado; só com o arquivo disponível.</summary>
    public string? StorageKey { get; private set; }

    public string? FailureCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static MessageAttachment CreatePending(TenantId tenantId, Guid messageId, Guid sessionId, MessageKind kind, InboundAttachment source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(source);
        if (kind is not (MessageKind.Audio or MessageKind.Image or MessageKind.Document))
        {
            throw new ConversationsValidationException("Só áudio, imagem e documento têm arquivo.");
        }

        var identificador = source.ProviderMediaId;
        if (string.IsNullOrEmpty(identificador) || identificador.Length > ProviderMediaIdMaxLength || identificador.Any(c => c is < '!' or > '~'))
        {
            throw new ConversationsValidationException("O identificador da mídia no provedor é inválido.");
        }

        return new MessageAttachment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            MessageId = messageId,
            SessionId = sessionId,
            Kind = kind,
            Status = AttachmentStatus.Pending,
            ProviderMediaId = identificador,
            DeclaredMimeType = AttachmentPolicy.NormalizeMimeType(source.DeclaredMimeType),
            FileName = AttachmentPolicy.SanitizeDisplayName(source.FileName),
            Caption = AttachmentPolicy.SanitizeCaption(source.Caption),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>O arquivo passou na política e foi guardado. Só de <c>Pending</c>.</summary>
    public bool MarkAvailable(string mimeType, string fileName, long sizeBytes, string sha256, string storageKey, DateTimeOffset now)
    {
        if (Status != AttachmentStatus.Pending)
        {
            return false;
        }

        Status = AttachmentStatus.Available;
        MimeType = mimeType;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        FailureCode = null;
        UpdatedAt = now;
        return true;
    }

    /// <summary>O arquivo não ficou disponível (<see cref="AttachmentStatus.Unavailable"/> ou <see cref="AttachmentStatus.Rejected"/>). Só de <c>Pending</c>.</summary>
    public bool MarkFailed(AttachmentStatus status, string code, DateTimeOffset now)
    {
        if (status is not (AttachmentStatus.Unavailable or AttachmentStatus.Rejected))
        {
            throw new ConversationsValidationException("Um arquivo que falha fica indisponível ou recusado.");
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > FailureCodeMaxLength || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ConversationsValidationException("O código do motivo é inválido.");
        }

        if (Status != AttachmentStatus.Pending)
        {
            return false;
        }

        Status = status;
        FailureCode = code;
        UpdatedAt = now;
        return true;
    }
}
