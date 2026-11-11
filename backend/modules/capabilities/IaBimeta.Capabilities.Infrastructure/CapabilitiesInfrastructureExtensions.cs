using IaBimeta.Capabilities.Application;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure.Outbound;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Capabilities.Infrastructure;

public static class CapabilitiesInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Capabilities (ADR 0035): o motor de execução, a administração de conexões e operações, o histórico, a persistência, a cifra dos segredos e o conector REST/JSON. O host registra antes o
    /// <c>IRequestContextAccessor</c> e o Audit (<c>IAuditWriter</c>). A string de conexão é lida só quando o DbContext é usado. Sem <c>Capabilities:Secrets</c> a administração funciona, mas gravar um segredo
    /// responde 503 e nenhuma conexão com autenticação executa (falha fechada). Os testes substituem a rede registrando um <see cref="IOutboundHttp"/> ou um <see cref="IHostResolver"/> antes desta chamada.
    /// </summary>
    public static IServiceCollection AddCapabilities(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<CapabilitiesOptions>? configure = null,
        Action<CapabilitiesSecretOptions>? configureSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var opcoes = new CapabilitiesOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();
        var segredos = new CapabilitiesSecretOptions();
        configureSecrets?.Invoke(segredos);

        services.AddSingleton(opcoes);
        services.AddSingleton(segredos);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new OutboundDestinationPolicy(opcoes.Outbound));
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(segredos));

        services.AddDbContext<CapabilitiesDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CapabilitiesDbContext.Schema)));

        services.AddScoped<IConnectionStore, ConnectionStore>();
        services.AddScoped<IDefinitionStore, DefinitionStore>();
        services.AddScoped<IExecutionStore, ExecutionStore>();

        // O transporte de saída e o conector. Quem já registrou outro IOutboundHttp (os testes) o mantém.
        services.TryAddSingleton<IHostResolver, SystemHostResolver>();
        services.TryAddSingleton<IOutboundHttp, GuardedHttpTransport>();
        services.AddSingleton<OAuthTokenCache>();
        services.AddSingleton<ICapabilityConnector, RestJsonConnector>();
        services.AddSingleton<IConnectorRegistry>(provider => new ConnectorRegistry(provider.GetServices<ICapabilityConnector>()));

        services.AddScoped<CapabilitiesAuthorizer>();
        services.AddScoped(p => new ConnectionService(
            p.GetRequiredService<CapabilitiesAuthorizer>(), p.GetRequiredService<IConnectionStore>(), p.GetRequiredService<ISecretProtector>(), p.GetRequiredService<IConnectorRegistry>(),
            p.GetRequiredService<OutboundDestinationPolicy>(), p.GetRequiredService<CapabilitiesOptions>(), p.GetRequiredService<TimeProvider>()));
        services.AddScoped(p => new DefinitionService(
            p.GetRequiredService<CapabilitiesAuthorizer>(), p.GetRequiredService<IDefinitionStore>(), p.GetRequiredService<IConnectionStore>(), p.GetRequiredService<CapabilitiesOptions>(), p.GetRequiredService<TimeProvider>()));
        services.AddScoped(p => new CapabilityExecutionService(
            p.GetRequiredService<CapabilitiesAuthorizer>(), p.GetRequiredService<IDefinitionStore>(), p.GetRequiredService<IConnectionStore>(), p.GetRequiredService<IExecutionStore>(),
            p.GetRequiredService<ISecretProtector>(), p.GetRequiredService<IConnectorRegistry>(), p.GetRequiredService<CapabilitiesOptions>(), p.GetRequiredService<TimeProvider>()));
        services.AddScoped(p => new ExecutionHistoryService(p.GetRequiredService<CapabilitiesAuthorizer>(), p.GetRequiredService<IExecutionStore>(), p.GetRequiredService<TimeProvider>()));

        // O ponto de entrada para quem usa o motor (Agents, chatbot, workflows, assistente interno): só os Contracts.
        services.AddScoped<ICapabilityExecutor>(p => p.GetRequiredService<CapabilityExecutionService>());
        services.AddScoped<ICapabilityCatalog>(p => p.GetRequiredService<CapabilityExecutionService>());
        return services;
    }
}
