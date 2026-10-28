using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Persistence;

/// <summary>
/// Cada módulo tem esquema, DbContext e migrations próprios (BOUNDARIES.md): as migrations estão aplicadas, o modelo
/// não tem mudança pendente e toda tabela de tenant exige <c>tenant_id</c> com a chave começando por ele (TEN-3).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ModuleSchemasTests(PostgresFixture banco)
{
    public static TheoryData<string> Modulos => new() { "tenancy", "identity", "audit", "conversations", "channels", "contact_center" };

    private static DbContext ContextoDo(IServiceProvider provider, string modulo) => modulo switch
    {
        "tenancy" => provider.GetRequiredService<TenancyDbContext>(),
        "identity" => provider.GetRequiredService<IdentityDbContext>(),
        "audit" => provider.GetRequiredService<AuditDbContext>(),
        "conversations" => provider.GetRequiredService<ConversationsDbContext>(),
        "channels" => provider.GetRequiredService<ChannelsDbContext>(),
        "contact_center" => provider.GetRequiredService<ContactCenterDbContext>(),
        _ => throw new ArgumentOutOfRangeException(nameof(modulo)),
    };

    [Theory]
    [MemberData(nameof(Modulos))]
    public async Task Todas_as_migrations_do_modulo_estao_aplicadas(string modulo)
    {
        await using var escopo = banco.NovoEscopo();
        var db = ContextoDo(escopo.ServiceProvider, modulo);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [MemberData(nameof(Modulos))]
    public async Task Modelo_do_modulo_nao_tem_mudancas_pendentes_em_relacao_as_migrations(string modulo)
    {
        await using var escopo = banco.NovoEscopo();
        var db = ContextoDo(escopo.ServiceProvider, modulo);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    public static TheoryData<string, string> TabelasDeTenant => new()
    {
        { "tenancy", "organizations" },
        { "tenancy", "business_units" },
        { "tenancy", "departments" },
        { "identity", "users" },
        { "identity", "user_organization_grants" },
        { "identity", "sessions" },
        { "identity", "login_throttles" },
        { "audit", "audit_events" },
        { "conversations", "contacts" },
        { "conversations", "service_sessions" },
        { "conversations", "conversations" },
        { "conversations", "messages" },
        { "conversations", "handling_transitions" },
        { "conversations", "message_translations" },
        { "conversations", "session_transfers" },
        { "channels", "channel_accounts" },
        { "channels", "channel_instances" },
        { "channels", "channel_instance_units" },
        { "channels", "channel_routes" },
        { "channels", "channel_secrets" },
        { "contact_center", "service_queues" },
        { "contact_center", "queue_memberships" },
    };

    [Theory]
    [MemberData(nameof(TabelasDeTenant))]
    public async Task Tabela_de_tenant_exige_tenant_id_e_a_chave_comeca_por_ele(string esquema, string tabela)
    {
        await using var escopo = banco.NovoEscopo();
        var db = ContextoDo(escopo.ServiceProvider, esquema);

        var anulavel = await db.Database
            .SqlQueryRaw<string>(
                "select is_nullable as \"Value\" from information_schema.columns " +
                "where table_schema = {0} and table_name = {1} and column_name = 'tenant_id'",
                esquema, tabela)
            .SingleAsync();
        var colunasDaChave = await db.Database
            .SqlQueryRaw<string>(
                "select kcu.column_name as \"Value\" from information_schema.table_constraints tc " +
                "join information_schema.key_column_usage kcu on kcu.constraint_name = tc.constraint_name and kcu.constraint_schema = tc.constraint_schema " +
                "where tc.table_schema = {0} and tc.table_name = {1} and tc.constraint_type = 'PRIMARY KEY' " +
                "order by kcu.ordinal_position",
                esquema, tabela)
            .ToListAsync();

        Assert.Equal("NO", anulavel);
        Assert.Equal("tenant_id", colunasDaChave.First());
        Assert.True(colunasDaChave.Count >= 2, "A chave precisa ser composta: tenant_id e ao menos uma coluna do recurso.");
    }

    [Theory]
    [MemberData(nameof(Modulos))]
    public async Task Esquema_do_modulo_tem_o_proprio_historico_de_migrations(string modulo)
    {
        await using var escopo = banco.NovoEscopo();
        var db = ContextoDo(escopo.ServiceProvider, modulo);

        var historico = await db.Database
            .SqlQueryRaw<string>(
                "select table_name as \"Value\" from information_schema.tables where table_schema = {0} and table_name = '__ef_migrations_history'",
                modulo)
            .ToListAsync();

        Assert.Single(historico);
    }

    [Fact]
    public async Task Nenhuma_chave_estrangeira_atravessa_esquemas_de_modulos()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var entreEsquemas = await db.Database
            .SqlQueryRaw<int>(
                "select count(*)::int as \"Value\" from information_schema.referential_constraints rc " +
                "join information_schema.table_constraints fk on fk.constraint_name = rc.constraint_name and fk.constraint_schema = rc.constraint_schema " +
                "join information_schema.table_constraints pk on pk.constraint_name = rc.unique_constraint_name and pk.constraint_schema = rc.unique_constraint_schema " +
                "where fk.table_schema in ('tenancy', 'identity', 'audit', 'conversations', 'channels', 'contact_center') and fk.table_schema <> pk.table_schema")
            .SingleAsync();

        Assert.Equal(0, entreEsquemas);
    }

    [Fact]
    public async Task A_trilha_de_auditoria_tem_os_gatilhos_que_a_tornam_append_only()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<AuditDbContext>();

        var gatilhos = await db.Database
            .SqlQueryRaw<string>(
                "select tgname as \"Value\" from pg_trigger t join pg_class c on c.oid = t.tgrelid join pg_namespace n on n.oid = c.relnamespace " +
                "where n.nspname = 'audit' and c.relname = 'audit_events' and not t.tgisinternal")
            .ToListAsync();

        Assert.Contains("trg_audit_events_append_only", gatilhos);
        Assert.Contains("trg_audit_events_no_truncate", gatilhos);
    }
}
