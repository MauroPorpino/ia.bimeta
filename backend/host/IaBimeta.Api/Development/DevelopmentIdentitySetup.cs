using IaBimeta.Audit.Infrastructure;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Development;

/// <summary>
/// SOMENTE DESENVOLVIMENTO. Roda depois da preparação do Tenancy: aplica as migrations pendentes do Identity e do
/// Audit e cria o administrador de cada tenant de demonstração que ainda não tem. O administrador é criado por um
/// contexto de sistema do próprio host (código confiável), com concessão de escopo do tenant. A senha vem da
/// configuração de desenvolvimento, é sintética e nunca é registrada em log. Só é registrado quando a configuração de
/// desenvolvimento está ligada. Uma falha é registrada em log e não derruba a API.
/// </summary>
internal sealed class DevelopmentIdentitySetup : IHostedService
{
    private const string SeedOrigin = "development-seed";

    private readonly IServiceProvider _servicos;
    private readonly DevelopmentTenancyOptions _opcoes;
    private readonly ILogger<DevelopmentIdentitySetup> _log;

    public DevelopmentIdentitySetup(IServiceProvider servicos, DevelopmentTenancyOptions opcoes, ILogger<DevelopmentIdentitySetup> log)
    {
        _servicos = servicos;
        _opcoes = opcoes;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_opcoes.ApplyMigrations)
            {
                await using var escopoDasMigrations = _servicos.CreateAsyncScope();
                await escopoDasMigrations.ServiceProvider.GetRequiredService<AuditDbContext>().Database.MigrateAsync(cancellationToken);
                await escopoDasMigrations.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync(cancellationToken);
                _log.LogInformation("Migrations do Audit e do Identity aplicadas.");
            }

            foreach (var semente in _opcoes.SeedAdministrators)
            {
                await using var escopo = _servicos.CreateAsyncScope();

                var tenant = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(semente.TenantSlug, cancellationToken);
                if (tenant is null)
                {
                    _log.LogWarning("Administrador de demonstração ignorado: o tenant '{Slug}' não existe.", semente.TenantSlug);
                    continue;
                }

                escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(tenant.TenantId, SeedOrigin));

                var criado = await escopo.ServiceProvider.GetRequiredService<UserService>()
                    .EnsureTenantAdministratorAsync(semente.Email, semente.DisplayName, semente.Password, cancellationToken);
                if (criado)
                {
                    _log.LogInformation("Administrador de demonstração do tenant '{Slug}' criado.", semente.TenantSlug);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A preparação de desenvolvimento do Identity falhou (o banco está configurado e acessível?). A API continua no ar.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
