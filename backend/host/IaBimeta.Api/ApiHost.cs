using IaBimeta.Adapters.Ai.Ollama;
using IaBimeta.Adapters.Ai.OpenAI;
using IaBimeta.Knowledge.Infrastructure;
using IaBimeta.Adapters.WhatsApp;
using IaBimeta.Agents.Presentation;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Api.Channels;
using IaBimeta.Api.ContactCenter;
using IaBimeta.Api.Development;
using IaBimeta.Api.Operations;
using IaBimeta.Api.Proxy;
using IaBimeta.Api.RequestContexts;
using IaBimeta.Api.Retention;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Audit.Presentation;
using IaBimeta.Capabilities.Infrastructure;
using IaBimeta.Capabilities.Presentation;
using IaBimeta.Workflows.Infrastructure;
using IaBimeta.Workflows.Presentation;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Channels.Presentation;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.ContactCenter.Presentation;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Conversations.Presentation;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Identity.Presentation;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Infrastructure;
using IaBimeta.Tenancy.Presentation;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

namespace IaBimeta.Api;

/// <summary>Limite de taxa do login por origem (seção <c>Identity:LoginRateLimit</c>).</summary>
internal sealed class LoginRateLimitOptions
{
    public int PermitLimit { get; set; } = 20;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>Limites de taxa do Embedded por origem de rede (seção <c>Embedded:RateLimit</c>): as chamadas assinadas do servidor do ERP (por chave) e a troca do ticket no iframe (por origem).</summary>
internal sealed class EmbeddedRateLimitOptions
{
    public int IntegrationPermitLimit { get; set; } = 120;

    public int ExchangePermitLimit { get; set; } = 30;

    public int WindowSeconds { get; set; } = 60;
}

public static class ApiHost
{
    /// <summary>
    /// Monta o host. Está separado do <c>Program</c> para que os testes de integração subam a mesma composição
    /// (e a mesma segurança) da aplicação real. O <paramref name="configure"/> permite sobrescrever configuração
    /// (por exemplo, a string de conexão) e serviços (por exemplo, o relógio) antes da composição.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        // Falha na partida se algum recurso de desenvolvimento estiver ligado fora do ambiente Development.
        var desenvolvimento = DevelopmentOnlyGuard.Resolve(builder.Environment, builder.Configuration);

        // O RequestContext é estabelecido só aqui, no host (TEN-1).
        builder.Services.AddScoped<RequestContextHolder>();
        builder.Services.AddScoped<IRequestContextAccessor>(provider => provider.GetRequiredService<RequestContextHolder>());

        // Cada módulo tem esquema, DbContext e migrations próprios, no mesmo banco por enquanto.
        builder.Services.AddAudit(provider => ConnectionStringOf(provider, "Audit"));
        builder.Services.AddTenancy(provider => ConnectionStringOf(provider, "Tenancy"));
        builder.Services.AddIdentityModule(
            provider => ConnectionStringOf(provider, "Identity"),
            opcoes => builder.Configuration.GetSection("Identity").Bind(opcoes),
            // As chaves mestras dos segredos das chaves de integração do Embedded (ADR 0039): do ambiente, nunca de arquivo versionado. Sem chave ativa, criar chave de integração falha fechado.
            segredos => builder.Configuration.GetSection("Identity:Secrets").Bind(segredos));

        // Etapa 4: os eventos duráveis vão para o OUTBOX do módulo emissor, gravados na mesma transação da mudança de estado
        // (BOUNDARIES.md §12.9). Quem os entrega aos consumidores (o relay) é o Worker, que roda como processo à parte: o Api
        // só escreve. O dublê em memória continua registrado SÓ para o simulador de desenvolvimento enxergar os eventos
        // recentes; ele não é durável e não entrega nada a ninguém.
        builder.Services.AddMessaging(mensageria => builder.Configuration.GetSection("Messaging").Bind(mensageria));
        builder.Services.AddSingleton<InMemoryEventPublisher>();
        builder.Services.AddSingleton<IEventPublisher>(provider => provider.GetRequiredService<InMemoryEventPublisher>());

        // A retenção de dados (ADR 0025): a política por ambiente e por empresa e as tarefas de expurgo TÉCNICO, que só rodam pelo comando "retention" (o Api não expurga sozinho).
        builder.Services.AddRetention(builder.Configuration);

        // A supervisão (ADR 0026): limites e sondas de saúde dos módulos, consultados só pelo comando "status".
        builder.Services.AddOperationsHealth(builder.Configuration);

