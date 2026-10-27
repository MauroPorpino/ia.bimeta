using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class TenantRegistryTests(PostgresFixture banco)
{
    [Fact]
    public async Task Tenant_registrado_e_persistido_e_encontrado_por_slug_e_por_id()
    {
        var registrado = await banco.RegistrarTenantAsync("registro");

        await using var escopo = banco.NovoEscopo();
        var diretorio = escopo.ServiceProvider.GetRequiredService<ITenantDirectory>();

        var porSlug = await diretorio.FindBySlugAsync(registrado.Slug);
        var porId = await diretorio.FindByIdAsync(registrado.TenantId);

        Assert.NotNull(porSlug);
        Assert.NotNull(porId);
        Assert.Equal(registrado.TenantId, porSlug.TenantId);
        Assert.Equal(registrado.Slug, porId.Slug);
        Assert.Equal(TenantStatus.Active, porSlug.Status);
    }

    [Fact]
    public async Task Tenant_inexistente_nao_e_encontrado()
    {
        await using var escopo = banco.NovoEscopo();
        var diretorio = escopo.ServiceProvider.GetRequiredService<ITenantDirectory>();

        Assert.Null(await diretorio.FindBySlugAsync("nao-existe-" + Guid.NewGuid().ToString("N")[..8]));
        Assert.Null(await diretorio.FindByIdAsync(new TenantId(Guid.NewGuid())));
    }

    [Fact]
    public async Task Servico_recusa_slug_repetido()
    {
        var registrado = await banco.RegistrarTenantAsync("repetido");

        await using var escopo = banco.NovoEscopo();
        var cadastro = escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>();

        await Assert.ThrowsAsync<DuplicateTenantSlugException>(() => cadastro.RegisterAsync(registrado.Slug, "Outro nome"));
    }

    [Fact]
    public async Task Banco_recusa_slug_repetido_mesmo_sem_a_pre_checagem_do_servico()
    {
        var slug = "corrida-" + Guid.NewGuid().ToString("N")[..8];
        await using var primeiro = banco.NovoEscopo();
        await using var segundo = banco.NovoEscopo();
        var dbPrimeiro = primeiro.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var dbSegundo = segundo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbPrimeiro.Tenants.Add(Tenant.Register(new TenantId(Guid.NewGuid()), slug, "Primeiro", DateTimeOffset.UtcNow));
        dbSegundo.Tenants.Add(Tenant.Register(new TenantId(Guid.NewGuid()), slug, "Segundo", DateTimeOffset.UtcNow));

        await dbPrimeiro.SaveChangesAsync();
        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => dbSegundo.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(falha.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    [Fact]
    public async Task Suspensao_do_tenant_e_persistida_e_a_reativacao_tambem()
    {
        var registrado = await banco.RegistrarTenantAsync("suspensao");

        await using (var escopo = banco.NovoEscopo())
        {
            Assert.True(await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().SuspendAsync(registrado.Slug));
        }

        await using (var escopo = banco.NovoEscopo())
        {
            var suspenso = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(registrado.Slug);
            Assert.Equal(TenantStatus.Suspended, suspenso!.Status);
            Assert.True(await escopo.ServiceProvider.GetRequiredService<TenantRegistrationService>().ReactivateAsync(registrado.Slug));
        }

        await using (var escopo = banco.NovoEscopo())
        {
            var ativo = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(registrado.Slug);
            Assert.Equal(TenantStatus.Active, ativo!.Status);
        }
    }
}
