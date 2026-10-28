using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Como o idioma efetivo de uma conversa NOVA é escolhido. A ordem vai do que se sabe do cliente para a suposição: a
/// preferência guardada do contato, depois o idioma que o canal ou o provedor declarou para o contato, depois o idioma
/// padrão configurado para o número (<see cref="LocaleSource.ChannelDefault"/>) e só no fim o idioma padrão do tenant
/// (<see cref="LocaleSource.TenantDefault"/>). As duas últimas são suposições, porque nem o idioma do número nem o do tenant
/// são, por definição, o do cliente. Nenhum passo detecta idioma pelo conteúdo: detecção é etapa futura.
/// </summary>
public static class LocaleResolution
{
    public static (Locale Locale, LocaleSource Source) ForNewConversation(Locale? contactPreferred, Locale? channelHint, Locale? channelDefault, Locale tenantDefault)
    {
        ArgumentNullException.ThrowIfNull(tenantDefault);

        if (contactPreferred is not null)
        {
            return (contactPreferred, LocaleSource.ContactPreference);
        }

        if (channelHint is not null)
        {
            return (channelHint, LocaleSource.ChannelHint);
        }

        if (channelDefault is not null)
        {
            return (channelDefault, LocaleSource.ChannelDefault);
        }

        return (tenantDefault, LocaleSource.TenantDefault);
    }

    /// <summary>É uma suposição (nada se sabia do cliente) e pode ser substituída por informação melhor?</summary>
    public static bool IsAssumed(LocaleSource source) => source is LocaleSource.TenantDefault or LocaleSource.ChannelDefault;
}
