using System.Net.Http.Headers;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.Knowledge.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Adapters.Ai.Ollama;

public static class OllamaExtensions
{
    /// <summary>
    /// Registra o adaptador do Ollama como provedor de embeddings e de geração local (ADR 0034). Registre ANTES do <c>AddKnowledge</c>. Sem token no repositório: <c>BearerToken</c> vem do ambiente.
    /// </summary>
    public static IServiceCollection AddOllamaProvider(this IServiceCollection services, Action<OllamaOptions>? configure = null)
    {
        var opcoes = new OllamaOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.AddSingleton(opcoes);
        services.AddHttpClient<OllamaProvider>(http =>
        {
            http.BaseAddress = new Uri(opcoes.BaseUrl.EndsWith('/') ? opcoes.BaseUrl : opcoes.BaseUrl + "/");
            http.Timeout = Timeout.InfiniteTimeSpan; // os tempos são por chamada (linked token), não do cliente
            if (!string.IsNullOrWhiteSpace(opcoes.BearerToken))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opcoes.BearerToken);
            }
        });
        services.AddSingleton<IEmbeddingProvider>(provider => provider.GetRequiredService<OllamaProvider>());
        services.AddSingleton<ILocalGenerationProvider>(provider => provider.GetRequiredService<OllamaProvider>());
        return services;
    }

    /// <summary>
    /// Registra o adaptador CONVERSACIONAL do Ollama (Etapa 04) como um <see cref="IAiProvider"/> (o AiGateway o escolhe pelo <c>ProviderId</c> <c>ollama</c> do catálogo do tenant) e a sondagem somente leitura do
    /// servidor. Independe do <see cref="AddOllamaProvider"/> (embeddings e geração local do Knowledge): outra seção de configuração (<c>AiGateway:Providers:Ollama</c>) e nenhum estado compartilhado. Só os hosts
    /// chamam (DEP-3). Um provedor registrado não serve a tenant nenhum até o catálogo ter um modelo ATIVO dele e o tenant escolhê-lo.
    /// </summary>
    public static IServiceCollection AddOllamaChatProvider(this IServiceCollection services, Action<OllamaChatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opcoes = new OllamaChatOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAiProvider>(provider => OllamaChatProvider.Create(provider.GetRequiredService<OllamaChatOptions>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => OllamaServerProbe.Create(provider.GetRequiredService<OllamaChatOptions>()));
        return services;
    }
}
