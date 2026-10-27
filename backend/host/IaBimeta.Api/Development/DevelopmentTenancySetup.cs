using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Development;

/// <summary>
/// SOMENTE DESENVOLVIMENTO. Na partida, aplica as migrations pendentes e cadastra os tenants de demonstração que
/// ainda não existem. Só é registrado quando a configuração de desenvolvimento está ligada. Uma falha (por exemplo,
/// banco indisponível) é registrada em log e não derruba a API: o /health continua respondendo.
/// </summary>
internal sealed class DevelopmentTenancySetup : IHostedService
{
    private readonly IServiceProvider _servicos;
    private readonly DevelopmentTenancyOptions _opcoes;
    private readonly ILogger<DevelopmentTenancySetup> _log;

    public DevelopmentTenancySetup(IServiceProvider servicos, DevelopmentTenancyOptions opcoes, ILogger<DevelopmentTenancySetup> log)
    {
        _servicos = servicos;
        _opcoes = opcoes;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var escopo = _servicos.CreateAsyncScope();

            if (_opcoes.ApplyMigrations)
            {
                var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                _log.LogInformation("Migrations do Tenancy aplicadas.");
            }

            var diretorio = escopo.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var cadastro = escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>();
            foreach (var semente in _opcoes.SeedTenants)
            {
                if (await diretorio.FindBySlugAsync(semente.Slug, cancellationToken) is null)
                {
                    await cadastro.RegisterAsync(semente.Slug, semente.Name, cancellationToken);
                    _log.LogInformation("Tenant de demonstração '{Slug}' cadastrado.", semente.Slug);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A preparação de desenvolvimento do Tenancy falhou (o banco está configurado e acessível?). A API continua no ar.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
