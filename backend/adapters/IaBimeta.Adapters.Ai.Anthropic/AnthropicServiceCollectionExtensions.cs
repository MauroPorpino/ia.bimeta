using IaBimeta.AiGateway.Contracts.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Adapters.Ai.Anthropic;

public static class AnthropicServiceCollectionExtensions
{
    /// <summary>
    /// Registra o adaptador da Anthropic como um <see cref="IAiProvider"/> (o AiGateway o escolhe pelo <c>ProviderId</c> <c>anthropic</c> do catálogo do tenant). Só os hosts chamam (DEP-3). A configuração
    /// não tem segredo: as credenciais são BYOK, guardadas cifradas pelo AiGateway e entregues a cada chamada. Um provedor registrado não é usado por tenant nenhum até o tenant escolhê-lo e ter credencial.
    /// </summary>
    /// <param name="configure">Ajusta as opções (endereço, versão da API, perfis de modelo); o host liga a seção <c>AiGateway:Providers:Anthropic</c>.</param>
    public static IServiceCollection AddAnthropicProvider(this IServiceCollection services, Action<AnthropicOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opcoes = new AnthropicOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAiProvider>(provider => AnthropicProvider.Create(provider.GetRequiredService<AnthropicOptions>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
