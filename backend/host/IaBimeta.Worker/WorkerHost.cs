using IaBimeta.Agents.Infrastructure;
using IaBimeta.Adapters.Ai.Ollama;
using IaBimeta.Adapters.Ai.OpenAI;
using IaBimeta.Knowledge.Infrastructure;
using IaBimeta.Adapters.WhatsApp;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Infrastructure;

namespace IaBimeta.Worker;

/// <summary>Configuração do Worker (seção <c>Worker</c>): o que o laço de reconciliação do ContactCenter faz e de quanto em quanto tempo.</summary>
public sealed class WorkerOptions
{
    /// <summary>De quanto em quanto tempo o Worker procura operações do ContactCenter que ficaram pendentes.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Só retoma o que está pendente há pelo menos este tempo, para não disputar com a requisição que acabou de gravar a intenção.</summary>
    public TimeSpan ReconcileMinimumAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Quantas operações pendentes cada tenant reconcilia por ciclo.</summary>
    public int ReconcileBatchSize { get; set; } = 50;

    /// <summary>Quantos tenants o ciclo de reconciliação atende por vez.</summary>
    public int ReconcileTenantBatchSize { get; set; } = 100;

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        if (ReconcileInterval <= TimeSpan.Zero || ReconcileMinimumAge < TimeSpan.Zero || ReconcileBatchSize is < 1 or > 100 || ReconcileTenantBatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("A configuração do Worker é inválida (intervalo, idade mínima ou tamanhos de lote).");
        }
    }
}

/// <summary>
/// O host do processamento assíncrono (BOUNDARIES.md §12): o relay do outbox do Conversations, que entrega os eventos aos handlers
/// dos módulos consumidores (hoje, o ContactCenter), e o laço que conclui as operações do ContactCenter que ficaram pendentes. Ele
/// está separado do <c>Main</c> para os testes de integração subirem a mesma composição do processo real.
/// <para>
/// O <see cref="RequestContext"/> de cada entrega é criado AQUI, pelo tenant do envelope gravado pelo módulo emissor (TEN-1), e é
/// sempre um contexto de sistema: o Worker não age como usuário. Não aplica migrations: o esquema é do Api/da implantação.
/// </para>
/// </summary>
public static class WorkerHost
{
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        // A raiz de conteúdo é a pasta do binário, não a pasta de onde o processo foi iniciado: o appsettings.json do Worker (catálogo de modelos e limites padrão de IA) é lido de lá, qualquer que seja o
        // diretório corrente. Sem isto, um Worker iniciado de outra pasta subia SEM o catálogo e sem os limites, e toda chamada de IA terminava em "não configurada".
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var opcoes = new WorkerOptions();
        builder.Configuration.GetSection("Worker").Bind(opcoes);
        opcoes.Validate();
        builder.Services.AddSingleton(opcoes);

        // O RequestContext é estabelecido só nos hosts (TEN-1), por escopo de entrega.
        builder.Services.AddScoped<RequestContextHolder>();
        builder.Services.AddScoped<IRequestContextAccessor>(provider => provider.GetRequiredService<RequestContextHolder>());

        // Os mesmos módulos que o Api compõe, sem a camada de apresentação: os handlers e os comandos do Conversations rodam aqui.
        builder.Services.AddAudit(provider => ConnectionStringOf(provider, "Audit"));
        builder.Services.AddTenancy(provider => ConnectionStringOf(provider, "Tenancy"));
        builder.Services.AddIdentityModule(
            provider => ConnectionStringOf(provider, "Identity"),
            identidade => builder.Configuration.GetSection("Identity").Bind(identidade));
        builder.Services.AddChannels(
            provider => ConnectionStringOf(provider, "Channels"),
            segredos => builder.Configuration.GetSection("Channels:Secrets").Bind(segredos),
            envio => builder.Configuration.GetSection("Channels:Sending").Bind(envio),
            webhook => builder.Configuration.GetSection("Channels:Webhook").Bind(webhook));

        // A mesma ligação mediada pelo host do Api (ADR 0008): sem ela as mudanças de unidade seriam recusadas por falha fechada.
        builder.Services.AddScoped<IChannelUnitAuthorization, ChannelsBackedUnitAuthorization>();

