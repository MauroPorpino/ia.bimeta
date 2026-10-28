using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Conversa de um canal dentro de uma sessão de atendimento (<see cref="ServiceSession"/>). Hoje há uma conversa por sessão e
/// canal; o destino da resposta fica restrito a ela (várias conversas na mesma sessão é ponto em aberto,
/// CONVERSATION-LIFECYCLE.md §17). Cada conversa tem o seu idioma efetivo, independente do idioma do tenant e do de
/// qualquer outra conversa.
/// </summary>
public sealed class Conversation : ITenantOwned
{
    private Conversation()
    {
        TenantId = null!;
        EffectiveLocale = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid ContactId { get; private set; }

    public ChannelInstanceId ChannelInstanceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>O idioma em que esta conversa acontece (BCP 47). Muda por <see cref="ChangeLocale"/>, nunca por inferência do tenant.</summary>
    public Locale EffectiveLocale { get; private set; }

    /// <summary>De onde veio a escolha de <see cref="EffectiveLocale"/>; <see cref="Contracts.LocaleSource.TenantDefault"/> é uma suposição.</summary>
    public LocaleSource LocaleSource { get; private set; }

    public static Conversation Create(TenantId tenantId, Guid sessionId, Guid contactId, ChannelInstanceId channelInstanceId, DateTimeOffset now, Locale effectiveLocale, LocaleSource localeSource)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(effectiveLocale);
        ValidateSource(localeSource);

        return new Conversation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SessionId = sessionId,
            ContactId = contactId,
            ChannelInstanceId = channelInstanceId,
            CreatedAt = now,
            EffectiveLocale = effectiveLocale,
            LocaleSource = localeSource,
        };
    }

    /// <summary>Troca o idioma efetivo e a origem da escolha. Devolve <c>false</c> se as duas coisas já eram essas.</summary>
    public bool ChangeLocale(Locale locale, LocaleSource source)
    {
        ArgumentNullException.ThrowIfNull(locale);
        ValidateSource(source);

        if (EffectiveLocale == locale && LocaleSource == source)
        {
            return false;
        }

        EffectiveLocale = locale;
        LocaleSource = source;
        return true;
    }

    private static void ValidateSource(LocaleSource source)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ConversationsValidationException("A origem da escolha do idioma é inválida.");
        }
    }
}
