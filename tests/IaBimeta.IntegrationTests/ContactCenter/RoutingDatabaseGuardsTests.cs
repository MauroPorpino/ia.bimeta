using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// As garantias do próprio banco para a política de roteamento, as regras, o horário e a presença, independentes do aplicativo
/// (TEN-3, DAD-2): limites e valores válidos, contingência obrigatória quando usada, chaves compostas que carregam o tenant e a
/// ausência de chave para outro esquema.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoutingDatabaseGuardsTests(PostgresFixture banco)
{
    private static string Texto(Guid? valor) => valor is null ? "null" : $"'{valor}'";

    private static string InserirFila(Guid tenant, Guid id, Guid organizacao, string nome) =>
        "insert into contact_center.service_queues (tenant_id, id, organization_id, business_unit_id, department_id, name, name_key, status, version, created_at, updated_at) " +
        $"values ('{tenant}', '{id}', '{organizacao}', null, null, '{nome}', '{nome.ToLowerInvariant()}', 1, 1, now(), now())";

    private static string InserirRegra(Guid tenant, Guid fila, Guid organizacao, int prioridade = 0, Guid? unidade = null, Guid? setor = null) =>
        "insert into contact_center.routing_rules (tenant_id, id, organization_id, name, channel_instance_id, business_unit_id, department_id, queue_id, priority, sort_order, enabled, version, created_at, updated_at) " +
        $"values ('{tenant}', '{Guid.NewGuid()}', '{organizacao}', 'Regra', null, {Texto(unidade)}, {Texto(setor)}, '{fila}', {prioridade}, 0, true, 1, now(), now())";

    private static string InserirFaixa(Guid tenant, Guid fila, int dia, int inicio, int fim) =>
        $"insert into contact_center.queue_schedule_windows (tenant_id, queue_id, day_of_week, start_minute, end_minute) values ('{tenant}', '{fila}', {dia}, {inicio}, {fim})";

    private static Task<int> Executar(CenarioDeConversas c, string sql) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database.ExecuteSqlRawAsync(sql));

    private static async Task<PostgresException> FalhaDoBancoAsync(Task<int> operacao)
    {
        var falha = await Assert.ThrowsAnyAsync<Exception>(() => operacao);
        var atual = falha;
        while (atual is not null and not PostgresException)
        {
            atual = atual.InnerException;
        }

        return Assert.IsType<PostgresException>(atual);
    }

    private async Task<(CenarioDeConversas C, Guid Tenant, Guid Fila)> CenaAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var tenant = c.Tenant.TenantId.Value;
        var fila = Guid.NewGuid();
        await Executar(c, InserirFila(tenant, fila, c.OrganizacaoId, "Fila"));
        return (c, tenant, fila);
    }

    [Theory]
    [InlineData("out_of_hours = 4", "ck_service_queues_out_of_hours")]
    [InlineData("no_agent = 3", "ck_service_queues_no_agent")]
    [InlineData("default_priority = 10", "ck_service_queues_default_priority")]
    [InlineData("default_priority = -1", "ck_service_queues_default_priority")]
    [InlineData("max_items_per_agent = 0", "ck_service_queues_max_items")]
    [InlineData("out_of_hours = 3", "ck_service_queues_fallback")]          // contingência sem fila de contingência
    [InlineData("no_agent = 2", "ck_service_queues_fallback")]
    public async Task O_banco_recusa_politica_de_fila_invalida(string alteracao, string restricao)
    {
        var (c, tenant, fila) = await CenaAsync("dbpolitica");

        var falha = await FalhaDoBancoAsync(Executar(c, $"update contact_center.service_queues set {alteracao} where tenant_id = '{tenant}' and id = '{fila}'"));

        Assert.Equal((PostgresErrorCodes.CheckViolation, restricao), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task O_banco_recusa_contingencia_da_propria_fila_ou_de_outro_tenant_e_aceita_a_de_outra_fila_do_mesmo_tenant()
    {
        var (c, tenant, fila) = await CenaAsync("dbcontingencia");
        var (_, _, filaDoOutro) = await CenaAsync("dbcontingenciab");
        var outra = Guid.NewGuid();
        await Executar(c, InserirFila(tenant, outra, c.OrganizacaoId, "Outra"));

        var propria = await FalhaDoBancoAsync(Executar(c, $"update contact_center.service_queues set fallback_queue_id = '{fila}' where tenant_id = '{tenant}' and id = '{fila}'"));
        var deOutroTenant = await FalhaDoBancoAsync(Executar(c, $"update contact_center.service_queues set fallback_queue_id = '{filaDoOutro}' where tenant_id = '{tenant}' and id = '{fila}'"));

        Assert.Equal("ck_service_queues_fallback_self", propria.ConstraintName);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deOutroTenant.SqlState);
        Assert.Equal(1, await Executar(c, $"update contact_center.service_queues set fallback_queue_id = '{outra}', out_of_hours = 3 where tenant_id = '{tenant}' and id = '{fila}'"));
    }

    [Theory]
    [InlineData(-1, "ck_routing_rules_priority")]
    [InlineData(10, "ck_routing_rules_priority")]
    public async Task O_banco_recusa_regra_com_prioridade_fora_da_faixa(int prioridade, string restricao)
    {
        var (c, tenant, fila) = await CenaAsync("dbregraprioridade");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirRegra(tenant, fila, c.OrganizacaoId, prioridade)));

        Assert.Equal((PostgresErrorCodes.CheckViolation, restricao), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task O_banco_recusa_regra_com_setor_sem_unidade_e_apontando_para_fila_de_outro_tenant()
    {
        var (c, tenant, fila) = await CenaAsync("dbregraestrutura");
        var (_, _, filaDoOutro) = await CenaAsync("dbregraestruturab");

        Assert.Equal(1, await Executar(c, InserirRegra(tenant, fila, c.OrganizacaoId, 3, Guid.NewGuid(), Guid.NewGuid())));
        var semUnidade = await FalhaDoBancoAsync(Executar(c, InserirRegra(tenant, fila, c.OrganizacaoId, setor: Guid.NewGuid())));
        var deOutroTenant = await FalhaDoBancoAsync(Executar(c, InserirRegra(tenant, filaDoOutro, c.OrganizacaoId)));

        Assert.Equal("ck_routing_rules_department", semUnidade.ConstraintName);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deOutroTenant.SqlState);
    }

    [Theory]
    [InlineData(7, 0, 60, "ck_queue_schedule_windows_day")]
    [InlineData(1, -1, 60, "ck_queue_schedule_windows_minutes")]
    [InlineData(1, 60, 60, "ck_queue_schedule_windows_minutes")]
    [InlineData(1, 120, 60, "ck_queue_schedule_windows_minutes")]
    [InlineData(1, 0, 1441, "ck_queue_schedule_windows_minutes")]
    public async Task O_banco_recusa_faixa_de_horario_invalida(int dia, int inicio, int fim, string restricao)
    {
        var (c, tenant, fila) = await CenaAsync("dbfaixa");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirFaixa(tenant, fila, dia, inicio, fim)));

        Assert.Equal((PostgresErrorCodes.CheckViolation, restricao), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task O_banco_aceita_o_fim_do_dia_recusa_faixa_repetida_e_faixa_ou_dia_fechado_de_fila_de_outro_tenant()
    {
        var (c, tenant, fila) = await CenaAsync("dbfaixachave");
        var (_, _, filaDoOutro) = await CenaAsync("dbfaixachaveb");

        Assert.Equal(1, await Executar(c, InserirFaixa(tenant, fila, 1, 0, 1440)));
        var repetida = await FalhaDoBancoAsync(Executar(c, InserirFaixa(tenant, fila, 1, 0, 600)));
        var deOutroTenant = await FalhaDoBancoAsync(Executar(c, InserirFaixa(tenant, filaDoOutro, 1, 0, 60)));
        Assert.Equal(1, await Executar(c, $"insert into contact_center.queue_closures (tenant_id, queue_id, closed_on) values ('{tenant}', '{fila}', '2026-12-25')"));
        var diaRepetido = await FalhaDoBancoAsync(Executar(c, $"insert into contact_center.queue_closures (tenant_id, queue_id, closed_on) values ('{tenant}', '{fila}', '2026-12-25')"));
        var diaDeOutroTenant = await FalhaDoBancoAsync(Executar(c, $"insert into contact_center.queue_closures (tenant_id, queue_id, closed_on) values ('{tenant}', '{filaDoOutro}', '2026-12-25')"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, repetida.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deOutroTenant.SqlState);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, diaRepetido.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, diaDeOutroTenant.SqlState);
    }

    [Fact]
    public async Task O_banco_garante_uma_presenca_por_atendente_com_situacao_valida()
    {
        var (c, tenant, _) = await CenaAsync("dbpresenca");
        var usuario = Guid.NewGuid();
        string Inserir(Guid u, int situacao, long versao = 1) =>
            $"insert into contact_center.agent_presences (tenant_id, user_id, status, version, updated_at) values ('{tenant}', '{u}', {situacao}, {versao}, now())";

        Assert.Equal(1, await Executar(c, Inserir(usuario, 1)));
        var repetida = await FalhaDoBancoAsync(Executar(c, Inserir(usuario, 2)));
        var situacaoInvalida = await FalhaDoBancoAsync(Executar(c, Inserir(Guid.NewGuid(), 4)));
        var versaoZero = await FalhaDoBancoAsync(Executar(c, Inserir(Guid.NewGuid(), 1, 0)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, repetida.SqlState);
        Assert.Equal("ck_agent_presences_status", situacaoInvalida.ConstraintName);
        Assert.Equal("ck_agent_presences_version", versaoZero.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_prioridade_do_item_e_do_historico_fora_da_faixa()
    {
        var (c, tenant, fila) = await CenaAsync("dbprioridade");
        var item = Guid.NewGuid();
        string InserirItem(int prioridade) =>
            "insert into contact_center.queue_items (tenant_id, id, session_id, organization_id, queue_id, status, version, entered_at, updated_at, pending_attempts, priority) " +
            $"values ('{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{c.OrganizacaoId}', '{fila}', 1, 1, now(), now(), 0, {prioridade})";

        Assert.Equal(1, await Executar(c, InserirItem(9)));
        var alta = await FalhaDoBancoAsync(Executar(c, InserirItem(10)));
        var baixa = await FalhaDoBancoAsync(Executar(c, InserirItem(-1)));
        await Executar(c,
            "insert into contact_center.queue_items (tenant_id, id, session_id, organization_id, queue_id, status, version, entered_at, updated_at, pending_attempts) " +
            $"values ('{tenant}', '{item}', '{Guid.NewGuid()}', '{c.OrganizacaoId}', '{fila}', 1, 1, now(), now(), 0)");
        var historico = await FalhaDoBancoAsync(Executar(c,
            "insert into contact_center.queue_item_history (tenant_id, id, item_id, session_id, operation_id, kind, to_queue_id, reason, occurred_at, from_priority, to_priority) " +
            $"values ('{tenant}', '{Guid.NewGuid()}', '{item}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 8, '{fila}', 'priority-change', now(), 0, 10)"));

        Assert.Equal("ck_queue_items_priority", alta.ConstraintName);
        Assert.Equal("ck_queue_items_priority", baixa.ConstraintName);
        Assert.Equal("ck_queue_item_history_priority", historico.ConstraintName);
    }

    [Fact]
    public async Task O_banco_garante_a_prioridade_padrao_zero_nas_filas_e_itens_antigos_sem_a_coluna()
    {
        var (c, tenant, fila) = await CenaAsync("dbpadroes");

        var padroes = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Queues.AsNoTracking().Where(q => q.Id == fila).SingleAsync());

        Assert.Equal((null, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, null, 0, null), (padroes.TimeZoneId, padroes.OutOfHours, padroes.NoAgent, padroes.FallbackQueueId, padroes.DefaultPriority, padroes.MaxItemsPerAgent));
        Assert.Equal(tenant, padroes.TenantId.Value);
    }

    [Fact]
    public async Task Nenhuma_chave_estrangeira_das_tabelas_de_roteamento_aponta_para_outro_esquema()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbroteamentoesquemas");

        var esquemas = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database
            .SqlQueryRaw<string>(
                "select distinct pk.table_schema::text as \"Value\" from information_schema.referential_constraints rc " +
                "join information_schema.table_constraints fk on fk.constraint_name = rc.constraint_name and fk.constraint_schema = rc.constraint_schema " +
                "join information_schema.table_constraints pk on pk.constraint_name = rc.unique_constraint_name and pk.constraint_schema = rc.unique_constraint_schema " +
                "where fk.table_schema = 'contact_center' and fk.table_name in ('routing_rules', 'queue_schedule_windows', 'queue_closures', 'service_queues')")
            .ToListAsync());

        Assert.Equal(new[] { "contact_center" }, esquemas); // o conjunto não é vazio e só aponta para o próprio esquema
    }

    [Fact]
    public async Task O_DbContext_recusa_gravar_regra_e_presenca_de_outro_tenant_antes_de_chegar_ao_banco()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbroteamentotrava");
        var outro = await CenarioDeConversas.CriarAsync(banco, "dbroteamentotravab");
        var regraDeOutro = RoutingRule.Create(outro.Tenant.TenantId, outro.OrganizacaoId, "Intrusa", null, null, null, Guid.NewGuid(), 0, 0, DateTimeOffset.UtcNow);
        var presencaDeOutro = AgentPresence.Create(outro.Tenant.TenantId, Guid.NewGuid(), AgentStatus.Available, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            db.RoutingRules.Add(regraDeOutro);
            return await db.SaveChangesAsync();
        }));
        await Assert.ThrowsAsync<CrossTenantAccessException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            db.Presences.Add(presencaDeOutro);
            return await db.SaveChangesAsync();
        }));
    }
}
