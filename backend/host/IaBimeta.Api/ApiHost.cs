using IaBimeta.Api.Development;
using IaBimeta.Api.RequestContexts;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Infrastructure;
using IaBimeta.Tenancy.Presentation;

namespace IaBimeta.Api;

public static class ApiHost
{
    /// <summary>
    /// Monta o host. Está separado do <c>Program</c> para que os testes de integração subam a mesma composição
    /// (e a mesma segurança) da aplicação real. O <paramref name="configure"/> permite sobrescrever configuração
    /// (por exemplo, a string de conexão) antes da composição.
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

        builder.Services.AddTenancy(provider =>
            provider.GetRequiredService<IConfiguration>().GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException("A string de conexão 'ConnectionStrings:Tenancy' não está configurada."));

        // Fontes de contexto confiáveis. Sem Identity ainda (etapa 2a), não há nenhuma fonte de produção:
        // em produção toda operação que exige tenant é rejeitada. A única fonte existente é a de desenvolvimento,
        // só registrada no ambiente Development e com habilitação explícita.
        if (desenvolvimento.HeaderEnabled)
        {
            builder.Services.AddScoped<ITenantContextSource, DevelopmentTenantHeaderSource>();
        }

        if (desenvolvimento.ApplyMigrations || desenvolvimento.SeedTenants.Count > 0)
        {
            builder.Services.AddSingleton(desenvolvimento);
            builder.Services.AddHostedService<DevelopmentTenancySetup>();
        }

        var app = builder.Build();

        app.UseMiddleware<RequestContextMiddleware>();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapTenancyEndpoints();

        return app;
    }
}
