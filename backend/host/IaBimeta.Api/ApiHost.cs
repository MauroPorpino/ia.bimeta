using IaBimeta.Api.Channels;
using IaBimeta.Api.Development;
using IaBimeta.Api.RequestContexts;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Audit.Presentation;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Channels.Presentation;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Conversations.Presentation;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Identity.Presentation;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Infrastructure;
using IaBimeta.Tenancy.Presentation;
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
            opcoes => builder.Configuration.GetSection("Identity").Bind(opcoes));

        // Etapa 3: o publicador de eventos é o dublê em memória (NÃO durável). O outbox transacional e o relay nascem na
        // etapa 4 (BOUNDARIES.md §12.12) e entram aqui no lugar dele, sem mudar os módulos.
        builder.Services.AddSingleton<InMemoryEventPublisher>();
        builder.Services.AddSingleton<IEventPublisher>(provider => provider.GetRequiredService<InMemoryEventPublisher>());

        // Channels: cadastro de contas, números, unidades autorizadas e credenciais. A chave de cifra das credenciais vem da
        // configuração do ambiente (Channels:Secrets: ActiveKeyId e Keys), nunca de arquivo versionado; sem ela o cadastro
        // funciona e só a gravação de credenciais responde 503.
        builder.Services.AddChannels(
            provider => ConnectionStringOf(provider, "Channels"),
            opcoes => builder.Configuration.GetSection("Channels:Secrets").Bind(opcoes));

        // O Conversations pergunta ao Channels, por uma porta própria, se um número pode atender uma unidade. A matriz de
        // dependências não liga os dois módulos: a ligação é feita aqui, no host. Registrada ANTES do AddConversations, que
        // só instala a implementação que nega tudo se não houver outra.
        builder.Services.AddScoped<IChannelUnitAuthorization, ChannelsBackedUnitAuthorization>();
        builder.Services.AddConversations(provider => ConnectionStringOf(provider, "Conversations"));

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

        // Limite de taxa do login por origem (IP), antes de qualquer consulta ao banco. Vale para quem insiste em tenant ou
        // e-mail inexistente, que não têm contagem própria. Atrás de proxy a origem é a do proxy: confiar em cabeçalho de
        // encaminhamento exige configuração explícita de proxies confiáveis, que não existe ainda.
        var limiteDeLogin = builder.Configuration.GetSection("Identity:LoginRateLimit").Get<LoginRateLimitOptions>() ?? new LoginRateLimitOptions();
        builder.Services.AddRateLimiter(opcoes =>
        {
            opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

        var app = builder.Build();

        app.UseRateLimiter();
        app.UseMiddleware<RequestContextMiddleware>();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapIdentityEndpoints();
        app.MapTenancyEndpoints();
        app.MapAuditEndpoints();
        app.MapConversationsEndpoints();
        app.MapChannelsEndpoints();

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
        return configuracao.GetConnectionString(module)
            ?? configuracao.GetConnectionString("Default")
            ?? configuracao.GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException($"A string de conexão 'ConnectionStrings:{module}' (ou 'Default') não está configurada.");
    }
}
