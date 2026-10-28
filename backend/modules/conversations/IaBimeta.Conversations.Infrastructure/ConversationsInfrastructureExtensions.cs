using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Conversations.Infrastructure;

public static class ConversationsInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Conversations (casos de uso, persistência e DbContext). O host precisa registrar antes o
    /// <c>IRequestContextAccessor</c>, o Tenancy (<c>IOrganizationDirectory</c> e <c>IBusinessUnitDirectory</c>), o Audit
    /// (<c>IAuditWriter</c>) e um <c>IEventPublisher</c> (na etapa 3, o dublê em memória; o outbox durável nasce na etapa 4).
    /// A string de conexão é lida só quando o DbContext é usado.
    /// </summary>
    public static IServiceCollection AddConversations(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<ConversationsDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ConversationsDbContext.Schema)));

        services.AddScoped<IConversationStore, ConversationStore>();

        // A autorização de unidade por canal é do Channels, e o host a implementa sobre o Channels.Contracts (a matriz não deixa
        // o Conversations referenciar o Channels). O host registra a implementação ANTES desta chamada; sem ela, vale a que nega
        // tudo (falha fechada).
        services.TryAddScoped<IChannelUnitAuthorization, DenyAllChannelUnitAuthorization>();

        // Cada interface de contrato é a porta de um consumidor: o Channels usa a ingestão, o Agents os turnos, o
        // ContactCenter o estado de atendimento, e quem lê o histórico usa as consultas.
        services.AddScoped<InboundMessageService>();
        services.AddScoped<IConversationIngestion>(provider => provider.GetRequiredService<InboundMessageService>());
        services.AddScoped<TurnService>();
        services.AddScoped<ITurnCommands>(provider => provider.GetRequiredService<TurnService>());
        services.AddScoped<HandlingService>();
        services.AddScoped<IHandlingCommands>(provider => provider.GetRequiredService<HandlingService>());
        services.AddScoped<ConversationQueryService>();
        services.AddScoped<IConversationQueries>(provider => provider.GetRequiredService<ConversationQueryService>());

        return services;
    }
}
