using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Workflows.Infrastructure;

public static class WorkflowsInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Workflows (ADR 0037): administração de fluxos versionados, validação e simulação. O host registra antes o <c>IRequestContextAccessor</c>, o Audit (<c>IAuditWriter</c>) e, se houver nós de integração,
    /// o Capabilities (<c>ICapabilityCatalog</c>). A string de conexão é lida só quando o DbContext é usado. A função de runtime (<c>Workflows:Options:Enabled</c>) nasce DESLIGADA.
    /// </summary>
    public static IServiceCollection AddWorkflows(this IServiceCollection services, Func<IServiceProvider, string> connectionString, Action<WorkflowsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var opcoes = new WorkflowsOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SimulationThrottle>();

        services.AddDbContext<WorkflowsDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WorkflowsDbContext.Schema)));

        services.AddScoped<IWorkflowStore, WorkflowStore>();
        services.AddScoped<WorkflowsAuthorizer>();
        services.AddScoped(p => new WorkflowDefinitionService(
            p.GetRequiredService<WorkflowsAuthorizer>(), p.GetRequiredService<IWorkflowStore>(), p.GetService<ICapabilityCatalog>(), p.GetRequiredService<WorkflowsOptions>(),
            p.GetRequiredService<SimulationThrottle>(), p.GetRequiredService<TimeProvider>()));
        return services;
    }
}
