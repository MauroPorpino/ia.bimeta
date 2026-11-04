using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.AiGateway.Infrastructure;

/// <summary>Uma entrada do catálogo de modelos, na forma que a configuração (<c>AiGateway:Catalog:Models</c>) preenche. Preços por MILHÃO de tokens, na moeda indicada.</summary>
public sealed class AiCatalogModelSettings
{
    public string ProviderId { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string Profile { get; set; } = string.Empty;

    public int ContextWindowTokens { get; set; }

    public int MaxOutputTokens { get; set; }

    public string Currency { get; set; } = "USD";

    public decimal InputPerMillion { get; set; }

    public decimal CachedInputPerMillion { get; set; }

    public decimal CacheWritePerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    /// <summary>Texto <c>Active</c> ou <c>Disabled</c>.</summary>
    public string Status { get; set; } = nameof(AiModelStatus.Active);

    internal AiModelDescriptor ToDescriptor()
    {
        if (!Enum.TryParse<AiModelStatus>(Status, ignoreCase: true, out var situacao) || !Enum.IsDefined(situacao))
        {
            throw new AiGatewayConfigurationException($"A situação '{Status}' do modelo '{ProviderId}/{ModelId}' do catálogo é desconhecida.");
        }

        return new AiModelDescriptor(
            ProviderId,
            ModelId,
            Profile,
            ContextWindowTokens,
            MaxOutputTokens,
            new AiModelPrices(Currency, InputPerMillion, CachedInputPerMillion, CacheWritePerMillion, OutputPerMillion),
            situacao);
    }
}

/// <summary>O catálogo de modelos suportados (seção <c>AiGateway:Catalog</c>): CONFIGURAÇÃO, não tabela. Uma entrada inválida falha na partida do módulo.</summary>
public sealed class AiCatalogSettings
{
    public List<AiCatalogModelSettings> Models { get; set; } = new();
}

public static class AiGatewayInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo AiGateway (ADR 0017): o catálogo (da configuração, validado AGORA: uma entrada inválida falha na partida), a seleção de provedor e modelo, a persistência
    /// (<c>ai_gateway</c>), a configuração de IA do tenant, a cifra BYOK, o ledger de uso e o <see cref="IAiGateway"/>. O host precisa registrar antes o <c>IRequestContextAccessor</c> e o Audit
    /// (<c>IAuditWriter</c>), e registra os ADAPTADORES de provedor (<see cref="IAiProvider"/>) por conta própria: sem nenhum, o módulo sobe e a seleção responde <c>provider-not-registered</c>.
    /// Sem chave mestra configurada, o módulo sobe e a configuração funciona, mas nenhuma credencial é gravada nem lida (falha fechada). A string de conexão é lida só quando o DbContext é usado.
    /// </summary>
    public static IServiceCollection AddAiGateway(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<AiSecretOptions>? configureSecrets = null,
        Action<AiCatalogSettings>? configureCatalog = null,
        Action<AiDefaultLimits>? configureDefaultLimits = null,
        AiGatewayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var segredos = new AiSecretOptions();
        configureSecrets?.Invoke(segredos);
        services.AddSingleton(segredos);
        services.AddSingleton<IAiCredentialProtector, AesGcmCredentialProtector>();

        var configuracaoDoCatalogo = new AiCatalogSettings();
        configureCatalog?.Invoke(configuracaoDoCatalogo);
        services.AddSingleton(AiModelCatalog.Create(configuracaoDoCatalogo.Models.Select(m => m.ToDescriptor())));

        var limitesPadrao = new AiDefaultLimits();
        configureDefaultLimits?.Invoke(limitesPadrao);
        if (!limitesPadrao.Merge(AiTenantLimitOverrides.None).IsValid && HasAnyDefault(limitesPadrao))
        {
            // Padrão PARCIAL é erro de implantação (um limite sem valor deixaria todo tenant sem sobrescrita sem limites utilizáveis, em silêncio). Nenhum padrão também é válido: então
            // cada tenant precisa de todas as sobrescritas.
            throw new AiGatewayConfigurationException("Os limites padrão de IA estão incompletos: defina todos os obrigatórios (chamadas simultâneas, chamadas, tokens, período, mensagens e saída) ou nenhum.");
        }

        services.AddSingleton(limitesPadrao);

        var opcoes = options ?? new AiGatewayOptions();
        opcoes.EnsureValid();
        services.AddSingleton(opcoes);

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<AiGatewayDbContext>((provider, dbOptions) =>
            dbOptions.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiGatewayDbContext.Schema)));

        services.AddSingleton(provider => new AiProviderRegistry(provider.GetServices<IAiProvider>()));
        services.AddSingleton<AiModelSelector>();

        services.AddScoped<IAiTenantConfigurationStore, AiTenantConfigurationStore>();
        services.AddScoped<ITenantAiConfigurationReader, TenantAiConfigurationReader>();
        services.AddScoped<IAiCredentialResolver, AiCredentialResolver>();
        services.AddScoped<IAiUsageLedger, PostgresAiUsageLedger>();
        services.AddScoped<AiTenantConfigurationService>();
        services.AddScoped<AiGatewayService>();
        services.AddScoped<IAiGateway>(provider => provider.GetRequiredService<AiGatewayService>());

        return services;
    }

    private static bool HasAnyDefault(AiDefaultLimits limites) =>
        limites.MaxConcurrentCalls is not null || limites.MaxCallsPerPeriod is not null || limites.MaxTokensPerPeriod is not null || limites.PeriodSeconds is not null
        || limites.MaxInputMessages is not null || limites.MaxOutputTokens is not null || limites.MaxTokensPerCall is not null;
}
