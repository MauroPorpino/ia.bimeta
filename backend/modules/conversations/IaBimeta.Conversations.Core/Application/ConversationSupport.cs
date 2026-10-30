using System.Buffers.Text;
using System.Globalization;
using System.Text;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>Montagem do envelope dos eventos do módulo. A ordem de entrega é por sessão (<c>OrderingKey</c>).</summary>
internal static class ConversationEventFactory
{
    public static EventEnvelope Create(TenantId tenantId, Guid sessionId, string type, object payload, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), type, 1, tenantId, Guid.CreateVersion7(), null, $"session:{sessionId}", now, payload);
}

/// <summary>Cursores opacos das páginas. Um cursor adulterado ou de outro formato é recusado com erro de validação, nunca ignorado.</summary>
internal static class CursorCodec
{
    public static string Encode(SessionCursor cursor) =>
        Pack("s1", cursor.LastMessageAt.UtcTicks.ToString(CultureInfo.InvariantCulture), cursor.Id.ToString("N"));

    public static string Encode(MessageCursor cursor) =>
        Pack("m1", cursor.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture), cursor.MessageSeq.ToString(CultureInfo.InvariantCulture));

    public static SessionCursor? DecodeSession(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var partes = Unpack(cursor, "s1");
        if (long.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && Guid.TryParseExact(partes[1], "N", out var id)
            && IsValidTicks(ticks))
        {
            return new SessionCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        throw Invalido();
    }

    public static MessageCursor? DecodeMessage(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var partes = Unpack(cursor, "m1");
        if (long.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && long.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            && IsValidTicks(ticks))
        {
            return new MessageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), seq);
        }

        throw Invalido();
    }

    private static bool IsValidTicks(long ticks) => ticks >= 0 && ticks <= DateTime.MaxValue.Ticks;

    private static string Pack(string tipo, string a, string b) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{tipo}.{a}.{b}"));

    private static string[] Unpack(string cursor, string tipoEsperado)
    {
        try
        {
            var texto = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var partes = texto.Split('.');
            if (partes.Length == 3 && partes[0] == tipoEsperado)
            {
                return new[] { partes[1], partes[2] };
            }
        }
        catch (FormatException)
        {
        }
        catch (ArgumentException)
        {
        }

        throw Invalido();
    }

    private static ConversationsValidationException Invalido() => new("O cursor de paginação é inválido.");
}

internal static class ConversationMapping
{
    public static SessionView ToView(this SessionRow linha)
    {
        var s = linha.Session;
        return new SessionView(
            s.Id,
            s.OrganizationId,
            s.BusinessUnitId,
            s.ContactId,
            linha.ContactDisplayName,
            s.ChannelInstanceId,
            s.Status,
            s.HandlingMode,
            s.ResponderRef,
            s.StateVersion,
            s.LastInboundSeq,
            s.AnsweredThroughSeq,
            s.ActiveTurnId is not null,
            s.ReplyDeliveryFailed,
            s.CreatedAt,
            s.LastMessageAt,
            linha.EffectiveLocale,
            linha.LocaleSource,
            linha.ContactPreferredLocale,
            s.DepartmentId,
            s.LastMessageSeq,
            s.HandlingPeriod,
            s.PlacementEpoch);
    }

    public static SessionTransferView ToView(this SessionTransfer t) =>
        new(t.TransferId, t.SessionId, t.FromBusinessUnitId, t.FromDepartmentId, t.ToBusinessUnitId, t.ToDepartmentId, t.RequestedBy, t.Reason, t.StateVersionAfter, t.TransferredAt);

    public static MessageTranslationView ToView(this MessageTranslation t) =>
        new(t.Id, t.MessageId, t.TargetLocale, t.SourceLocale, t.Text, t.Provenance, t.CreatedAt);

    /// <summary>Visão para leitura humana: o texto de conteúdo que não está pronto fica nulo.</summary>
    public static MessageView ToView(this Message m) => new(
        m.Id,
        m.SessionId,
        m.ConversationId,
        m.Direction,
        m.Author,
        m.AuthorRef,
        m.MessageSeq,
        m.InboundSeq,
        m.Kind,
        m.ContentState,
        m.ContentState == ContentState.Ready ? m.Text : null,
        m.LateArrival,
        m.OccurredAt,
        m.ProviderTimestamp,
        m.ReceivedAt,
        m.DeliveryState,
        m.ResendOfMessageId,
        m.ReplyKind,
        m.CoveredThroughSeq,
        m.ChannelInstanceId,
        m.Locale,
        m.LocaleOrigin);

    /// <summary>
    /// Visão para o modelo de IA: conteúdo indisponível nunca entra (I9). Sem texto, sem marcador que o modelo possa tratar
    /// como conteúdo: só o estado.
    /// </summary>
    public static SnapshotMessage ToSnapshot(this Message m, long answeredThroughSeq) => new(
        m.Id,
        m.Direction,
        m.Author,
        m.MessageSeq,
        m.InboundSeq,
        m.Kind,
        m.ContentState,
        m.ContentState == ContentState.Ready ? m.Text : null,
        m.LateArrival,
        m.Direction == MessageDirection.Inbound && m.InboundSeq > answeredThroughSeq,
        m.OccurredAt,
        m.Locale);
}
