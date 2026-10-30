using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// As garantias do próprio banco para os itens da fila e o histórico, independentes do aplicativo (TEN-3, DAD-2): no máximo um item
/// ativo por conversa, no máximo um responsável (e só no estado atribuído), operação pendente tudo-ou-nada, chaves compostas que
/// carregam o tenant, e histórico só de acréscimo com uma linha por operação.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueItemDatabaseGuardsTests(PostgresFixture banco)
{
    private static string Texto(Guid? valor) => valor is null ? "null" : $"'{valor}'";

    private static string InserirFila(Guid tenant, Guid id, Guid organizacao, string nome) =>
        "insert into contact_center.service_queues (tenant_id, id, organization_id, business_unit_id, department_id, name, name_key, status, version, created_at, updated_at) " +
        $"values ('{tenant}', '{id}', '{organizacao}', null, null, '{nome}', '{nome.ToLowerInvariant()}', 1, 1, now(), now())";

    private static string InserirItem(
        Guid tenant, Guid id, Guid sessao, Guid fila, Guid organizacao, int status = 1, Guid? responsavel = null, bool encerrado = false, long versao = 1, string? extras = null)
    {
        var colunas = "tenant_id, id, session_id, organization_id, queue_id, status, assigned_user_id, version, entered_at, closed_at, updated_at, pending_attempts";
        var valores = $"'{tenant}', '{id}', '{sessao}', '{organizacao}', '{fila}', {status}, {Texto(responsavel)}, {versao}, now(), {(encerrado ? "now()" : "null")}, now(), 0";
        if (extras is not null)
        {
            var partes = extras.Split('=', 2);
            colunas += ", " + partes[0];
            valores += ", " + partes[1];
        }

        return $"insert into contact_center.queue_items ({colunas}) values ({valores})";
    }

    private static string InserirHistorico(Guid tenant, Guid id, Guid item, Guid sessao, Guid operacao, Guid fila, int tipo = 1) =>
        "insert into contact_center.queue_item_history (tenant_id, id, item_id, session_id, operation_id, kind, from_queue_id, to_queue_id, reason, occurred_at) " +
        $"values ('{tenant}', '{id}', '{item}', '{sessao}', '{operacao}', {tipo}, null, '{fila}', 'handoff', now())";

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

    [Fact]
    public async Task O_banco_garante_no_maximo_um_item_ativo_por_conversa_e_libera_depois_de_encerrado()
    {
        var (c, tenant, fila) = await CenaAsync("dbitemunico");
        var sessao = Guid.NewGuid();
        var primeiro = Guid.NewGuid();
        await Executar(c, InserirItem(tenant, primeiro, sessao, fila, c.OrganizacaoId));

        var segundo = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), sessao, fila, c.OrganizacaoId)));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_queue_items_active_session"), (segundo.SqlState, segundo.ConstraintName));

        // Encerrado o primeiro, a conversa pode entrar de novo, e vários encerrados convivem.
        await Executar(c, $"update contact_center.queue_items set status = 3, closed_at = now() where tenant_id = '{tenant}' and id = '{primeiro}'");
        var terceiro = Guid.NewGuid();
        Assert.Equal(1, await Executar(c, InserirItem(tenant, terceiro, sessao, fila, c.OrganizacaoId)));
        await Executar(c, $"update contact_center.queue_items set status = 3, closed_at = now() where tenant_id = '{tenant}' and id = '{terceiro}'");
        Assert.Equal(1, await Executar(c, InserirItem(tenant, Guid.NewGuid(), sessao, fila, c.OrganizacaoId)));
    }

    [Fact]
    public async Task A_mesma_conversa_em_outro_tenant_nao_conflita()
    {
        var (c, tenant, fila) = await CenaAsync("dbitemtenanta");
        var (outro, tenantB, filaB) = await CenaAsync("dbitemtenantb");
        var sessao = Guid.NewGuid();

        Assert.Equal(1, await Executar(c, InserirItem(tenant, Guid.NewGuid(), sessao, fila, c.OrganizacaoId)));
        Assert.Equal(1, await Executar(outro, InserirItem(tenantB, Guid.NewGuid(), sessao, filaB, outro.OrganizacaoId)));
    }

    [Theory]
    [InlineData(2, false, "ck_queue_items_assigned")]   // atribuído sem responsável
    [InlineData(1, true, "ck_queue_items_assigned")]    // responsável em item que não está atribuído
    [InlineData(3, false, "ck_queue_items_closed")]     // encerrado sem data de encerramento
    [InlineData(0, false, "ck_queue_items_status")]
    [InlineData(4, false, "ck_queue_items_status")]
    public async Task O_banco_recusa_estado_incoerente_do_item_o_responsavel_e_uma_coluna_so_e_so_existe_se_atribuido(int status, bool comResponsavel, string restricao)
    {
        var (c, tenant, fila) = await CenaAsync("dbitemestado");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, status, comResponsavel ? Guid.NewGuid() : null)));

        Assert.Equal((PostgresErrorCodes.CheckViolation, restricao), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task O_banco_recusa_data_de_encerramento_em_item_aberto_versao_zero_e_pendencia_em_item_encerrado()
    {
        var (c, tenant, fila) = await CenaAsync("dbitemencerra");

        var abertoComData = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, encerrado: true)));
        var versaoZero = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, versao: 0)));

        Assert.Equal("ck_queue_items_closed", abertoComData.ConstraintName);
        Assert.Equal("ck_queue_items_version", versaoZero.ConstraintName);
    }

    [Theory]
    [InlineData("pending_kind=2")]                                                // tipo sem os demais campos
    [InlineData("pending_operation_id='5a1a0a7e-0000-4000-8000-000000000001'")]   // operação sem tipo
    [InlineData("pending_user_id='5a1a0a7e-0000-4000-8000-000000000001'")]        // destino sem tipo
    [InlineData("pending_kind=9")]                                                // tipo fora do domínio
    public async Task A_operacao_pendente_e_tudo_ou_nada_no_banco(string extras)
    {
        var (c, tenant, fila) = await CenaAsync("dbitempendente");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, extras: extras)));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_pending"), (falha.SqlState, falha.ConstraintName));
    }

    private static string InserirItemPendente(Guid tenant, Guid fila, Guid organizacao, Guid? unidade, Guid? setor)
    {
        var operacao = Guid.NewGuid();
        return "insert into contact_center.queue_items (tenant_id, id, session_id, organization_id, queue_id, status, version, entered_at, updated_at, pending_attempts, " +
               "pending_kind, pending_operation_id, pending_transition_id, pending_transfer_id, pending_queue_id, pending_business_unit_id, pending_department_id, pending_reason, pending_started_at) " +
               $"values ('{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{organizacao}', '{fila}', 1, 1, now(), now(), 0, 4, '{operacao}', '{operacao}', '{Guid.NewGuid()}', '{fila}', " +
               $"{Texto(unidade)}, {Texto(setor)}, 'transfer', now())";
    }

    [Fact]
    public async Task Uma_operacao_pendente_completa_e_aceita_e_o_setor_pendente_so_existe_com_a_unidade_pendente()
    {
        var (c, tenant, fila) = await CenaAsync("dbitempendsetor");

        Assert.Equal(1, await Executar(c, InserirItemPendente(tenant, fila, c.OrganizacaoId, null, null)));
        Assert.Equal(1, await Executar(c, InserirItemPendente(tenant, fila, c.OrganizacaoId, Guid.NewGuid(), Guid.NewGuid())));
        var semUnidade = await FalhaDoBancoAsync(Executar(c, InserirItemPendente(tenant, fila, c.OrganizacaoId, null, Guid.NewGuid())));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_pending"), (semUnidade.SqlState, semUnidade.ConstraintName));
    }

    [Fact]
    public async Task Operacao_pendente_sem_tipo_mas_com_os_demais_campos_e_recusada_o_NULL_da_comparacao_nao_pode_passar_pela_restricao()
    {
        var (c, tenant, fila) = await CenaAsync("dbitempendnulo");
        var operacao = Guid.NewGuid();

        // Sem o tipo (NULL), o teste "pending_kind IN (...)" vale NULL, e uma restrição CHECK aceita NULL: o resto da operação
        // pendente (identificadores, motivo e data) ficaria gravado em um item que, para o aplicativo, não tem operação nenhuma.
        var semTipo = "insert into contact_center.queue_items (tenant_id, id, session_id, organization_id, queue_id, status, version, entered_at, updated_at, pending_attempts, " +
            "pending_operation_id, pending_transition_id, pending_transfer_id, pending_queue_id, pending_reason, pending_started_at) " +
            $"values ('{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{c.OrganizacaoId}', '{fila}', 1, 1, now(), now(), 0, '{operacao}', '{operacao}', '{Guid.NewGuid()}', '{fila}', 'transfer', now())";

        var falha = await FalhaDoBancoAsync(Executar(c, semTipo));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_pending"), (falha.SqlState, falha.ConstraintName));
    }

    private static string InserirEntradaPendenteComBase(Guid tenant, Guid fila, Guid organizacao, string colunasDaBase, string valoresDaBase)
    {
        var operacao = Guid.NewGuid();
        return "insert into contact_center.queue_items (tenant_id, id, session_id, organization_id, queue_id, status, version, entered_at, updated_at, pending_attempts, " +
               $"pending_kind, pending_operation_id, pending_transition_id, pending_transfer_id, pending_queue_id, pending_reason, pending_started_at, {colunasDaBase}) " +
               $"values ('{tenant}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', '{organizacao}', '{fila}', 1, 1, now(), now(), 0, 1, '{operacao}', '{operacao}', " +
               $"'{Guid.NewGuid()}', '{fila}', 'handoff', now(), {valoresDaBase})";
    }

    [Theory]
    [InlineData("pending_basis_period, pending_basis_ai_mode", "0, true", true)]                                                                  // a entrada na conversa em IA que nunca teve atendimento humano
    [InlineData("pending_basis_period, pending_basis_responder", "1, '5a1a0a7e-0000-4000-8000-000000000001'", true)]                              // atendimento humano com responsável
    [InlineData("pending_basis_period, pending_basis_responder, pending_basis_ai_mode", "1, '5a1a0a7e-0000-4000-8000-000000000001', true", false)] // a IA não tem responsável
    [InlineData("pending_basis_ai_mode", "true", false)]                                                                                          // a base de IA sem o período lido
    [InlineData("pending_basis_responder", "'5a1a0a7e-0000-4000-8000-000000000001'", false)]                                                      // responsável sem período
    public async Task A_base_da_autorizacao_da_operacao_pendente_e_coerente_no_banco(string colunas, string valores, bool aceita)
    {
        var (c, tenant, fila) = await CenaAsync("dbitembase");
        var comando = InserirEntradaPendenteComBase(tenant, fila, c.OrganizacaoId, colunas, valores);

        if (aceita)
        {
            Assert.Equal(1, await Executar(c, comando));
            return;
        }

        var falha = await FalhaDoBancoAsync(Executar(c, comando));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_pending"), (falha.SqlState, falha.ConstraintName));
    }

    [Theory]
    [InlineData("placement_epoch=1", true)]
    [InlineData("placement_epoch=40", true)]
    [InlineData("placement_epoch=0", false)]
    [InlineData("placement_epoch=-1", false)]
    public async Task O_banco_recusa_geracao_de_colocacao_do_item_que_nao_seja_a_partir_de_1(string extras, bool aceita)
    {
        var (c, tenant, fila) = await CenaAsync("dbitemgeracao");
        var comando = InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, extras: extras);

        if (aceita)
        {
            Assert.Equal(1, await Executar(c, comando));
            return;
        }

        var falha = await FalhaDoBancoAsync(Executar(c, comando));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_placement"), (falha.SqlState, falha.ConstraintName));
    }

    [Theory]
    [InlineData("pending_basis_period, pending_basis_placement_epoch", "1, 3", true)]
    [InlineData("pending_basis_period, pending_basis_placement_epoch", "1, 0", false)]
    [InlineData("pending_basis_placement_epoch", "3", false)] // a geração sem o período lido
    public async Task A_geracao_da_colocacao_na_base_da_operacao_pendente_e_coerente_no_banco(string colunas, string valores, bool aceita)
    {
        var (c, tenant, fila) = await CenaAsync("dbitembasegeracao");
        var comando = InserirEntradaPendenteComBase(tenant, fila, c.OrganizacaoId, colunas, valores);

        if (aceita)
        {
            Assert.Equal(1, await Executar(c, comando));
            return;
        }

        var falha = await FalhaDoBancoAsync(Executar(c, comando));
        Assert.Equal(PostgresErrorCodes.CheckViolation, falha.SqlState);
        Assert.Contains(falha.ConstraintName, new[] { "ck_queue_items_pending", "ck_queue_items_placement" });
    }

    [Fact]
    public async Task A_geracao_na_base_nao_existe_sem_operacao_pendente()
    {
        var (c, tenant, fila) = await CenaAsync("dbitembasegeracao2");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, extras: "pending_basis_placement_epoch=2")));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_pending"), (falha.SqlState, falha.ConstraintName));
    }

    [Theory]
    [InlineData("resync_state_version=7", true)]
    [InlineData("history_limit_seq=0", true)]
    [InlineData("history_limit_seq=12", true)]
    [InlineData("resync_state_version=-1", false)]
    [InlineData("history_limit_seq=-1", false)]
    public async Task O_banco_recusa_marca_de_projecao_e_limite_de_historico_negativos(string extras, bool aceita)
    {
        var (c, tenant, fila) = await CenaAsync("dbitemproj");
        var comando = InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, extras: extras);

        if (aceita)
        {
            Assert.Equal(1, await Executar(c, comando));
            return;
        }

        var falha = await FalhaDoBancoAsync(Executar(c, comando));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_projection"), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task A_base_de_IA_nao_existe_sem_operacao_pendente()
    {
        var (c, tenant, fila) = await CenaAsync("dbitembaseia");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), fila, c.OrganizacaoId, extras: "pending_basis_ai_mode=true")));

        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_items_pending"), (falha.SqlState, falha.ConstraintName));
    }

    [Fact]
    public async Task A_chave_estrangeira_composta_recusa_item_ligado_a_fila_de_outro_tenant()
    {
        var (c, tenant, _) = await CenaAsync("dbitemfka");
        var (_, _, filaDoOutro) = await CenaAsync("dbitemfkb");

        var falha = await FalhaDoBancoAsync(Executar(c, InserirItem(tenant, Guid.NewGuid(), Guid.NewGuid(), filaDoOutro, c.OrganizacaoId)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
    }

    [Fact]
    public async Task O_historico_so_aceita_acrescimo_o_banco_recusa_alterar_apagar_e_truncar()
    {
        var (c, tenant, fila) = await CenaAsync("dbhistappend");
        var item = Guid.NewGuid();
        var sessao = Guid.NewGuid();
        var historico = Guid.NewGuid();
        await Executar(c, InserirItem(tenant, item, sessao, fila, c.OrganizacaoId));
        Assert.Equal(1, await Executar(c, InserirHistorico(tenant, historico, item, sessao, Guid.NewGuid(), fila)));

        var alterar = await FalhaDoBancoAsync(Executar(c, $"update contact_center.queue_item_history set reason = 'outro' where tenant_id = '{tenant}' and id = '{historico}'"));
        var apagar = await FalhaDoBancoAsync(Executar(c, $"delete from contact_center.queue_item_history where tenant_id = '{tenant}' and id = '{historico}'"));
        var truncar = await FalhaDoBancoAsync(Executar(c, "truncate table contact_center.queue_item_history"));

        Assert.All(new[] { alterar, apagar, truncar }, f => Assert.Equal(PostgresErrorCodes.RestrictViolation, f.SqlState));
        Assert.Equal(1, await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().History.CountAsync(h => h.Id == historico)));
    }

    [Fact]
    public async Task Cada_operacao_deixa_no_maximo_um_registro_no_historico()
    {
        var (c, tenant, fila) = await CenaAsync("dbhistunico");
        var item = Guid.NewGuid();
        var sessao = Guid.NewGuid();
        var operacao = Guid.NewGuid();
        await Executar(c, InserirItem(tenant, item, sessao, fila, c.OrganizacaoId));
        await Executar(c, InserirHistorico(tenant, Guid.NewGuid(), item, sessao, operacao, fila));

        var repetida = await FalhaDoBancoAsync(Executar(c, InserirHistorico(tenant, Guid.NewGuid(), item, sessao, operacao, fila, tipo: 2)));
        var tipoInvalido = await FalhaDoBancoAsync(Executar(c, InserirHistorico(tenant, Guid.NewGuid(), item, sessao, Guid.NewGuid(), fila, tipo: 9)));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_queue_item_history_operation"), (repetida.SqlState, repetida.ConstraintName));
        Assert.Equal((PostgresErrorCodes.CheckViolation, "ck_queue_item_history_kind"), (tipoInvalido.SqlState, tipoInvalido.ConstraintName));
    }

    [Fact]
    public async Task A_chave_estrangeira_composta_recusa_historico_ligado_ao_item_de_outro_tenant()
    {
        var (c, tenant, fila) = await CenaAsync("dbhistfka");
        var (outro, tenantB, filaB) = await CenaAsync("dbhistfkb");
        var itemDoOutro = Guid.NewGuid();
        await Executar(outro, InserirItem(tenantB, itemDoOutro, Guid.NewGuid(), filaB, outro.OrganizacaoId));

        var falha = await FalhaDoBancoAsync(Executar(c, InserirHistorico(tenant, Guid.NewGuid(), itemDoOutro, Guid.NewGuid(), Guid.NewGuid(), fila)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, falha.SqlState);
    }

    [Fact]
    public async Task Nenhuma_chave_estrangeira_do_modulo_aponta_para_outro_esquema_mesmo_com_itens_e_historico()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbitemesquemas");

        var esquemas = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Database
            .SqlQueryRaw<string>(
                "select distinct pk.table_schema::text as \"Value\" from information_schema.referential_constraints rc " +
                "join information_schema.table_constraints fk on fk.constraint_name = rc.constraint_name and fk.constraint_schema = rc.constraint_schema " +
                "join information_schema.table_constraints pk on pk.constraint_name = rc.unique_constraint_name and pk.constraint_schema = rc.unique_constraint_schema " +
                "where fk.table_schema = 'contact_center' and fk.table_name in ('queue_items', 'queue_item_history')")
            .ToListAsync());

        Assert.Equal(new[] { "contact_center" }, esquemas); // o conjunto não é vazio e só aponta para o próprio esquema
    }

    [Fact]
    public async Task O_DbContext_recusa_gravar_item_de_outro_tenant_antes_de_chegar_ao_banco()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "dbitemtrava");
        var outro = await CenarioDeConversas.CriarAsync(banco, "dbitemtravab");
        var deOutroTenant = QueueItem.Enter(
            outro.Tenant.TenantId, Guid.NewGuid(), outro.OrganizacaoId, null, null, Guid.NewGuid(), Guid.NewGuid(), null, "handoff", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<CrossTenantAccessException>(() => c.ComoAsync(c.ContextoDeSistema, async sp =>
        {
            var db = sp.GetRequiredService<ContactCenterDbContext>();
            db.Items.Add(deOutroTenant);
            return await db.SaveChangesAsync();
        }));

        Assert.Null(await outro.ItemAsync(deOutroTenant.Id));
    }
}
