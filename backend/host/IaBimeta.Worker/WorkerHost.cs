using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
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
        var builder = Host.CreateApplicationBuilder(args);
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
            segredos => builder.Configuration.GetSection("Channels:Secrets").Bind(segredos));

        // A mesma ligação mediada pelo host do Api (ADR 0008): sem ela as mudanças de unidade seriam recusadas por falha fechada.
        builder.Services.AddScoped<IChannelUnitAuthorization, ChannelsBackedUnitAuthorization>();

        // O Messaging: o escritor do outbox (os comandos do Conversations gravam o evento na mesma transação do estado), as opções
        // e o relay do outbox do Conversations, que é quem produz os eventos consumidos hoje.
        builder.Services.AddMessaging(mensageria => builder.Configuration.GetSection("Messaging").Bind(mensageria));
        builder.Services.AddSingleton<IMessagingScopeInitializer, WorkerScopeInitializer>();
        builder.Services.AddConversations(provider => ConnectionStringOf(provider, "Conversations"));
        builder.Services.AddContactCenter(provider => ConnectionStringOf(provider, "ContactCenter"));
        builder.Services.AddOutboxRelay<ConversationsDbContext>();

        builder.Services.AddSingleton<ContactCenterReconciler>();
        builder.Services.AddHostedService<OutboxRelayService>();
        builder.Services.AddHostedService<ContactCenterReconciliationService>();

        return builder.Build();
    }

    // Todos os módulos usam o mesmo banco por enquanto: a string própria do módulo, senão a padrão e, por compatibilidade, a do Tenancy.
    private static string ConnectionStringOf(IServiceProvider provider, string module)
    {
        var configuracao = provider.GetRequiredService<IConfiguration>();
        return configuracao.GetConnectionString(module)
            ?? configuracao.GetConnectionString("Default")
            ?? configuracao.GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException($"A string de conexão 'ConnectionStrings:{module}' (ou 'Default') não está configurada.");
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
