using IaBimeta.Channels.Contracts.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Adapters.WhatsApp;

public static class WhatsAppServiceCollectionExtensions
{
    /// <summary>
    /// Registra o adaptador do WhatsApp Cloud API como um <see cref="IChannelProviderAdapter"/> (o Channels o escolhe pelo <see cref="IChannelProviderAdapter.Provider"/>). Só os hosts chamam (DEP-3). A configuração
    /// não tem segredo: o token de acesso e o App Secret da conta chegam a cada chamada. Um adaptador registrado não recebe tráfego até uma conta de canal do tipo existir e estar ativa.
    /// </summary>
    /// <param name="configure">Ajusta as opções (endereço, versão da Graph API, tempo limite); o host liga a seção <c>Channels:Providers:WhatsApp</c>.</param>
    public static IServiceCollection AddWhatsAppAdapter(this IServiceCollection services, Action<WhatsAppOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opcoes = new WhatsAppOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);

        // Um só cliente HTTP, com a vida das conexões limitada, e descartado pelo contêiner junto com o adaptador (que o possui).
        services.AddSingleton<IChannelProviderAdapter>(provider => WhatsAppCloudAdapter.Create(provider.GetRequiredService<WhatsAppOptions>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