        // O Messaging: o escritor do outbox (os comandos do Conversations gravam o evento na mesma transação do estado), as opções
        // e o relay do outbox do Conversations, que é quem produz os eventos consumidos hoje.
        builder.Services.AddMessaging(mensageria => builder.Configuration.GetSection("Messaging").Bind(mensageria));
        builder.Services.AddSingleton<IMessagingScopeInitializer, WorkerScopeInitializer>();

        // A política de retenção (ADR 0025), a mesma do Api (seção Retention): o relay a usa para expurgar os eventos PROCESSADOS do outbox com o prazo de cada empresa. As demais tarefas (sessões, tokens, Inbox) só
        // rodam pelo comando "retention" do Api. Configuração inválida falha na partida.
        var retencao = new RetentionOptions();
        builder.Configuration.GetSection(RetentionOptions.SectionName).Bind(retencao);
        retencao.Validate();
        builder.Services.AddSingleton(retencao);
        builder.Services.AddSingleton<IRetentionPolicy>(provider => new ConfiguredRetentionPolicy(retencao, async (slug, cancellationToken) =>
        {
            await using var escopoDaConsulta = provider.CreateAsyncScope();
            var tenant = await escopoDaConsulta.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(slug, cancellationToken);
            return tenant?.TenantId.Value;
        }));
        // Os arquivos de mídia (ADR 0028): Conversations:Attachments (pasta privada, tamanho máximo e tipos permitidos); Api e Worker apontam para a MESMA pasta.
        builder.Services.AddConversations(provider => ConnectionStringOf(provider, "Conversations"), anexos => builder.Configuration.GetSection("Conversations:Attachments").Bind(anexos));
        builder.Services.AddContactCenter(provider => ConnectionStringOf(provider, "ContactCenter"));

        // O AiGateway que o Agents (C2) vai chamar neste processo: a mesma composição do Api, com a mesma configuração (catálogo e limites padrão em AiGateway:Catalog e
        // AiGateway:DefaultLimits) e as mesmas chaves mestras do ambiente (AiGateway:Secrets). A expiração de vagas acontece no próprio TryBegin: não há varredor periódico (fica para depois do MVP).
        builder.Services.AddAiGateway(
            provider => ConnectionStringOf(provider, "AiGateway"),
            segredos => builder.Configuration.GetSection("AiGateway:Secrets").Bind(segredos),
            catalogo => builder.Configuration.GetSection("AiGateway:Catalog").Bind(catalogo),
            limites => builder.Configuration.GetSection("AiGateway:DefaultLimits").Bind(limites),
            builder.Configuration.GetSection("AiGateway:Options").Get<AiGatewayOptions>());

        // O adaptador da OpenAI (C4): o Worker é quem chama o modelo (o executor de turnos do Agents). Nenhum segredo na configuração: a credencial BYOK de cada tenant chega por chamada.
        builder.Services.AddOpenAiProvider(openai => builder.Configuration.GetSection("AiGateway:Providers:OpenAi").Bind(openai));
        builder.Services.AddOutboxRelay<ConversationsDbContext>();

        // O Inbox do Channels (ADR 0011): o Receiver do Api grava os eventos do provedor no outbox do Channels, e este relay os entrega, em ordem por lane, aos handlers do próprio módulo (a ingestão no
        // Conversations e o registro dos status de entrega). O Sender consome, pelo relay do Conversations, a saída liberada para envio.
        builder.Services.AddOutboxRelay<ChannelsDbContext>();

        // O adaptador do WhatsApp Cloud API: o Sender envia a mensagem de saída por ele (o token de acesso da conta é decifrado a cada envio).
        builder.Services.AddWhatsAppAdapter(whatsapp => builder.Configuration.GetSection("Channels:Providers:WhatsApp").Bind(whatsapp));

