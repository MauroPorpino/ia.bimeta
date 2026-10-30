using IaBimeta.ContactCenter.Application;
using IaBimeta.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.ContactCenter.Infrastructure;

public static class ContactCenterInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo ContactCenter (filas de atendimento, atendentes das filas e persistência). O host precisa registrar antes
    /// o <c>IRequestContextAccessor</c>, o Tenancy (<c>IOrganizationDirectory</c>, <c>IBusinessUnitDirectory</c> e
    /// <c>IDepartmentDirectory</c>), o Identity (<c>IUserDirectory</c>) e o Audit (<c>IAuditWriter</c>), além do Conversations
    /// (<c>IConversationQueries</c> e <c>IHandlingCommands</c>). A string de conexão é lida só quando o DbContext é usado. O
    /// módulo fala com o Conversations por uma porta (<see cref="IConversationGateway"/>) que o host pode substituir registrando
    /// a sua ANTES desta chamada: a padrão usa o contexto de quem chamou (falha fechada para o atendente comum).
    /// </summary>
    public static IServiceCollection AddContactCenter(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<ContactCenterDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ContactCenterDbContext.Schema)));

        services.AddScoped<IQueueRepository, QueueRepository>();
        services.AddScoped<IQueueItemRepository, QueueItemRepository>();
        services.AddScoped<IRoutingRepository, RoutingRepository>();
        services.AddScoped<QueueRouter>();
        services.AddScoped<QueuePolicyService>();
        services.AddScoped<RoutingRuleService>();
        services.AddScoped<AgentPresenceService>();
        services.TryAddScoped<IConversationGateway, DirectConversationGateway>();
        services.AddScoped<ServiceQueueService>();
        services.AddScoped<HandlingExecutor>();
        services.AddScoped<QueueItemService>();

        // Consumo durável do estado de atendimento do Conversations (etapa 4): o inbox do módulo e os handlers. Quem entrega os
        // eventos é o relay do Worker; o Api só registra os handlers (nenhum é chamado fora do relay).
        services.AddInboxGate<ContactCenterDbContext>();
        services.AddScoped<ConversationProjectionService>();
        services.AddEventHandler<HandlingModeChangedHandler>();
        services.AddEventHandler<SessionClosedHandler>();
        services.AddEventHandler<SessionTransferredHandler>();
        services.AddScoped<IPendingWorkSource, PendingWorkSource>();
        services.AddScoped<IDeferredAudit, DeferredAudit>();

        return services;
    }
}