        // Channels: cadastro de contas, números, unidades autorizadas e credenciais. A chave de cifra das credenciais vem da
        // configuração do ambiente (Channels:Secrets: ActiveKeyId e Keys), nunca de arquivo versionado; sem ela o cadastro
        // funciona e só a gravação de credenciais responde 503.
        builder.Services.AddChannels(
            provider => ConnectionStringOf(provider, "Channels"),
            opcoes => builder.Configuration.GetSection("Channels:Secrets").Bind(opcoes),
            envio => builder.Configuration.GetSection("Channels:Sending").Bind(envio),
            webhook => builder.Configuration.GetSection("Channels:Webhook").Bind(webhook));

        // O Conversations pergunta ao Channels, por uma porta própria, se um número pode atender uma unidade. A matriz de
        // dependências não liga os dois módulos: a ligação é feita aqui, no host. Registrada ANTES do AddConversations, que
        // só instala a implementação que nega tudo se não houver outra.
        builder.Services.AddScoped<IChannelUnitAuthorization, ChannelsBackedUnitAuthorization>();
        // Os arquivos de mídia (ADR 0028): Conversations:Attachments (pasta privada, tamanho máximo e tipos permitidos); Api e Worker apontam para a MESMA pasta.
        builder.Services.AddConversations(provider => ConnectionStringOf(provider, "Conversations"), anexos => builder.Configuration.GetSection("Conversations:Attachments").Bind(anexos));

        // ContactCenter (etapa 4): filas, atendentes, entrada de conversas, atribuição, transferência e handoff IA/humano. O
        // ContactCenter chama o Conversations por uma porta própria; a do host age com o contexto de sistema do mesmo tenant,
        // depois de o ContactCenter decidir o acesso pela fila. Registrada ANTES do AddContactCenter, que só instala a padrão
        // (a que usa o contexto de quem chamou) se não houver outra.
        builder.Services.AddScoped<IConversationGateway, ElevatedConversationGateway>();
        builder.Services.AddScoped<IAttachmentGateway, ElevatedAttachmentGateway>();

        // A janela de resposta livre e os modelos de mensagem do número (ADR 0027): o ContactCenter pergunta por uma porta própria e quem responde é o Channels, ligado aqui no host.
        builder.Services.AddScoped<IChannelReplyPolicy, ChannelsBackedReplyPolicy>();
        builder.Services.AddScoped<IaBimeta.Workflows.Contracts.IActivationScopeValidator, WorkflowActivationScopeValidator>();
        builder.Services.AddContactCenter(provider => ConnectionStringOf(provider, "ContactCenter"));

        // AiGateway (ADR 0017, C1.4): a configuração de IA do tenant, a credencial BYOK cifrada, o ledger de uso e o IAiGateway. O catálogo de modelos e os limites padrão vêm da
        // configuração (AiGateway:Catalog e AiGateway:DefaultLimits); as CHAVES MESTRAS (AiGateway:Secrets: ActiveKeyId e Keys) vêm do ambiente, nunca de arquivo versionado, e são
        // diferentes das do Channels. Os adaptadores de provedor (um por provedor, só os hosts os referenciam) entram abaixo: um provedor sem adaptador responde provider-not-registered e nada é chamado.
        builder.Services.AddAiGateway(
            provider => ConnectionStringOf(provider, "AiGateway"),
            segredos => builder.Configuration.GetSection("AiGateway:Secrets").Bind(segredos),
            catalogo => builder.Configuration.GetSection("AiGateway:Catalog").Bind(catalogo),
            limites => builder.Configuration.GetSection("AiGateway:DefaultLimits").Bind(limites),
            builder.Configuration.GetSection("AiGateway:Options").Get<AiGatewayOptions>());

        // O adaptador da OpenAI (C4): HttpClient direto na Responses API, credencial BYOK por chamada, nenhum segredo na configuração.
        builder.Services.AddOpenAiProvider(openai => builder.Configuration.GetSection("AiGateway:Providers:OpenAi").Bind(openai));

        // O adaptador do WhatsApp Cloud API (Meta): o Receiver do webhook o usa para verificar a assinatura e normalizar o corpo. Nenhum segredo na configuração: o App Secret e o token de acesso são por conta, cifrados.
        builder.Services.AddWhatsAppAdapter(whatsapp => builder.Configuration.GetSection("Channels:Providers:WhatsApp").Bind(whatsapp));