        // O Agents (C2, ADR 0017): o turno de IA. Os handlers só gravam o AiTurn (o relay os entrega); o executor e o reconciliador abaixo é que rodam os turnos, no contexto de sistema de cada
        // tenant. As opções (agrupamento, tentativas, tempos, capacidade) vêm da seção Agents:Options, com a referência aprovada como padrão.
        builder.Services.AddAgents(
            provider => ConnectionStringOf(provider, "Agents"),
            agentes => builder.Configuration.GetSection("Agents:Options").Bind(agentes));

        // O Knowledge (ADR 0034): o Worker vetoriza o conhecimento publicado (SemanticIndexer) e, no Pacote 2, busca por similaridade no turno. Desligado por padrão (Knowledge:Semantic:Enabled).
        builder.Services.AddOllamaProvider(ollama => builder.Configuration.GetSection("Knowledge:Providers:Ollama").Bind(ollama));
        builder.Services.AddKnowledge(
            provider => ConnectionStringOf(provider, "Knowledge"),
            semantico => builder.Configuration.GetSection("Knowledge:Semantic").Bind(semantico));
        builder.Services.AddHostedService<SemanticIndexerService>();

        // Um resumo (sem segredo) da configuração de IA que ESTE processo enxerga, na partida: um Worker sem o catálogo, sem os limites ou sem a chave mestra falha cada turno com "não configurada".
        builder.Services.AddHostedService<AiConfigurationReport>();

        builder.Services.AddSingleton<ContactCenterReconciler>();
        builder.Services.AddSingleton<AgentsTurnExecutor>();
        builder.Services.AddSingleton<AgentsReconciler>();
        builder.Services.AddHostedService<OutboxRelayService>();

        // O expurgo técnico agendado (ADR 0026): as mesmas tarefas do comando "retention purge" do Api, só dado técnico, com os prazos por empresa e os bloqueios de segurança. Desligado por padrão
        // (Retention:Schedule:Enabled). Dado pessoal e registros de referência não têm tarefa de expurgo.
        builder.Services.AddIdentityRetention();
        builder.Services.AddOutboxRetention<ConversationsDbContext>("conversations");
        builder.Services.AddAttachmentRetention();
        builder.Services.AddSingleton<IRetentionAuditSink, RetentionAuditSink>();
        builder.Services.AddOutboxRetention<ChannelsDbContext>("channels");
        builder.Services.AddInboxRetention<ChannelsDbContext>("channels");
        builder.Services.AddInboxRetention<AgentsDbContext>("agents");
        builder.Services.AddInboxRetention<ContactCenterDbContext>("contact_center");
        builder.Services.AddHostedService<RetentionSchedulerService>();
        builder.Services.AddHostedService<ContactCenterReconciliationService>();
        builder.Services.AddHostedService<AgentsTurnExecutorService>();
        builder.Services.AddHostedService<AgentsReconciliationService>();

        return builder.Build();
    }

    // Todos os módulos usam o mesmo banco por enquanto: a string própria do módulo, senão a padrão e, por compatibilidade, a do Tenancy.
    private static string ConnectionStringOf(IServiceProvider provider, string module)
    {
        var configuracao = provider.GetRequiredService<IConfiguration>();
        var texto = configuracao.GetConnectionString(module)
            ?? configuracao.GetConnectionString("Default")
            ?? configuracao.GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException($"A string de conexão 'ConnectionStrings:{module}' (ou 'Default') não está configurada.");
        return WithApplicationName(texto, "IaBimeta.Worker");
    }

    // O nome da aplicação nas conexões é como o comando "status" enxerga que o Worker está vivo (o laço do relay consulta o banco a cada poucos segundos). Um nome já definido na configuração vale.
    private static string WithApplicationName(string connectionString, string name)
    {
        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrEmpty(builder.ApplicationName))
            {
                builder.ApplicationName = name;
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }
}

/// <summary>
/// Cria o contexto confiável do tenant do evento no escopo da entrega. É contexto de SISTEMA (sem usuário): o tenant vem do envelope
/// gravado pelo módulo emissor, nunca do payload, e o handler só alcança o que as portas do módulo dele permitem.
/// </summary>
internal sealed class WorkerScopeInitializer : IMessagingScopeInitializer
{
    public void Initialize(IServiceScope scope, TenantId tenant, string origin) =>
        scope.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant, origin));
}
