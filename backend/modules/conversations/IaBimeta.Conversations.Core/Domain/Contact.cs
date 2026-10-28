using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Contato de um canal (por exemplo, o número de um cliente no WhatsApp). A chave é <c>(tenant_id, id)</c>, e o contato é
/// único por <c>(tenant, canal, referência no provedor)</c>. O canal é um identificador opaco do módulo Channels: não há
/// chave estrangeira entre esquemas (DAD-2).
/// </summary>
public sealed class Contact : ITenantOwned
{
    private Contact()
    {
        ExternalRef = string.Empty;
        TenantId = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public ChannelInstanceId ChannelInstanceId { get; private set; }

    public string ExternalRef { get; private set; }

    public string? DisplayName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// O idioma que o contato prefere (BCP 47), se alguém o definiu. É um dado do contato, no tenant dele: não vem do
    /// idioma do tenant e só vale para escolher o idioma das conversas novas.
    /// </summary>
    public Locale? PreferredLocale { get; private set; }

    /// <summary>Guarda (ou limpa) a preferência. Devolve <c>false</c> se nada mudou.</summary>
    public bool SetPreferredLocale(Locale? locale)
    {
        if (PreferredLocale == locale)
        {
            return false;
        }

        PreferredLocale = locale;
        return true;
    }

    public static Contact Create(TenantId tenantId, ChannelInstanceId channelInstanceId, string externalRef, string? displayName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var referencia = (externalRef ?? string.Empty).Trim();
        if (channelInstanceId.IsEmpty || referencia.Length == 0 || referencia.Length > ConversationLimits.ContactRefMaxLength)
        {
            throw new ConversationsValidationException($"O contato precisa de um canal e de uma referência de 1 a {ConversationLimits.ContactRefMaxLength} caracteres.");
        }

        var nome = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (nome is { Length: > ConversationLimits.DisplayNameMaxLength })
        {
            throw new ConversationsValidationException($"O nome do contato deve ter até {ConversationLimits.DisplayNameMaxLength} caracteres.");
        }

        return new Contact { Id = Guid.CreateVersion7(), TenantId = tenantId, ChannelInstanceId = channelInstanceId, ExternalRef = referencia, DisplayName = nome, CreatedAt = now };
    }
}
