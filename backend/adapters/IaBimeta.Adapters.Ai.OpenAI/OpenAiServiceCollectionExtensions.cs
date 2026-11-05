using IaBimeta.AiGateway.Contracts.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Adapters.Ai.OpenAI;

public static class OpenAiServiceCollectionExtensions
{
    /// <summary>
    /// Registra o adaptador da OpenAI como um <see cref="IAiProvider"/> (o AiGateway o escolhe pelo <c>ProviderId</c> <c>openai</c> do catálogo do tenant). Só os hosts chamam (DEP-3). A configuração não tem
    /// segredo: as credenciais são BYOK, guardadas cifradas pelo AiGateway e entregues a cada chamada. Um provedor registrado não é usado por tenant nenhum até o tenant escolhê-lo no catálogo e ter credencial.
    /// </summary>
    /// <param name="configure">Ajusta as opções (endereço, tamanho máximo da resposta, perfis de modelo); o host liga a seção <c>AiGateway:Providers:OpenAi</c>.</param>
    public static IServiceCollection AddOpenAiProvider(this IServiceCollection services, Action<OpenAiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opcoes = new OpenAiOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);

        // Um só cliente HTTP, com a vida das conexões limitada, e descartado pelo contêiner junto com o provedor (que o possui).
        services.AddSingleton<IAiProvider>(provider => OpenAiProvider.Create(provider.GetRequiredService<OpenAiOptions>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
