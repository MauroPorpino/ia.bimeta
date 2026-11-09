using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// O arquivo de uma mensagem de mídia (áudio, imagem ou documento), um por mensagem. Do CLIENTE (ADR 0028): nasce <see cref="AttachmentStatus.Pending"/> junto da mensagem, com o que o canal declarou; o Channels
/// busca o arquivo no provedor e o Conversations o valida e o guarda em armazenamento privado (<see cref="StorageKey"/> é opaca: nunca sai do módulo). Sai de <c>Pending</c> uma única vez: para
/// <c>Available</c>, <c>Unavailable</c> ou <c>Rejected</c>. Do ATENDENTE (ADR 0031, <see cref="MessageDirection.Outbound"/>): já nasce <c>Available</c> (o Conversations validou e guardou o arquivo antes de gravar a
/// mensagem) e só muda quando o prazo de guarda vence (<see cref="MarkExpired"/>). Nome e legenda são texto de pessoa: ficam saneados.
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

    /// <summary>De quem é o arquivo: o cliente mandou (entrada) ou o atendente enviou (saída).</summary>
    public MessageDirection Direction { get; private set; }

    public AttachmentStatus Status { get; private set; }

    /// <summary>O identificador da mídia no provedor: só para o job de busca (arquivo do cliente); nulo no arquivo do atendente. Nunca vai a uma visão.</summary>
    public string? ProviderMediaId { get; private set; }

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
            Direction = MessageDirection.Inbound,
            Status = AttachmentStatus.Pending,
            ProviderMediaId = identificador,
            DeclaredMimeType = AttachmentPolicy.NormalizeMimeType(source.DeclaredMimeType),
            FileName = AttachmentPolicy.SanitizeDisplayName(source.FileName),
            Caption = AttachmentPolicy.SanitizeCaption(source.Caption),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>O motivo gravado quando o prazo de guarda do arquivo enviado venceu.</summary>
    public const string RetentionFailureCode = "retention";

    /// <summary>
    /// O arquivo que o atendente enviou (ADR 0031): já validado pela <see cref="OutboundAttachmentPolicy"/> e guardado em <paramref name="storageKey"/>, então nasce <c>Available</c>, com o tipo REAL, o nome e a legenda
    /// saneados e o resumo. Não há identificador no provedor (o envio sobe o arquivo e usa o que o provedor devolver, sem guardá-lo).
    /// </summary>
    public static MessageAttachment CreateOutbound(TenantId tenantId, Guid id, Guid messageId, Guid sessionId, OutboundAttachmentVerdict verdict, long sizeBytes, string storageKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(verdict);
        if (!verdict.Accepted || verdict.Kind is not (MessageKind.Audio or MessageKind.Image or MessageKind.Document) || verdict.MimeType is null || verdict.FileName is null || verdict.Sha256 is null)
        {
            throw new ConversationsValidationException("Só um arquivo aceito pela política de envio vira arquivo de mensagem.");
        }

        if (id == Guid.Empty || sizeBytes < 1 || string.IsNullOrEmpty(storageKey) || storageKey.Length > StorageKeyMaxLength)
        {
            throw new ConversationsValidationException("O arquivo enviado precisa de identificador, tamanho e chave de armazenamento.");
        }

        return new MessageAttachment
        {
            Id = id,
            TenantId = tenantId,
            MessageId = messageId,
            SessionId = sessionId,
            Kind = verdict.Kind,
            Direction = MessageDirection.Outbound,
            Status = AttachmentStatus.Available,
            ProviderMediaId = null,
            DeclaredMimeType = null,
            MimeType = verdict.MimeType,
            FileName = verdict.FileName,
            Caption = verdict.Caption,
            SizeBytes = sizeBytes,
            Sha256 = verdict.Sha256,
            StorageKey = storageKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// O prazo de guarda do arquivo ENVIADO venceu e o conteúdo foi apagado: o arquivo passa a <see cref="AttachmentStatus.Unavailable"/> com o motivo <c>retention</c>, sem chave, tipo, tamanho nem resumo (o nome e a
    /// legenda ficam para o histórico dizer o que foi enviado). Só vale para o arquivo do atendente ainda disponível.
    /// </summary>
    public bool MarkExpired(DateTimeOffset now)
    {
        if (Direction != MessageDirection.Outbound || Status != AttachmentStatus.Available)
        {
            return false;
        }

        Status = AttachmentStatus.Unavailable;
        FailureCode = RetentionFailureCode;
        MimeType = null;
        SizeBytes = null;
        Sha256 = null;
        StorageKey = null;
        UpdatedAt = now;
        return true;
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
