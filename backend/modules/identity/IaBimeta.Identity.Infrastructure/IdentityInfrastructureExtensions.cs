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
        Action<IdentityOptions>? configure = null,
        Action<IdentitySecretOptions>? configureSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(TimeProvider.System);

        var opcoes = new IdentityOptions();
        configure?.Invoke(opcoes);
        opcoes.ValidateEmbedded();
        services.AddSingleton(opcoes);
        services.AddSingleton<PasswordHasher>();

        // As chaves mestras dos segredos de integração do Embedded (Identity:Secrets). Sem chave ativa, criar chave de integração falha fechado; o resto do Identity segue funcionando.
        var segredos = new IdentitySecretOptions();
        configureSecrets?.Invoke(segredos);
        services.AddSingleton<IIntegrationSecretProtector>(new AesGcmIntegrationSecretProtector(segredos));

        services.AddDbContext<IdentityDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema)));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IGrantRepository, GrantRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IUserAdminStore, UserAdminStore>();
        services.AddScoped<UserAdmissionGuard>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<TenantUserPolicyService>();
        services.AddScoped<IAuthenticationStore, AuthenticationStore>();
        services.AddScoped<IEmbeddedStore, EmbeddedStore>();
        services.AddScoped<IIdentityAuditOutbox, IdentityAuditOutbox>();

        services.AddScoped<AuthenticationService>();
        services.AddScoped<PasswordService>();
        services.AddScoped<SessionAuthenticator>();
        services.AddScoped<UserService>();
        services.AddScoped<UserDirectory>();
        services.AddScoped<IUserDirectory>(provider => provider.GetRequiredService<UserDirectory>());

        services.AddScoped<IntegrationAdminService>();
        services.AddSingleton<IntegrationRateLimiter>();
        services.AddScoped<IntegrationRequestAuthenticator>();
        services.AddScoped<IaBimeta.Identity.Contracts.Integration.IIntegrationRequestAuthenticator, IntegrationRequestAuthenticatorPort>();
        services.AddScoped<EmbeddedTicketService>();
        services.AddScoped<EmbeddedExchangeService>();
        services.AddScoped<EmbeddedContextQuery>();
        services.AddScoped<IEmbeddedContextQuery>(provider => provider.GetRequiredService<EmbeddedContextQuery>());

        return services;
    }
}
