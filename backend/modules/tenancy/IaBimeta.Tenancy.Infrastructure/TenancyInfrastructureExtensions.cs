using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Tenancy.Infrastructure;

public static class TenancyInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Tenancy (casos de uso, repositórios e DbContext). O host precisa registrar antes o
    /// <c>IRequestContextAccessor</c> e o módulo Audit (<c>IAuditWriter</c>). A string de conexão é lida só quando o DbContext é usado, para que um
    /// host sem banco configurado continue subindo (por exemplo, para o /health).
    /// </summary>
    public static IServiceCollection AddTenancy(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<TenancyDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TenancyDbContext.Schema)));

        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<TenantRegistrationService>();
        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<TenantSettingsService>();
        services.AddScoped<ITenantSettings>(provider => provider.GetRequiredService<TenantSettingsService>());
        services.AddScoped<OrganizationService>();
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();

        services.AddScoped<IBusinessUnitDirectory, BusinessUnitDirectory>();
        services.AddScoped<IDepartmentDirectory, DepartmentDirectory>();
        services.AddScoped<IBusinessUnitRepository, BusinessUnitRepository>();
        services.AddScoped<IDepartmentRepository, DepartmentRepository>();
        services.AddScoped<BusinessUnitService>();
        services.AddScoped<DepartmentService>();

        return services;
    }
}
