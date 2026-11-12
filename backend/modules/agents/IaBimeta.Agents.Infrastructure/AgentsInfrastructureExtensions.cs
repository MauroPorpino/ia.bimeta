using IaBimeta.Agents.Application;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Agents.Domain;
using IaBimeta.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Agents.Infrastructure;

public static class AgentsInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Agents (ADR 0017): o turno de IA, o agendamento pelos eventos do Conversations, a definição do agente do tenant e a persistência. O host precisa registrar antes o
    /// <c>IRequestContextAccessor</c>, o Audit (<c>IAuditWriter</c>), o Messaging (<c>AddMessaging</c>, que dá o <c>IMessagingScopeInitializer</c> ao relay), o Conversations (<c>ITurnCommands</c>), o AiGateway
    /// (<c>IAiGateway</c>) e o ContactCenter (<c>IHandoffRequests</c>). A string de conexão é lida só quando o DbContext é usado. Quem entrega os eventos é o relay do Worker (o Api só registra
    /// os handlers, nenhum é chamado fora do relay), e quem executa os turnos é o executor do Worker: o Agents nunca chama o modelo dentro de um handler.
    /// </summary>
    /// <param name="configure">Ajusta as opções (agrupamento, tentativas, tempos, capacidade); a configuração do host liga a seção <c>Agents:Options</c>.</param>
    public static IServiceCollection AddAgents(this IServiceCollection services, Func<IServiceProvider, string> connectionString, Action<AgentsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var opcoes = new AgentsOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();
        services.AddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<AgentsDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AgentsDbContext.Schema)));

        services.AddScoped<IAiTurnStore, AiTurnStore>();
        services.AddScoped<AgentDefinitionStore>();
        services.AddScoped<IAgentDefinitionStore>(provider => provider.GetRequiredService<AgentDefinitionStore>());
        services.AddScoped<IAgentDefinitionReader>(provider => provider.GetRequiredService<AgentDefinitionStore>());
        services.AddScoped<IPendingTurnSource, PendingTurnSource>();
        services.AddScoped<TurnScheduler>();
        services.AddScoped<TurnRunner>();

        // ADR 0036: a consulta do agente a operações PÚBLICAS de leitura do Motor de Integração. Só existe se o host compôs o Capabilities (o catálogo e o executor); desligada por padrão (Agents:Options:IntegrationLookupsEnabled).
        services.AddScoped<IntegrationLookupService>(provider =>
        {
            var catalogo = provider.GetService<IaBimeta.Capabilities.Contracts.ICapabilityCatalog>();
            var executor = provider.GetService<IaBimeta.Capabilities.Contracts.ICapabilityExecutor>();
            var auditoria = provider.GetService<IaBimeta.Audit.Contracts.Commands.IAuditWriter>();
            return catalogo is null || executor is null || auditoria is null ? null! : new IntegrationLookupService(catalogo, executor, auditoria, provider.GetRequiredService<AgentsOptions>());
        });
        services.AddScoped<AgentDefinitionService>();

        // As lacunas do FAQ (ADR 0030): o registro (melhor esforço, só referências e um hash com segredo do tenant) e a revisão do administrador. Sem Agents:Options:KnowledgeGapHashKey o registro fica desligado.
        services.AddSingleton(new KnowledgeGapHasherHolder(KnowledgeGapHasher.FromBase64(opcoes.KnowledgeGapHashKey)));
        services.AddScoped<IKnowledgeGapStore, KnowledgeGapStore>();
        services.AddScoped<IKnowledgeGapRecorder, KnowledgeGapRecorder>();
        services.AddScoped<KnowledgeGapService>();

        // A resposta direta por similaridade (ADR 0034): a configuração por tenant (kill switch e limiares), o registro das decisões (só números) e o caso de uso do administrador. Quem busca é o Knowledge.
        services.AddScoped<ISemanticAnswerSettingsStore, SemanticAnswerSettingsStore>();
        services.AddScoped<ISemanticDecisionRecorder, SemanticDecisionRecorder>();
        services.AddScoped<SemanticAnswerSettingsService>();
        services.AddScoped<ISemanticMetricsStore, SemanticMetricsStore>();
        services.AddScoped<SemanticMetricsService>();

        // As sugestoes da IA local para o FAQ (ADR 0034): a IA so propoe; esta e a revisao humana. Desligado por padrao (Agents:Options:LocalSuggestionsEnabled).
        services.AddScoped<IFaqSuggestionStore, FaqSuggestionStore>();
        services.AddScoped<FaqSuggestionService>();

        // Consumo durável do estado de atendimento do Conversations: o inbox do módulo e os handlers (que só gravam o AiTurn).
        services.AddInboxGate<AgentsDbContext>();
        services.AddEventHandler<InboundMessageRegisteredHandler>();
        services.AddEventHandler<MessageContentReadyHandler>();
        services.AddEventHandler<MessageContentFailedHandler>();
        services.AddEventHandler<HandlingModeChangedHandler>();
        services.AddEventHandler<SessionClosedHandler>();
        services.AddEventHandler<SessionTransferredHandler>();
        services.AddEventHandler<ConversationLocaleChangedHandler>();
        services.AddEventHandler<FlowReleasedToAgentHandler>();

        // A porta de conhecimento do chatbot visual (ADR 0037): o nó "knowledge" reaproveita o FAQ aprovado e o índice semântico, sem outro mecanismo de conhecimento.
        services.AddScoped<IFlowKnowledgeLookup, FlowKnowledgeLookup>();

        return services;
    }
}
