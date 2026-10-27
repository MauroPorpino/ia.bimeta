using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class MigrationsTests(PostgresFixture banco)
{
    [Fact]
    public async Task Todas_as_migrations_estao_aplicadas()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Modelo_atual_nao_tem_mudancas_pendentes_em_relacao_as_migrations()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Esquema_do_modulo_tem_as_tabelas_e_o_historico_de_migrations()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var tabelas = await db.Database
            .SqlQueryRaw<string>("select table_name as \"Value\" from information_schema.tables where table_schema = 'tenancy'")
            .ToListAsync();

        Assert.Contains("tenants", tabelas);
        Assert.Contains("organizations", tabelas);
        Assert.Contains("__ef_migrations_history", tabelas);
    }

    [Fact]
    public async Task Tabela_do_tenant_exige_tenant_id_e_a_chave_comeca_por_ele()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var anulavel = await db.Database
            .SqlQueryRaw<string>(
                "select is_nullable as \"Value\" from information_schema.columns " +
                "where table_schema = 'tenancy' and table_name = 'organizations' and column_name = 'tenant_id'")
            .SingleAsync();
        var colunasDaChave = await db.Database
            .SqlQueryRaw<string>(
                "select kcu.column_name as \"Value\" from information_schema.table_constraints tc " +
                "join information_schema.key_column_usage kcu on kcu.constraint_name = tc.constraint_name and kcu.constraint_schema = tc.constraint_schema " +
                "where tc.table_schema = 'tenancy' and tc.table_name = 'organizations' and tc.constraint_type = 'PRIMARY KEY' " +
                "order by kcu.ordinal_position")
            .ToListAsync();

        Assert.Equal("NO", anulavel);
        Assert.Equal(new[] { "tenant_id", "id" }, colunasDaChave);
    }
}
