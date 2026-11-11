using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.Knowledge.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Knowledge.Infrastructure;

public static class KnowledgeInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Knowledge (ADR 0034): o índice semântico com pgvector, a busca e o indexador. O host registra antes o <c>IRequestContextAccessor</c> e, se quiser vetores de verdade, um
    /// <see cref="IEmbeddingProvider"/> (o adaptador do Ollama); sem ele, a vetorização e a busca respondem "não configurado" e o atendimento segue pelos fluxos existentes. Com
    /// <c>Knowledge:Semantic:Enabled = false</c> (padrão) nada toca o banco do módulo.
    /// </summary>
    public static IServiceCollection AddKnowledge(this IServiceCollection services, Func<IServiceProvider, string> connectionString, Action<KnowledgeSemanticOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var opcoes = new KnowledgeSemanticOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();
        if (opcoes.Dimension != 1024)
        {
            // A coluna é vector(1024): outra dimensão exige uma migration nova (e um ADR), não só configuração.
            throw new InvalidOperationException("Knowledge:Semantic:Dimension precisa ser 1024 (a coluna do índice é vector(1024)).");
        }

        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEmbeddingProvider, UnconfiguredEmbeddingProvider>();

        services.AddDbContext<KnowledgeDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", KnowledgeDbContext.Schema)));

        services.AddSingleton<ISemanticStore>(provider => new PostgresSemanticStore(() => connectionString(provider)));
        services.AddScoped<SemanticIndexService>();
        services.AddScoped<IKnowledgeIndexing>(provider => provider.GetRequiredService<SemanticIndexService>());
        services.AddScoped<IKnowledgeSearch>(provider => provider.GetRequiredService<SemanticIndexService>());
        services.AddSingleton<SemanticIndexer>();
        return services;
    }

    /// <summary>O provedor "ninguém configurado": toda chamada vira indisponibilidade segura.</summary>
    private sealed class UnconfiguredEmbeddingProvider : IEmbeddingProvider
    {
        public EmbeddingModelInfo Model => new(KnowledgeSemanticOptions.DefaultModelId, 1024);

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default) =>
            throw new EmbeddingUnavailableException("not-configured", "Nenhum provedor de embeddings foi configurado.");
    }
}