        // O Agents (C2): o Api só precisa dos casos de uso da definição do agente do tenant (a preparação de desenvolvimento os chama; a API administrativa virá depois). Quem entrega os eventos e executa os
        // turnos é o Worker: aqui não há relay nem executor, então nenhum turno de IA roda dentro do Api.
        builder.Services.AddAgents(
            provider => ConnectionStringOf(provider, "Agents"),
            agentes => builder.Configuration.GetSection("Agents:Options").Bind(agentes));

        // O Knowledge (ADR 0034): o índice semântico com pgvector. Desligado por padrão (Knowledge:Semantic:Enabled). O Api só grava o que deve ser vetorizado quando o conhecimento é publicado; quem vetoriza é o Worker.
        builder.Services.AddOllamaProvider(ollama => builder.Configuration.GetSection("Knowledge:Providers:Ollama").Bind(ollama));
        builder.Services.AddKnowledge(
            provider => ConnectionStringOf(provider, "Knowledge"),
            semantico => builder.Configuration.GetSection("Knowledge:Semantic").Bind(semantico));

        // O Motor de Integração Empresarial (ADR 0035, módulo Capabilities): conexões, operações versionadas e execução governada. As opções (limites, política de destino) vêm de Capabilities:Options e as CHAVES MESTRAS
        // dos segredos (Capabilities:Secrets: ActiveKeyId e Keys) vêm do ambiente, nunca de arquivo versionado. Nenhum sistema externo é chamado sem uma conexão habilitada por um administrador.
        builder.Services.AddCapabilities(
            provider => ConnectionStringOf(provider, "Capabilities"),
            opcoes => builder.Configuration.GetSection("Capabilities:Options").Bind(opcoes),
            segredos => builder.Configuration.GetSection("Capabilities:Secrets").Bind(segredos));

        // O Chatbot Visual (ADR 0037, módulo Workflows): fluxos versionados, validação e simulação. As opções vêm de Workflows:Options; o runtime (Enabled) nasce DESLIGADO.
        builder.Services.AddWorkflows(
            provider => ConnectionStringOf(provider, "Workflows"),
            opcoes => builder.Configuration.GetSection("Workflows:Options").Bind(opcoes));

