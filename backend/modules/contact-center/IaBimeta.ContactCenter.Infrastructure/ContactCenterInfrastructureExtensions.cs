using IaBimeta.ContactCenter.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.ContactCenter.Infrastructure;

public static class ContactCenterInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo ContactCenter (filas de atendimento, atendentes das filas e persistência). O host precisa registrar antes
    /// o <c>IRequestContextAccessor</c>, o Tenancy (<c>IOrganizationDirectory</c>, <c>IBusinessUnitDirectory</c> e
    /// <c>IDepartmentDirectory</c>), o Identity (<c>IUserDirectory</c>) e o Audit (<c>IAuditWriter</c>). A string de conexão é
    /// lida só quando o DbContext é usado. O roteamento, a atribuição e o handoff IA/humano NÃO existem aqui ainda.
    /// </summary>
    public static IServiceCollection AddContactCenter(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<ContactCenterDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ContactCenterDbContext.Schema)));

        services.AddScoped<IQueueRepository, QueueRepository>();
        services.AddScoped<ServiceQueueService>();

        return services;
    }
}
