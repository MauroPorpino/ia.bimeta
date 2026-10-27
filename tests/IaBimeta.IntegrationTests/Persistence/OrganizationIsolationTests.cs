using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Domain;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Isolamento entre tenants com PostgreSQL real: dados persistidos, listagem e busca por tenant, falha fechada sem
/// contexto e bloqueio de acesso cruzado. Cada teste cria os próprios tenants.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrganizationIsolationTests(PostgresFixture banco)
{
    private async Task<OrganizationView> CriarAsync(RequestContext contexto, string nome)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await escopo.ServiceProvider.GetRequiredService<OrganizationService>().CreateAsync(nome);
    }

    [Fact]
    public async Task Dados_de_dois_tenants_ficam_persistidos_cada_um_no_seu_tenant()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        await CriarAsync(PostgresFixture.Contexto(a), "Matriz A");
        await CriarAsync(PostgresFixture.Contexto(a), "Filial A");
        await CriarAsync(PostgresFixture.Contexto(b), "Matriz B");

        // Leitura administrativa, ignorando o filtro, só para conferir o que o banco guardou.
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var doA = await db.Organizations.IgnoreQueryFilters().Where(o => o.TenantId == a.TenantId).Select(o => o.Name).OrderBy(n => n).ToListAsync();
        var doB = await db.Organizations.IgnoreQueryFilters().Where(o => o.TenantId == b.TenantId).Select(o => o.Name).OrderBy(n => n).ToListAsync();

        Assert.Equal(new[] { "Filial A", "Matriz A" }, doA);
        Assert.Equal(new[] { "Matriz B" }, doB);
    }

    [Fact]
    public async Task Cada_tenant_so_lista_as_proprias_organizacoes()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        await CriarAsync(PostgresFixture.Contexto(a), "Matriz A");
        await CriarAsync(PostgresFixture.Contexto(b), "Matriz B");

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a));
        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b));
        var listaA = await escopoA.ServiceProvider.GetRequiredService<OrganizationService>().ListAsync();
        var listaB = await escopoB.ServiceProvider.GetRequiredService<OrganizationService>().ListAsync();

        Assert.Equal(new[] { "Matriz A" }, listaA.Select(o => o.Name));
        Assert.Equal(new[] { "Matriz B" }, listaB.Select(o => o.Name));
    }

    [Fact]
    public async Task Mesmo_nome_vale_em_tenants_diferentes_mas_nao_dentro_do_mesmo_tenant()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");

        await CriarAsync(PostgresFixture.Contexto(a), "Matriz");
        await CriarAsync(PostgresFixture.Contexto(b), "Matriz");

        await Assert.ThrowsAsync<DuplicateOrganizationNameException>(() => CriarAsync(PostgresFixture.Contexto(a), "Matriz"));
    }

    [Fact]
    public async Task Organizacao_de_outro_tenant_responde_nao_encontrada()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var deA = await CriarAsync(PostgresFixture.Contexto(a), "Reservada de A");

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a));
        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b));

        Assert.NotNull(await escopoA.ServiceProvider.GetRequiredService<OrganizationService>().GetAsync(deA.Id));
        Assert.Null(await escopoB.ServiceProvider.GetRequiredService<OrganizationService>().GetAsync(deA.Id));
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_os_casos_de_uso_falham_fechados()
    {
        await using var escopo = banco.NovoEscopo();
        var servico = escopo.ServiceProvider.GetRequiredService<OrganizationService>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.CreateAsync("Sem tenant"));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => servico.ListAsync());
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_a_consulta_direta_ao_DbContext_falha_fechada()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Organizations.ToListAsync());
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.Organizations.CountAsync());
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_a_gravacao_direta_falha_fechada()
    {
        var a = await banco.RegistrarTenantAsync("a");
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.Organizations.Add(Organization.Create(a.TenantId, "Sem contexto", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Tentativa_de_gravar_organizacao_em_nome_de_outro_tenant_e_bloqueada()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b));
        var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbB.Organizations.Add(Organization.Create(a.TenantId, "Intrusa", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a));
        Assert.Empty(await escopoA.ServiceProvider.GetRequiredService<OrganizationService>().ListAsync());
    }

    [Fact]
    public async Task Tentativa_de_excluir_organizacao_de_outro_tenant_e_bloqueada()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var deA = await CriarAsync(PostgresFixture.Contexto(a), "Protegida");

        await using (var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b)))
        {
            var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();
            // O invasor contorna o filtro de leitura de propósito e tenta excluir.
            var alvo = await dbB.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == deA.Id);
            dbB.Organizations.Remove(alvo);

            await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
        }

        await using var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a));
        Assert.NotNull(await escopoA.ServiceProvider.GetRequiredService<OrganizationService>().GetAsync(deA.Id));
    }

    [Fact]
    public async Task Tentativa_de_alterar_organizacao_de_outro_tenant_e_bloqueada()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var deA = await CriarAsync(PostgresFixture.Contexto(a), "Intocavel");

        await using var escopoB = banco.NovoEscopo(PostgresFixture.Contexto(b));
        var dbB = escopoB.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var alvo = await dbB.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == deA.Id);
        dbB.Entry(alvo).State = EntityState.Modified;

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Tentativa_de_trocar_o_tenant_de_uma_organizacao_propria_e_recusada()
    {
        var a = await banco.RegistrarTenantAsync("a");
        var b = await banco.RegistrarTenantAsync("b");
        var propria = await CriarAsync(PostgresFixture.Contexto(a), "Propria");

        await using (var escopoA = banco.NovoEscopo(PostgresFixture.Contexto(a)))
        {
            var dbA = escopoA.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var carregada = await dbA.Organizations.SingleAsync(o => o.Id == propria.Id);

            // O tenant faz parte da chave (TEN-3): o próprio EF Core recusa alterá-lo.
            Assert.Throws<InvalidOperationException>(() => dbA.Entry(carregada).Property(o => o.TenantId).CurrentValue = b.TenantId);
        }

        await using var escopoDeA = banco.NovoEscopo(PostgresFixture.Contexto(a));
        await using var escopoDeB = banco.NovoEscopo(PostgresFixture.Contexto(b));
        Assert.NotNull(await escopoDeA.ServiceProvider.GetRequiredService<OrganizationService>().GetAsync(propria.Id));
        Assert.Null(await escopoDeB.ServiceProvider.GetRequiredService<OrganizationService>().GetAsync(propria.Id));
    }

    [Fact]
    public async Task Banco_recusa_organizacao_de_tenant_que_nao_esta_registrado()
    {
        var fantasma = new RequestContext(new TenantId(Guid.NewGuid()), "teste-integracao");

        var falha = await Assert.ThrowsAsync<DbUpdateException>(() => CriarAsync(fantasma, "Orfa"));

        var postgres = Assert.IsType<PostgresException>(falha.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
    }
}
