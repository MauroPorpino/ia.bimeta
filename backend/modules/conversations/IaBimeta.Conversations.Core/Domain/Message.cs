using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Mensagem de uma sessão, do cliente (<see cref="MessageDirection.Inbound"/>) ou de saída (resposta da IA, de atendente ou
/// reenvio). A chave é <c>(tenant_id, id)</c> (TEN-2 e TEN-3). A ordem do histórico é <c>(OccurredAt, MessageSeq)</c>, com
/// <c>MessageSeq</c> único por sessão. Cada mensagem do cliente tem ainda o <c>InboundSeq</c> e a chave de idempotência do
/// provedor, única por canal.
/// </summary>
public sealed class Message : ITenantOwned
{
    private Message()
    {
        TenantId = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid ConversationId { get; private set; }

    public ChannelInstanceId ChannelInstanceId { get; private set; }

    public MessageDirection Direction { get; private set; }

    public long MessageSeq { get; private set; }

    public long? InboundSeq { get; private set; }

    public MessageAuthor Author { get; private set; }

    public Guid? AuthorRef { get; private set; }

    public MessageKind Kind { get; private set; }

    public ContentState ContentState { get; private set; }

    public string? Text { get; private set; }

    public string? ProviderEventId { get; private set; }

    public DateTimeOffset? ProviderTimestamp { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>Posição cronológica: o momento no provedor para o cliente, e o do servidor para a saída.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public bool LateArrival { get; private set; }

    public Guid? TurnId { get; private set; }

    public ReplyKind? ReplyKind { get; private set; }

    public long? CoveredThroughSeq { get; private set; }

    public DeliveryState? DeliveryState { get; private set; }

    public string? DeliveryFailureReason { get; private set; }

    public Guid? ResendOfMessageId { get; private set; }

    public Guid? ResendRequestId { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid[]? AiRequestIds { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// O idioma desta mensagem (BCP 47), ou <c>null</c> se não se sabe: nunca se presume o idioma do tenant nem o da
    /// conversa para uma mensagem do cliente. Não altera o <see cref="Text"/>, que é sempre o original.
    /// </summary>
    public Locale? Locale { get; private set; }

    /// <summary>De onde veio <see cref="Locale"/>; é nulo exatamente quando o idioma é desconhecido.</summary>
    public MessageLocaleOrigin? LocaleOrigin { get; private set; }

    public static void ValidateText(string? text, MessageKind kind, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(text))
        {
            throw new ConversationsValidationException("O texto da mensagem é obrigatório.");
        }

        if (text is { Length: > ConversationLimits.TextMaxLength })
        {
            throw new ConversationsValidationException($"O texto da mensagem deve ter até {ConversationLimits.TextMaxLength} caracteres.");
        }
    }

    /// <summary>
    /// Mensagem do cliente. Texto nasce pronto (<see cref="ContentState.Ready"/>); mídia nasce pendente e sem texto, e só vira
    /// pronta ou falha pelos comandos de conteúdo. Conteúdo que não está pronto nunca é entrada do modelo (I9). O texto é
    /// guardado EXATAMENTE como veio (sem cortar espaços, sem normalizar): é o original, e traduções ficam à parte.
    /// </summary>
    public static Message CreateInbound(
        TenantId tenantId,
        Guid sessionId,
        Guid conversationId,
        ChannelInstanceId channelInstanceId,
        InboundRegistration registration,
        string providerEventId,
        DateTimeOffset providerTimestamp,
        MessageKind kind,
        string? text,
        DateTimeOffset receivedAt,
        Locale? declaredLocale = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var evento = (providerEventId ?? string.Empty).Trim();
        if (evento.Length == 0 || evento.Length > ConversationLimits.ProviderEventIdMaxLength)
        {
            throw new ConversationsValidationException($"O id do evento do provedor deve ter de 1 a {ConversationLimits.ProviderEventIdMaxLength} caracteres.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ConversationsValidationException("O tipo da mensagem é inválido.");
        }

        if (providerTimestamp == default)
        {
            throw new ConversationsValidationException("O momento da mensagem no provedor é obrigatório.");
        }

        ValidateText(text, kind, required: kind == MessageKind.Text);

        var texto = kind == MessageKind.Text ? text : null;
        return new Message
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SessionId = sessionId,
            ConversationId = conversationId,
            ChannelInstanceId = channelInstanceId,
            Direction = MessageDirection.Inbound,
            MessageSeq = registration.MessageSeq,
            InboundSeq = registration.InboundSeq,
            Author = MessageAuthor.Customer,
            Kind = kind,
            ContentState = kind == MessageKind.Text ? ContentState.Ready : ContentState.Pending,
            Text = texto,
            ProviderEventId = evento,
            ProviderTimestamp = providerTimestamp,
            ReceivedAt = receivedAt,
            OccurredAt = providerTimestamp,
            LateArrival = registration.LateArrival,
            CreatedAt = receivedAt,
            Locale = declaredLocale,
            LocaleOrigin = declaredLocale is null ? null : MessageLocaleOrigin.ProviderDeclared,
        };
    }

    /// <summary>Resposta da IA aceita: já nasce liberada para envio (<see cref="DeliveryState.Requested"/>) e cobre logicamente até <paramref name="coveredThroughSeq"/>.</summary>
    public static Message CreateAiReply(
        TenantId tenantId,
        Guid sessionId,
        Guid conversationId,
        ChannelInstanceId channelInstanceId,
        long messageSeq,
        Guid turnId,
        ReplyKind replyKind,
        long coveredThroughSeq,
        string text,
        IReadOnlyList<Guid> aiRequestIds,
        DateTimeOffset now,
        Locale conversationLocale)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(conversationLocale);

        ValidateText(text, MessageKind.Text, required: true);

        return new Message
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SessionId = sessionId,
            ConversationId = conversationId,
            ChannelInstanceId = channelInstanceId,
            Direction = MessageDirection.Outbound,
            MessageSeq = messageSeq,
            Author = MessageAuthor.Ai,
            Kind = MessageKind.Text,
            ContentState = ContentState.Ready,
            Text = text,
            ReceivedAt = now,
            OccurredAt = now,
            TurnId = turnId,
            ReplyKind = replyKind,
            CoveredThroughSeq = coveredThroughSeq,
            DeliveryState = Contracts.DeliveryState.Requested,
            AiRequestIds = aiRequestIds.ToArray(),
            CreatedAt = now,
            Locale = conversationLocale,
            LocaleOrigin = MessageLocaleOrigin.ConversationLocale,
        };
    }

    /// <summary>
    /// Reenvio pedido por um atendente (COV-7): copia o conteúdo e o destino da mensagem que falhou, guarda o vínculo com ela
    /// e o autor, e nasce liberado para envio. Não cobre nada de novo (a cobertura já existia) e não vem de turno de IA.
    /// </summary>
    public static Message CreateResend(Message original, long messageSeq, Guid requestId, Guid requestedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(original);

        return new Message
        {
            Id = Guid.CreateVersion7(),
            TenantId = original.TenantId,
            SessionId = original.SessionId,
            ConversationId = original.ConversationId,
            ChannelInstanceId = original.ChannelInstanceId,
            Direction = MessageDirection.Outbound,
            MessageSeq = messageSeq,
            Author = MessageAuthor.Human,
            AuthorRef = requestedBy,
            Kind = original.Kind,
            ContentState = ContentState.Ready,
            Text = original.Text,
            ReceivedAt = now,
            OccurredAt = now,
            ReplyKind = original.ReplyKind,
            CoveredThroughSeq = original.CoveredThroughSeq,
            DeliveryState = Contracts.DeliveryState.Requested,
            ResendOfMessageId = original.Id,
            ResendRequestId = requestId,
            RequestedBy = requestedBy,
            CreatedAt = now,
            Locale = original.Locale,
            LocaleOrigin = original.LocaleOrigin,
        };
    }

    /// <summary>O conteúdo da mídia ficou pronto. Só vale para mensagem do cliente ainda pendente.</summary>
    public ContentChangeOutcome SetContent(string text)
    {
        if (Direction != MessageDirection.Inbound)
        {
            return ContentChangeOutcome.NotAllowed;
        }

        if (ContentState == ContentState.Ready)
        {
            return ContentChangeOutcome.Unchanged;
        }

        if (ContentState == ContentState.Failed)
        {
            // MED-5: conteúdo pronto depois de a política ter sido aplicada não reabre o turno nem muda o estado final.
            return ContentChangeOutcome.NotAllowed;
        }

        ValidateText(text, Kind, required: true);
        Text = text.Trim();
        ContentState = ContentState.Ready;
        return ContentChangeOutcome.Changed;
    }

    /// <summary>O conteúdo falhou ou o prazo da política se esgotou. O estado é final e a mensagem segue visível ao atendente.</summary>
    public ContentChangeOutcome FailContent()
    {
        if (Direction != MessageDirection.Inbound)
        {
            return ContentChangeOutcome.NotAllowed;
        }

        if (ContentState == ContentState.Failed)
        {
            return ContentChangeOutcome.Unchanged;
        }

        if (ContentState == ContentState.Ready)
        {
            return ContentChangeOutcome.NotAllowed;
        }

        ContentState = ContentState.Failed;
        return ContentChangeOutcome.Changed;
    }

    /// <summary>Aplica um status de envio, se a máquina monotônica o permite. Devolve se aplicou.</summary>
    public bool TryApplyDelivery(DeliveryState state, string? failureReason)
    {
        if (Direction != MessageDirection.Outbound || DeliveryState is not { } atual || !DeliveryStateRules.CanAdvance(atual, state))
        {
            return false;
        }

        DeliveryState = state;
        if (DeliveryStateRules.IsDefinitiveFailure(state))
        {
            DeliveryFailureReason = failureReason;
        }

        return true;
    }
}
