using IaBimeta.Agents.Infrastructure;
using IaBimeta.Workflows.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Api.Retention;

/// <summary>
/// A composição da retenção de dados (ADR 0025) do host: a política lida da seção <c>Retention</c> (por ambiente, com sobrescritas por empresa) e as tarefas de expurgo TÉCNICO de cada módulo. Só dado técnico entra
/// aqui (sessões vencidas, tokens de redefinição encerrados, contadores de login parados, eventos processados e marcas do Inbox). Dados pessoais e registros de referência legal não têm tarefa de expurgo.
/// </summary>
internal static class RetentionComposition
{
    /// <summary>Registra só a política (o Worker a usa para o expurgo automático dos eventos processados do outbox).</summary>
    public static IServiceCollection AddRetentionPolicy(this IServiceCollection services, IConfiguration configuration)
    {
        var opcoes = new RetentionOptions();
        configuration.GetSection(RetentionOptions.SectionName).Bind(opcoes);
        opcoes.Validate();
        services.AddSingleton(opcoes);

        // O slug vira o tenant pelo diretório do Tenancy (a leitura não exige contexto), num escopo próprio de cada consulta.
        services.AddSingleton<IRetentionPolicy>(provider => new ConfiguredRetentionPolicy(opcoes, async (slug, cancellationToken) =>
        {
            await using var escopo = provider.CreateAsyncScope();
            var tenant = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(slug, cancellationToken);
            return tenant?.TenantId.Value;
        }));
        return services;
    }

    /// <summary>Registra a política e todas as tarefas de expurgo técnico (o comando <c>retention</c>).</summary>
    public static IServiceCollection AddRetention(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddRetentionPolicy(configuration);
        services.AddIdentityRetention();
        services.AddOutboxRetention<ConversationsDbContext>("conversations");
        services.AddAttachmentRetention();
        services.AddSingleton<IRetentionAuditSink, RetentionAuditSink>();
        services.AddOutboxRetention<ChannelsDbContext>("channels");
        services.AddInboxRetention<ChannelsDbContext>("channels");
        services.AddInboxRetention<AgentsDbContext>("agents");
        services.AddAgentsRetention();
        services.AddInboxRetention<WorkflowsDbContext>("workflows");
        services.AddWorkflowsRetention();
        services.AddInboxRetention<ContactCenterDbContext>("contact_center");
        return services;
    }
}
