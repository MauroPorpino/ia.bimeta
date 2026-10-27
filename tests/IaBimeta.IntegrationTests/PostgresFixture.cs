using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Compartilha o PostgreSQL de teste entre as classes de teste: aplica as migrations uma vez e entrega "escopos",
/// cada um equivalente a uma requisição (com ou sem contexto de tenant), usando a mesma composição de DI do host.
/// Cada teste cria os próprios tenants com slug aleatório, então execuções repetidas não interferem entre si e
/// nenhum dado é apagado.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private ServiceProvider? _servicos;

    public string ConnectionString { get; } = TestDatabase.ConnectionString;

    public async Task InitializeAsync()
    {
        var colecao = new ServiceCollection();
        colecao.AddScoped<RequestContextHolder>();
        colecao.AddScoped<IRequestContextAccessor>(provider => provider.GetRequiredService<RequestContextHolder>());
        colecao.AddTenancy(_ => ConnectionString);
        _servicos = colecao.BuildServiceProvider(validateScopes: true);

        await using var escopo = _servicos.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => _servicos is null ? Task.CompletedTask : _servicos.DisposeAsync().AsTask();

    /// <summary>Equivale a uma requisição. Sem <paramref name="contexto"/>, nenhum tenant foi estabelecido.</summary>
    public AsyncServiceScope NovoEscopo(RequestContext? contexto = null)
    {
        var escopo = _servicos!.CreateAsyncScope();
        if (contexto is not null)
        {
            escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(contexto);
        }

        return escopo;
    }

    public static RequestContext Contexto(TenantInfo tenant) => new(tenant.TenantId, "teste-integracao");

    public async Task<TenantInfo> RegistrarTenantAsync(string prefixo)
    {
        await using var escopo = NovoEscopo();
        var slug = $"{prefixo}-{Guid.NewGuid().ToString("N")[..8]}";
        return await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>()
            .RegisterAsync(slug, $"Tenant de teste {slug}");
    }
}
