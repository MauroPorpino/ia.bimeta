using IaBimeta.Api.Development;
using IaBimeta.Api.RequestContexts;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Audit.Presentation;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Identity.Presentation;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Infrastructure;
using IaBimeta.Tenancy.Presentation;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Api;

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

        var app = builder.Build();

        app.UseMiddleware<RequestContextMiddleware>();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapIdentityEndpoints();
        app.MapTenancyEndpoints();
        app.MapAuditEndpoints();

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
