using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Channels.Infrastructure;

public static class ChannelsInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Channels (cadastro de contas, números, unidades autorizadas e credenciais, rota de entrada e
    /// persistência). O host precisa registrar antes o <c>IRequestContextAccessor</c>, o Tenancy (<c>IOrganizationDirectory</c>,
    /// <c>IBusinessUnitDirectory</c> e <c>IDepartmentDirectory</c>) e o Audit (<c>IAuditWriter</c>). A string de conexão é lida só
    /// quando o DbContext é usado. Sem chave de cifra configurada, o módulo sobe e o cadastro funciona, mas nenhuma credencial é
    /// gravada (falha fechada). O Receiver, o Sender e a integração com a Meta NÃO existem aqui: dependem da confirmação dos
    /// parâmetros na documentação oficial (BOUNDARIES.md PRV-2).
    /// </summary>
    public static IServiceCollection AddChannels(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<ChannelSecretOptions>? configureSecrets = null,
        Action<ChannelSendingOptions>? configureSending = null,
        Action<ChannelWebhookOptions>? configureWebhook = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var segredos = new ChannelSecretOptions();
        configureSecrets?.Invoke(segredos);
        services.AddSingleton(segredos);

        // O envio (SND-2: vazão por conta, espera do provedor) e o webhook (endereço público): configuração validada na partida, nunca no meio do processamento.
        var envio = new ChannelSendingOptions();
        configureSending?.Invoke(envio);
        envio.Validate();
        services.AddSingleton(envio);

        var webhook = new ChannelWebhookOptions();
        configureWebhook?.Invoke(webhook);
        services.AddSingleton(webhook);
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<ChannelsDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ChannelsDbContext.Schema)));

        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<ChannelAccountService>();
        services.AddScoped<ChannelInstanceService>();
        services.AddScoped<ChannelRouteResolver>();
        services.AddScoped<ChannelRoutingQuery>();
        services.AddScoped<IChannelRouting>(provider => provider.GetRequiredService<ChannelRoutingQuery>());

        // Modelos de mensagem (templates) aprovados na Meta: o cadastro (administração) e a consulta de módulo a módulo (ADR 0027).
        services.AddScoped<ChannelTemplateService>();
        services.AddScoped<ChannelTemplateQueries>();
        services.AddScoped<IChannelTemplates>(provider => provider.GetRequiredService<ChannelTemplateQueries>());

        // O Receiver, o processamento do Inbox e o Sender (ADR 0011). Os adaptadores de provedor (IChannelProviderAdapter) são registrados pelo HOST (só os hosts referenciam adaptadores): sem nenhum, o webhook
        // responde 503 e o Sender falha a mensagem como "provider-unavailable". O Receiver não chama outro módulo (ARQ-19); quem chama o Conversations são os handlers do Inbox e do Sender, no Worker.
        services.AddScoped<ChannelProviderAdapters>();
        services.AddScoped<IChannelWebhookStore, ChannelWebhookStore>();
        services.AddScoped<IInboundEventWriter, InboundEventWriter>();
        services.AddScoped<IOutboundMessageStore, OutboundMessageStore>();
        services.AddScoped<IAccountSendStateStore, AccountSendStateStore>();
        services.AddScoped<IChannelDiagnosticsStore, ChannelDiagnosticsStore>();
        services.AddScoped<OutboundDiagnosticsService>();
        services.AddScoped<ChannelWebhookService>();
        services.AddScoped<ChannelWebhookAdminService>();
        services.AddScoped<AccountSendingService>();
        services.AddScoped<ChannelReadinessService>();
        services.AddEventHandler<InboundProviderMessageHandler>();
        services.AddEventHandler<InboundProviderStatusHandler>();
        services.AddEventHandler<OutboundMessageRequestedHandler>();

        return services;
    }
}
