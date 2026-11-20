using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
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
    public static IServiceCollection AddConversations(this IServiceCollection services, Func<IServiceProvider, string> connectionString, Action<AttachmentOptions>? configureAttachments = null)
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
        services.AddScoped<IFlowCommands>(provider => provider.GetRequiredService<TurnService>());
        services.AddScoped<HandlingService>();
        services.AddScoped<IHandlingCommands>(provider => provider.GetRequiredService<HandlingService>());
        services.AddScoped<IOutboundDispatchReader, OutboundDispatchReader>();
        services.AddScoped<OutboundDispatchQueryService>();
        services.AddScoped<IOutboundDispatchQueries>(provider => provider.GetRequiredService<OutboundDispatchQueryService>());
        services.AddScoped<OutboundDeliveryQueryService>();
        services.AddScoped<IOutboundDeliveryQueries>(provider => provider.GetRequiredService<OutboundDeliveryQueryService>());
        // Os arquivos de mídia (ADR 0028): armazenamento privado (a pasta de Conversations:Attachments:RootPath; trocar por S3 ou Azure Blob é trocar IAttachmentContentStore) e o ponto de ligação de um
        // mecanismo de varredura (sem nenhum, NotScanned). Api e Worker precisam apontar para a MESMA pasta: o Worker guarda e o Api serve.
        var anexos = new AttachmentOptions();
        configureAttachments?.Invoke(anexos);
        anexos.Validate();
        services.AddSingleton(anexos);
        // O armazenamento é SEMPRE envolvido pela cifra (ADR 0032): com chave ativa grava cifrado; sem ela grava em texto claro, mas nunca entrega como arquivo um objeto cifrado (a leitura reconhece o cabeçalho).
        services.TryAddSingleton(_ => new EncryptedAttachmentContentStore(new FileSystemAttachmentContentStore(anexos), anexos.Encryption));
        services.TryAddSingleton<IAttachmentContentStore>(provider => provider.GetRequiredService<EncryptedAttachmentContentStore>());
        services.AddScoped<AttachmentMaintenanceService>(provider => new AttachmentMaintenanceService(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<EncryptedAttachmentContentStore>(), anexos, provider.GetRequiredService<IAttachmentScanner>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<IRetentionAuditSink>()));
        // A varredura (ADR 0032): a detecção embutida da amostra EICAR sempre, e o ClamAV (clamd) se configurado. Com Scanner:Required, só o arquivo verificado como limpo é liberado.
        services.TryAddSingleton<IAttachmentScanner>(_ => new ConfiguredAttachmentScanner(
            anexos.Scanner.Mode == AttachmentScannerMode.ClamAv ? new ClamAvAttachmentScanner(anexos.Scanner.Host, anexos.Scanner.Port, anexos.Scanner.Timeout) : null));
        services.AddScoped<AttachmentService>();
        services.AddScoped<IAttachmentCommands>(provider => provider.GetRequiredService<AttachmentService>());
        services.AddScoped<IAttachmentQueries>(provider => provider.GetRequiredService<AttachmentService>());

        // O arquivo que o atendente ENVIA (ADR 0031): o mesmo armazenamento e a mesma varredura, validação própria do arquivo de saída, e a leitura do conteúdo pelo Sender (contexto de sistema).
        services.AddSingleton<OutboundAttachmentWriter>();
        services.AddScoped<IOutboundAttachmentReader>(provider => provider.GetRequiredService<AttachmentService>());

        services.AddScoped<ConversationQueryService>();
        services.AddScoped<IConversationQueries>(provider => provider.GetRequiredService<ConversationQueryService>());

        // O vínculo entre o cliente do sistema externo e o contato (ADR 0041, E2.2): o serviço serve as rotas assinadas do sistema externo (Presentation) e o atendente em sessão embutida (ContactCenter, pela porta).
        // A auditoria vai ao outbox do módulo na transação do efeito e é entregue pelo handler (o relay do Worker).
        services.AddScoped<IContactLinkStore, ContactLinkStore>();
        services.AddScoped<ContactLinkService>();
        services.AddScoped<IContactLinkCommands>(provider => provider.GetRequiredService<ContactLinkService>());
        services.AddEventHandler<ConversationAuditHandler>();

        // As consultas do vínculo para o ContactCenter (E2.3): histórico por cliente e leitura do CustomerOnly.
        services.AddScoped<IContactLinkReader, ContactLinkReader>();
        services.AddScoped<ContactLinkQueryService>();
        services.AddScoped<IContactLinkQueries>(provider => provider.GetRequiredService<ContactLinkQueryService>());

        // O consentimento (E2.4): registro pelo servidor do sistema externo (Presentation) e pelo atendente com ConsentManage (ContactCenter, pela porta), resolução de possível saída e consulta. A auditoria usa o
        // mesmo outbox e o mesmo handler do vínculo.
        services.AddScoped<IConsentStore, ConsentStore>();
        services.AddScoped<ConsentService>();
        services.AddScoped<IConsentCommands>(provider => provider.GetRequiredService<ConsentService>());
        services.AddScoped<IConsentReader, ConsentReader>();
        services.AddScoped<ConsentQueryService>();
        services.AddScoped<IConsentQueries>(provider => provider.GetRequiredService<ConsentQueryService>());

        return services;
    }
}