        // Enums saem e entram como texto (por exemplo, "HumanAssisted"), não como número.
        builder.Services.ConfigureHttpJsonOptions(opcoes => opcoes.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // Fontes de contexto confiáveis, na ordem em que são consultadas: a sessão autenticada primeiro. A de
        // desenvolvimento só é registrada no ambiente Development e com habilitação explícita. Em produção só existe
        // a sessão: sem token válido, toda operação que exige tenant é rejeitada.
        builder.Services.AddScoped<ITenantContextSource, SessionTenantContextSource>();
        if (desenvolvimento.HeaderEnabled)
        {
            builder.Services.AddScoped<ITenantContextSource, DevelopmentTenantHeaderSource>();
        }

        if (desenvolvimento.ApplyMigrations || desenvolvimento.SeedTenants.Count > 0)
        {
            builder.Services.AddSingleton(desenvolvimento);
            builder.Services.AddHostedService<DevelopmentTenancySetup>();
        }

        if (desenvolvimento.ApplyMigrations || desenvolvimento.SeedAdministrators.Count > 0)
        {
            builder.Services.TryAddSingleton(desenvolvimento);
            builder.Services.AddHostedService<DevelopmentIdentitySetup>();
        }

        // A IA de desenvolvimento (organização, fila, rota, configuração do tenant e agente publicado) roda DEPOIS das migrations e do cadastro dos tenants (os serviços hospedados partem na ordem do registro).
        if (desenvolvimento.Ai.Enabled)
        {
            builder.Services.TryAddSingleton(desenvolvimento);
            builder.Services.AddHostedService<DevelopmentAiSetup>();
        }

        if (desenvolvimento.SimulatorEnabled)
        {
            builder.Services.TryAddSingleton(desenvolvimento);
        }

        // Limite de taxa do login por origem (IP), antes de qualquer consulta ao banco. Vale para quem insiste em tenant ou
        // e-mail inexistente, que não têm contagem própria. Atrás de proxy a origem é a do proxy: só com ForwardedHeaders ligado
        // (ADR 0033, proxies e redes confiáveis explícitos) o endereço usado aqui é o do cliente de verdade.
        var limiteDeLogin = builder.Configuration.GetSection("Identity:LoginRateLimit").Get<LoginRateLimitOptions>() ?? new LoginRateLimitOptions();
        var limiteEmbedded = builder.Configuration.GetSection("Embedded:RateLimit").Get<EmbeddedRateLimitOptions>() ?? new EmbeddedRateLimitOptions();
        builder.Services.AddRateLimiter(opcoes =>
        {
            opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Chamadas assinadas do servidor do ERP: cota anterior à autenticação SÓ por origem de rede (nada do cabeçalho define a partição). A cota por chave conhecida e por conexão autenticada é do Identity.
            opcoes.AddPolicy(EmbeddedRateLimits.IntegrationPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                Embedded.EmbeddedHostPolicies.IntegrationPartition(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limiteEmbedded.IntegrationPermitLimit,
                    Window = TimeSpan.FromSeconds(limiteEmbedded.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            // Troca do ticket (anônima): por origem de rede, antes de qualquer consulta ao banco.
            opcoes.AddPolicy(EmbeddedRateLimits.ExchangePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "origem-desconhecida",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limiteEmbedded.ExchangePermitLimit,
                    Window = TimeSpan.FromSeconds(limiteEmbedded.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            opcoes.AddPolicy(IdentityEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "origem-desconhecida",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limiteDeLogin.PermitLimit,
                    Window = TimeSpan.FromSeconds(limiteDeLogin.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });

        var ingressoPublico = builder.Configuration.GetSection("Channels:Webhook").Get<PublicIngressOptions>() ?? new PublicIngressOptions();

        // Atrás de um proxy reverso (ADR 0033): confia nos cabeçalhos de encaminhamento SÓ dos proxies e redes configurados. Desligado por padrão.
        var encaminhamento = builder.Configuration.GetSection("ForwardedHeaders").Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        encaminhamento.Validate();
        if (encaminhamento.Enabled)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(encaminhamento.Apply);
        }

        var app = builder.Build();

        // Primeiro da fila: o guarda do ingresso, o limite de login e o resto já veem o endereço REAL do cliente (e o esquema https do proxy).
        if (encaminhamento.Enabled)
        {
            app.UseForwardedHeaders();
        }

        // O ingresso PÚBLICO (túnel HTTPS) só alcança o webhook do provedor: nada de login, simulador nem cabeçalho de desenvolvimento pela internet. Primeiro da fila, antes de qualquer outra coisa.
        app.UseMiddleware<PublicIngressGuard>(ingressoPublico);
        app.UseRateLimiter();
        app.UseMiddleware<RequestContextMiddleware>();

        // A sessão embutida (Embedded, ADR 0039) só alcança a lista fechada de rotas; o resto responde 403, existindo ou não.
        app.UseMiddleware<Embedded.EmbeddedRouteGuard>();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapIdentityEndpoints();
        app.MapTenancyEndpoints();
        app.MapAuditEndpoints();
        app.MapConversationsEndpoints();
        app.MapChannelsEndpoints();

        // A API administrativa do agente do tenant (ADR 0030): revisão das lacunas do FAQ, rascunho e publicação da definição.
        app.MapAgentsEndpoints();

        // A API do Motor de Integração Empresarial (ADR 0035): conexões, operações, execuções e histórico.
        app.MapCapabilitiesEndpoints();
        app.MapWorkflowsEndpoints();

        // O Receiver do webhook do provedor: anônimo, autenticado pela assinatura do corpo (ADR 0011, seção C).
        app.MapChannelWebhookEndpoints();
        app.MapContactCenterEndpoints();

        if (desenvolvimento.SimulatorEnabled)
        {
            app.MapSimulatorEndpoints();
        }

        return app;
    }

    // Todos os módulos usam o mesmo banco por enquanto: a string própria do módulo, se houver, senão a padrão
    // e, por compatibilidade, a do Tenancy (a primeira a existir).
    private static string ConnectionStringOf(IServiceProvider provider, string module)
    {
        var configuracao = provider.GetRequiredService<IConfiguration>();
        var texto = configuracao.GetConnectionString(module)
            ?? configuracao.GetConnectionString("Default")
            ?? configuracao.GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException($"A string de conexão 'ConnectionStrings:{module}' (ou 'Default') não está configurada.");
        return WithApplicationName(texto, "IaBimeta.Api");
    }

    // O nome da aplicação nas conexões deixa o diagnóstico enxergar quem está ligado ao banco (por exemplo, o Worker vivo). Um nome já definido na configuração vale.
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
