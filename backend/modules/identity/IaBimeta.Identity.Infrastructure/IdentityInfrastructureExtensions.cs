using IaBimeta.Identity.Application;
using IaBimeta.Identity.Contracts.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Identity.Infrastructure;

public static class IdentityInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Identity (casos de uso, repositórios e DbContext). O host precisa registrar antes o
    /// <c>IRequestContextAccessor</c>, o Tenancy (<c>ITenantDirectory</c> e <c>IOrganizationDirectory</c>) e o Audit
    /// (<c>IAuditWriter</c>). A string de conexão é lida só quando o DbContext é usado.
    /// </summary>
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<IdentityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        var opcoes = new IdentityOptions();
        configure?.Invoke(opcoes);
        services.AddSingleton(opcoes);
        services.AddSingleton<PasswordHasher>();

        services.AddDbContext<IdentityDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema)));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IGrantRepository, GrantRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IAuthenticationStore, AuthenticationStore>();

        services.AddScoped<AuthenticationService>();
        services.AddScoped<SessionAuthenticator>();
        services.AddScoped<UserService>();
        services.AddScoped<UserDirectory>();
        services.AddScoped<IUserDirectory>(provider => provider.GetRequiredService<UserDirectory>());

        return services;
    }
}
