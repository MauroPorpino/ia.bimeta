using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Channels.Infrastructure;

public static class ChannelsInfrastructureExtensions
{
    /// <summary>
    /// Registra o módulo Channels (cadastro de contas, números, unidades autorizadas e credenciais, rota de entrada e
    /// persistência). O host precisa registrar antes o <c>IRequestContextAccessor</c>, o Tenancy (<c>IOrganizationDirectory</c>,
    /// <c>IBusinessUnitDirectory</c> e <c>IDepartmentDirectory</c>) e o Audit (<c>IAuditWriter</c>). A string de conexão é lida só
    /// quando o DbContext é usado. Sem chave de cifra configurada, o módulo sobe e o cadastro funciona, mas nenhuma credencial é
    /// gravada (falha fechada). O Receiver, o Sender e a integração com a Meta NÃO existem aqui: dependem da confirmação dos
    /// parâmetros na documentação oficial (BOUNDARIES.md PRV-2).
    /// </summary>
    public static IServiceCollection AddChannels(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        Action<ChannelSecretOptions>? configureSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var segredos = new ChannelSecretOptions();
        configureSecrets?.Invoke(segredos);
        services.AddSingleton(segredos);
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<ChannelsDbContext>((provider, options) =>
            options.UseNpgsql(
                connectionString(provider),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ChannelsDbContext.Schema)));

        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<ChannelAccountService>();
        services.AddScoped<ChannelInstanceService>();
        services.AddScoped<ChannelRouteResolver>();
        services.AddScoped<ChannelRoutingQuery>();
        services.AddScoped<IChannelRouting>(provider => provider.GetRequiredService<ChannelRoutingQuery>());

        return services;
    }
}
