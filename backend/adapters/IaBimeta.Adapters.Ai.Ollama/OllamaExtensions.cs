using System.Net.Http.Headers;
using IaBimeta.Knowledge.Contracts;
using Microsoft.Extensions.DependencyInjection;

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
}
