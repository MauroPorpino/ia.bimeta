using IaBimeta.Agents.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Infrastructure;

namespace IaBimeta.Api.Operations;

/// <summary>A composição da supervisão: os limites (seção <c>Health</c>) e as sondas de saúde de cada módulo, que só o comando <c>status</c> consulta.</summary>
internal static class OperationsComposition
{
    public static IServiceCollection AddOperationsHealth(this IServiceCollection services, IConfiguration configuration)
    {
        var limites = new HealthThresholds();
        configuration.GetSection(HealthThresholds.SectionName).Bind(limites);
        limites.Validate();
        services.AddSingleton(limites);

        services.AddOutboxHealth<ConversationsDbContext>("conversations");
        services.AddOutboxHealth<ChannelsDbContext>("channels");
        services.AddOutboxHealth<WorkflowsDbContext>("workflows");
        services.AddChannelsHealth();
        services.AddAgentsHealth();
        services.AddContactCenterHealth();
        return services;
    }
}
