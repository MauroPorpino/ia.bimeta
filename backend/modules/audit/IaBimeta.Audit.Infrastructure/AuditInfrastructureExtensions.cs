using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Audit.Infrastructure;

public static class AuditInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Audit (escritor, consulta, repositório e DbContext). O host precisa registrar antes o
    /// <c>IRequestContextAccessor</c>. A string de conexão é lida só quando o DbContext é usado.
    /// </summary>
    public static IServiceCollection AddAudit(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<AuditDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AuditDbContext.Schema)));

        services.AddScoped<IAuditRepository, AuditRepository>();
        // TryAdd: quem registrou um escritor antes (os testes de falha de auditoria) continua valendo.
        services.TryAddScoped<IAuditWriter, AuditWriter>();
        // A entrega, com a autoria e o instante originais, do que um outbox de módulo guardou (capacidade à parte do escritor comum).
        services.TryAddScoped<IAuditRecoveryWriter, AuditWriter>();
        services.AddScoped<AuditQueryService>();

        return services;
    }
}
