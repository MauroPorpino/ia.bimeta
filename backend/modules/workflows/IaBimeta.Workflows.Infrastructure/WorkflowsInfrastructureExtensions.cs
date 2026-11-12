using IaBimeta.Capabilities.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Workflows.Infrastructure;

public static class WorkflowsInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Workflows (ADR 0037 e 0038). A ADMINISTRAÇÃO (rascunhos, validação, simulação, publicação) está sempre disponível. O RUNTIME (roteador, executor, consumidores de evento) só é registrado com
    /// <c>Workflows:Options:Enabled = true</c> (padrão: desligado): desligado, o roteador é um dublê que responde "agente" sem tocar no banco e NENHUM consumidor de evento é registrado, então o comportamento
    /// anterior ao chatbot visual é preservado integralmente. O host registra antes o <c>IRequestContextAccessor</c>, o Audit e, para o runtime, o Conversations (turnos, mensagens de fluxo e consultas) e, opcionalmente,
    /// o Capabilities, o conhecimento (<see cref="IFlowKnowledgeLookup"/>) e a transferência (<see cref="IFlowHandoffPort"/>). A string de conexão é lida só quando o DbContext é usado.
    /// Sem <c>Workflows:Secrets</c> a administração funciona, mas nenhuma instância é criada (falha fechada: nunca se grava variável em claro).
    /// </summary>
    public static IServiceCollection AddWorkflows(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<WorkflowsOptions>? configure = null,
        Action<WorkflowsSecretOptions>? configureSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var opcoes = new WorkflowsOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();
        var segredos = new WorkflowsSecretOptions();
        configureSecrets?.Invoke(segredos);

        services.AddSingleton(opcoes);
        services.AddSingleton(segredos);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SimulationThrottle>();
        services.AddSingleton<IFlowCipher>(new AesGcmFlowCipher(segredos));

        services.AddDbContext<WorkflowsDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WorkflowsDbContext.Schema)));

        services.AddScoped<IWorkflowStore, WorkflowStore>();
        services.AddScoped<IFlowRuntimeStore, FlowRuntimeStore>();
        services.AddScoped<IFlowOperationsStore, FlowOperationsStore>();
        services.AddScoped<WorkflowsAuthorizer>();
        services.AddScoped(p => new WorkflowDefinitionService(
            p.GetRequiredService<WorkflowsAuthorizer>(), p.GetRequiredService<IWorkflowStore>(), p.GetService<ICapabilityCatalog>(), p.GetRequiredService<WorkflowsOptions>(),
            p.GetRequiredService<SimulationThrottle>(), p.GetRequiredService<TimeProvider>()));
        services.AddScoped(p => new FlowOperationsService(
            p.GetRequiredService<WorkflowsAuthorizer>(), p.GetRequiredService<IFlowOperationsStore>(), p.GetRequiredService<IWorkflowStore>(), p.GetRequiredService<WorkflowsOptions>(), p.GetRequiredService<TimeProvider>()));

        if (!opcoes.Enabled)
        {
            services.TryAddSingleton<IAutomationRouter, DisabledAutomationRouter>();
            return services;
        }

        services.AddScoped<IAutomationRouter>(p => new FlowRouter(
            p.GetRequiredService<IFlowRuntimeStore>(), p.GetRequiredService<IWorkflowStore>(), p.GetRequiredService<IConversationQueries>(), p.GetRequiredService<IFlowCipher>(),
            p.GetRequiredService<IRequestContextAccessor>(), p.GetRequiredService<WorkflowsOptions>(), p.GetRequiredService<TimeProvider>()));
        services.AddScoped(p => new FlowRuntime(
            p.GetRequiredService<IFlowRuntimeStore>(), p.GetRequiredService<IWorkflowStore>(), p.GetRequiredService<IFlowCipher>(), p.GetRequiredService<ITurnCommands>(), p.GetRequiredService<IFlowCommands>(),
            p.GetRequiredService<IConversationQueries>(), p.GetService<ICapabilityExecutor>(), p.GetService<ICapabilityCatalog>(), p.GetService<IFlowKnowledgeLookup>(), p.GetService<IFlowHandoffPort>(),
            p.GetRequiredService<IRequestContextAccessor>(), p.GetRequiredService<WorkflowsOptions>(), p.GetRequiredService<TimeProvider>()));

        // Os consumidores dos eventos do Conversations (o relay os entrega pelo Inbox do módulo).
        services.AddInboxGate<WorkflowsDbContext>();
        services.AddEventHandler<InboundForFlowHandler>();
        services.AddEventHandler<ContentReadyForFlowHandler>();
        services.AddEventHandler<ModeChangedForFlowHandler>();
        services.AddEventHandler<SessionClosedForFlowHandler>();
        return services;
    }
}

/// <summary>O roteador com o chatbot visual DESLIGADO: toda sessão é do agente, sem consultar nem gravar nada.</summary>
internal sealed class DisabledAutomationRouter : IAutomationRouter
{
    public Task<AutomationOwner> GetOwnerAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(AutomationOwner.Agent);

    public Task<AutomationOwner> ResolveOnInboundAsync(Guid sessionId, long inboundSeq, CancellationToken cancellationToken = default) => Task.FromResult(AutomationOwner.Agent);
}
