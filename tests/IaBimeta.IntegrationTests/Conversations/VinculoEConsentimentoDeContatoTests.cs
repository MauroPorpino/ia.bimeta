using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// E2.1 com PostgreSQL real (ADR 0041, seções 2, 4 e 9): as duas tabelas aditivas, as restrições do banco, o gatilho de somente acréscimo, o isolamento entre tenants e a migration com <c>Down</c> (aplicada e revertida
/// numa transação que é desfeita, para não mexer no banco compartilhado). Os testes de domínio, sem banco, estão em <c>ContactLinkAndConsentTests</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VinculoEConsentimentoDeContatoTests(PostgresFixture banco)
{
    private const string MigracaoAnterior = "20261112214112_FlowMessageAuthor";
    private const string MigracaoDaE2 = "20261118182105_ContactLinksAndConsents";

    private static readonly DateTimeOffset Agora = DateTimeOffset.UtcNow;

    private async Task<Contact> CriarContatoAsync(CenarioDeConversas c)
    {
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var contato = Contact.Create(c.Tenant.TenantId, ChannelInstanceId.New(), $"55119{Random.Shared.NextInt64(10_000_000, 99_999_999)}", null, Agora);
        db.Contacts.Add(contato);
        await db.SaveChangesAsync();
        return contato;
    }

    private static ContactExternalLink NovoVinculo(CenarioDeConversas c, Guid contato, Guid conexao, string cliente = "CLI-1") =>
        ContactExternalLink.Create(c.Tenant.TenantId, c.OrganizacaoId, conexao, cliente, contato, LinkOrigin.Integration, ActorKind.Integration, conexao, Agora);

    private static ContactConsent NovoConsentimento(CenarioDeConversas c, string telefone, ConsentPurpose finalidade, ConsentStatus situacao, Guid? organizacao = null) =>
        ContactConsent.Create(c.Tenant.TenantId, organizacao ?? c.OrganizacaoId, telefone, finalidade, situacao, ConsentOrigin.IntegrationAsserted, "Formulário v3", Agora.AddHours(-1), ActorKind.Integration, Guid.NewGuid(), "CLI-1", null, null, Agora);

    private static ContactConsent Palavra(CenarioDeConversas c, string telefone, ConsentPurpose finalidade, Guid mensagem) =>
        ContactConsent.Create(c.Tenant.TenantId, c.OrganizacaoId, telefone, finalidade, ConsentStatus.Revoked, ConsentOrigin.CustomerKeyword, $"msg:{mensagem:N};palavra:parar", Agora, ActorKind.System, null, null, Guid.NewGuid(), mensagem, Agora);

    private async Task<string?> SqlStateAsync(Func<Task> acao)
    {
        try
        {
            await acao();
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return pg.SqlState;
        }
        catch (PostgresException pg)
        {
            return pg.SqlState;
        }
    }

    private async Task<T> EscalarAsync<T>(string sql)
    {
        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    // O catálogo

    [Fact]
    public async Task As_duas_tabelas_existem_com_chave_composta_por_tenant_e_tenant_id_obrigatorio()
    {
        foreach (var tabela in new[] { "contact_external_links", "contact_consents" })
        {
            Assert.Equal(1L, await EscalarAsync<long>($"select count(*) from information_schema.tables where table_schema = 'conversations' and table_name = '{tabela}'"));
            Assert.Equal("NO", await EscalarAsync<string>($"select is_nullable from information_schema.columns where table_schema = 'conversations' and table_name = '{tabela}' and column_name = 'tenant_id'"));
            var chave = await EscalarAsync<string>(
                "select string_agg(a.attname, ',' order by array_position(i.indkey::int2[], a.attnum)) from pg_index i join pg_attribute a on a.attrelid = i.indrelid and a.attnum = any(i.indkey) " +
                $"where i.indrelid = 'conversations.{tabela}'::regclass and i.indisprimary");
            Assert.Equal("tenant_id,id", chave);
        }

        // E nada além das duas tabelas novas (D1: o alias é da E3).
        Assert.Equal(0L, await EscalarAsync<long>("select count(*) from information_schema.tables where table_schema = 'conversations' and table_name = 'contact_aliases'"));
    }

    [Fact]
    public async Task O_vinculo_tem_chave_estrangeira_ao_contato_com_tenant_e_nenhuma_entre_esquemas()
    {
        var referencias = await EscalarAsync<string>(
            "select string_agg(confrelid::regclass::text || ':' || (select string_agg(a.attname, ',' order by x.ord) from unnest(conkey) with ordinality x(attnum, ord) join pg_attribute a on a.attrelid = conrelid and a.attnum = x.attnum), '|') " +
            "from pg_constraint where conrelid = 'conversations.contact_external_links'::regclass and contype = 'f'");
        Assert.Equal("conversations.contacts:tenant_id,contact_id", referencias);

        // O consentimento não tem chave estrangeira alguma (o titular pode nunca ter escrito; sessão e mensagem são só referências de rastreio).
        Assert.Equal(0L, await EscalarAsync<long>("select count(*) from pg_constraint where conrelid = 'conversations.contact_consents'::regclass and contype = 'f'"));
    }

    // Vínculo

    [Fact]
    public async Task Vinculo_persiste_e_no_maximo_um_ativo_por_conexao_cliente_e_contato_mas_revincular_apos_desvincular_insere_outra_linha()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "vinc1");
        var contato = await CriarContatoAsync(c);
        var conexao = Guid.NewGuid();

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(c, contato.Id, conexao));
            await db.SaveChangesAsync();
        }

        // O segundo vínculo ativo igual é recusado pelo índice único parcial (23505).
        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(c, contato.Id, conexao));
            Assert.Equal("23505", await SqlStateAsync(() => db.SaveChangesAsync()));
        }

        // Outro cliente no MESMO contato (telefone compartilhado) é permitido; a unicidade é por (conexão, cliente, contato).
        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(c, contato.Id, conexao, "CLI-2"));
            await db.SaveChangesAsync();
        }

        // Desvincular é lógico; revincular insere outra linha e a anterior permanece como histórico.
        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var vinculo = await db.ContactLinks.SingleAsync(l => l.CustomerRef == "CLI-1");
            Assert.True(vinculo.Unlink(ActorKind.Integration, conexao, Agora.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(c, contato.Id, conexao));
            await db.SaveChangesAsync();
        }

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var linhas = await db.ContactLinks.Where(l => l.CustomerRef == "CLI-1").OrderBy(l => l.LinkedAt).ToListAsync();
            Assert.Equal(2, linhas.Count);
            Assert.Equal(1, linhas.Count(l => l.IsActive));
            Assert.Equal(1, linhas.Count(l => !l.IsActive && l.UnlinkedById == conexao && l.UnlinkedByKind == ActorKind.Integration));
        }
    }

    [Fact]
    public async Task O_banco_recusa_vinculo_a_contato_de_outro_tenant_e_o_contexto_recusa_gravar_para_outro_tenant()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "vinca");
        var b = await CenarioDeConversas.CriarAsync(banco, "vincb");
        var contatoDeA = await CriarContatoAsync(a);

        // B tenta vincular um contato de A: a chave estrangeira composta (tenant_id, contact_id) recusa (23503).
        await using (var escopo = banco.NovoEscopo(b.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(b, contatoDeA.Id, Guid.NewGuid()));
            Assert.Equal("23503", await SqlStateAsync(() => db.SaveChangesAsync()));
        }

        // No contexto de A, uma entidade marcada com o tenant de B nem chega ao banco.
        await using (var escopo = banco.NovoEscopo(a.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(b, contatoDeA.Id, Guid.NewGuid()));
            await Assert.ThrowsAsync<CrossTenantAccessException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Os_vinculos_e_os_consentimentos_de_um_tenant_sao_invisiveis_ao_outro()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "isoa");
        var b = await CenarioDeConversas.CriarAsync(banco, "isob");
        var contatoDeA = await CriarContatoAsync(a);

        await using (var escopo = banco.NovoEscopo(a.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.ContactLinks.Add(NovoVinculo(a, contatoDeA.Id, Guid.NewGuid()));
            db.Consents.Add(NovoConsentimento(a, "+5511999990100", ConsentPurpose.Marketing, ConsentStatus.Granted));
            await db.SaveChangesAsync();
        }

        await using (var escopo = banco.NovoEscopo(b.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            Assert.Empty(await db.ContactLinks.ToListAsync());
            Assert.Empty(await db.Consents.ToListAsync());
            Assert.Empty(await db.Consents.Where(x => x.Phone == "+5511999990100").ToListAsync());
        }

        await using (var escopo = banco.NovoEscopo(a.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            Assert.Single(await db.ContactLinks.ToListAsync());
            Assert.Single(await db.Consents.ToListAsync());
        }
    }

    // Consentimento

    [Fact]
    public async Task Consentimento_e_somente_acrescimo_a_ultima_linha_da_chave_e_a_efetiva_e_o_banco_recusa_update_e_delete()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cons1");
        const string telefone = "+5511999990200";

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.Consents.Add(NovoConsentimento(c, telefone, ConsentPurpose.Marketing, ConsentStatus.Granted));
            await db.SaveChangesAsync();
            db.Consents.Add(NovoConsentimento(c, telefone, ConsentPurpose.Marketing, ConsentStatus.Revoked));
            db.Consents.Add(NovoConsentimento(c, telefone, ConsentPurpose.Utility, ConsentStatus.Granted));
            await db.SaveChangesAsync();
        }

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var linhas = await db.Consents.Where(x => x.Phone == telefone).OrderBy(x => x.Seq).ToListAsync();
            Assert.Equal(3, linhas.Count);
            Assert.True(linhas[0].Seq < linhas[1].Seq && linhas[1].Seq < linhas[2].Seq);

            // A situação efetiva da chave (organização, telefone, finalidade) é a última linha.
            var efetivaMarketing = await db.Consents.Where(x => x.OrganizationId == c.OrganizacaoId && x.Phone == telefone && x.Purpose == ConsentPurpose.Marketing).OrderByDescending(x => x.Seq).FirstAsync();
            Assert.Equal(ConsentStatus.Revoked, efetivaMarketing.Status);
            var efetivaUtilidade = await db.Consents.Where(x => x.OrganizationId == c.OrganizacaoId && x.Phone == telefone && x.Purpose == ConsentPurpose.Utility).OrderByDescending(x => x.Seq).FirstAsync();
            Assert.Equal(ConsentStatus.Granted, efetivaUtilidade.Status);
        }

        // O banco recusa a alteração e a exclusão (gatilho), mesmo por SQL direto e mesmo pelo tenant dono.
        await using var escopoSql = banco.NovoEscopo(c.ContextoDeSistema);
        var contexto = escopoSql.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        Assert.Equal("23000", await SqlStateAsync(() => contexto.Database.ExecuteSqlRawAsync("update conversations.contact_consents set status = 1 where phone = {0}", telefone)));
        Assert.Equal("23000", await SqlStateAsync(() => contexto.Database.ExecuteSqlRawAsync("update conversations.contact_consents set evidence = 'x' where phone = {0}", telefone)));
        Assert.Equal("23000", await SqlStateAsync(() => contexto.Database.ExecuteSqlRawAsync("delete from conversations.contact_consents where phone = {0}", telefone)));
        Assert.Equal(3L, await EscalarAsync<long>($"select count(*) from conversations.contact_consents where tenant_id = '{c.Tenant.TenantId.Value}' and phone = '{telefone}'"));
    }

    [Fact]
    public async Task O_aplicativo_nao_consegue_alterar_nem_apagar_uma_linha_de_consentimento_pelo_EF()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cons2");
        const string telefone = "+5511999990300";

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.Consents.Add(NovoConsentimento(c, telefone, ConsentPurpose.Marketing, ConsentStatus.Granted));
            await db.SaveChangesAsync();
        }

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            var linha = await db.Consents.SingleAsync(x => x.Phone == telefone);
            db.Consents.Remove(linha);
            Assert.Equal("23000", await SqlStateAsync(() => db.SaveChangesAsync()));
        }

        Assert.Equal(1L, await EscalarAsync<long>($"select count(*) from conversations.contact_consents where tenant_id = '{c.Tenant.TenantId.Value}' and phone = '{telefone}'"));
    }

    [Fact]
    public async Task As_restricoes_do_banco_recusam_telefone_fora_do_E164_origem_sem_autor_e_evidencia_vazia()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cons3");
        var tenant = c.Tenant.TenantId.Value;

        string Insert(string telefone, int origem, int tipo, string? autor, string evidencia, string? sessao = null, string? mensagem = null) =>
            "insert into conversations.contact_consents (id, tenant_id, organization_id, phone, purpose, status, origin, evidence, collected_at, recorded_at, actor_kind, actor_id, session_id, source_message_id) " +
            $"values ('{Guid.NewGuid()}', '{tenant}', '{c.OrganizacaoId}', '{telefone}', 2, 1, {origem}, '{evidencia}', now(), now(), {tipo}, {(autor is null ? "null" : $"'{autor}'")}, {(sessao is null ? "null" : $"'{sessao}'")}, {(mensagem is null ? "null" : $"'{mensagem}'")})";

        async Task<string?> ExecutarAsync(string sql)
        {
            await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            return await SqlStateAsync(() => db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}")));
        }

        var autor = Guid.NewGuid().ToString();

        // O caso válido passa (a base do teste).
        Assert.Null(await ExecutarAsync(Insert("+5511999990400", 1, 1, autor, "prova")));

        Assert.Equal("23514", await ExecutarAsync(Insert("5511999990401", 1, 1, autor, "prova")));        // sem +
        Assert.Equal("23514", await ExecutarAsync(Insert("+0511999990402", 1, 1, autor, "prova")));       // zero inicial
        Assert.Equal("23514", await ExecutarAsync(Insert("+1234567", 1, 1, autor, "prova")));             // curto
        Assert.Equal("23514", await ExecutarAsync(Insert("+5511999990403", 1, 1, autor, "   ")));         // evidência vazia
        Assert.Equal("23514", await ExecutarAsync(Insert("+5511999990404", 1, 2, autor, "prova")));       // origem 1 com autor usuário
        Assert.Equal("23514", await ExecutarAsync(Insert("+5511999990405", 1, 1, null, "prova")));        // origem 1 sem autor
        Assert.Equal("23514", await ExecutarAsync(Insert("+5511999990406", 3, 3, null, "prova")));        // palavra-chave sem sessão nem mensagem
        Assert.Equal("23514", await ExecutarAsync(Insert("+5511999990407", 9, 1, autor, "prova")));       // origem fora do domínio
        Assert.Null(await ExecutarAsync(Insert("+5511999990408", 3, 3, null, "msg;palavra", Guid.NewGuid().ToString(), Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task A_revogacao_por_palavra_chave_e_idempotente_por_mensagem_de_origem_e_finalidade()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cons4");
        const string telefone = "+5511999990500";
        var mensagem = Guid.NewGuid();

        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.Consents.Add(Palavra(c, telefone, ConsentPurpose.Utility, mensagem));
            db.Consents.Add(Palavra(c, telefone, ConsentPurpose.Marketing, mensagem));   // outra finalidade da MESMA mensagem: permitida
            await db.SaveChangesAsync();
        }

        // A reentrega da mesma mensagem (ou a segunda entrega concorrente) tenta gravar de novo: o índice único parcial a recusa.
        await using (var escopo = banco.NovoEscopo(c.ContextoDeSistema))
        {
            var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
            db.Consents.Add(Palavra(c, telefone, ConsentPurpose.Utility, mensagem));
            Assert.Equal("23505", await SqlStateAsync(() => db.SaveChangesAsync()));
        }

        Assert.Equal(2L, await EscalarAsync<long>($"select count(*) from conversations.contact_consents where tenant_id = '{c.Tenant.TenantId.Value}' and phone = '{telefone}'"));
    }

    [Fact]
    public async Task O_registro_do_sistema_externo_nao_e_afetado_pelo_indice_da_palavra_chave()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cons5");
        const string telefone = "+5511999990600";

        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        for (var i = 0; i < 3; i++)
        {
            db.Consents.Add(NovoConsentimento(c, telefone, ConsentPurpose.Marketing, i % 2 == 0 ? ConsentStatus.Granted : ConsentStatus.Revoked));
        }

        await db.SaveChangesAsync();
        Assert.Equal(3L, await EscalarAsync<long>($"select count(*) from conversations.contact_consents where tenant_id = '{c.Tenant.TenantId.Value}' and phone = '{telefone}'"));
    }

    // A migration

    [Fact]
    public async Task A_migration_reverte_com_Down_e_reaplica_com_Up_numa_transacao_desfeita()
    {
        await using var escopo = banco.NovoEscopo();
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var migrator = db.GetService<IMigrator>();

        // Sem blocos de transação no script: a transação é a deste teste, e é desfeita no fim (o banco compartilhado fica intacto).
        var down = migrator.GenerateScript(MigracaoDaE2, MigracaoAnterior, MigrationsSqlGenerationOptions.NoTransactions);
        var up = migrator.GenerateScript(MigracaoAnterior, MigracaoDaE2, MigrationsSqlGenerationOptions.NoTransactions);
        Assert.Contains("DROP TABLE conversations.contact_consents", down);
        Assert.Contains("DROP TABLE conversations.contact_external_links", down);
        Assert.Contains("DROP FUNCTION IF EXISTS conversations.contact_consents_append_only", down);
        Assert.Contains("CREATE TRIGGER tr_contact_consents_append_only", up);

        await using var conexao = new NpgsqlConnection(banco.ConnectionString);
        await conexao.OpenAsync();
        await using var transacao = await conexao.BeginTransactionAsync();

        async Task<long> ContarAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conexao, transacao);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        async Task ExecutarAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conexao, transacao);
            await cmd.ExecuteNonQueryAsync();
        }

        const string tabelas = "select count(*) from information_schema.tables where table_schema = 'conversations' and table_name in ('contact_consents', 'contact_external_links')";
        const string funcao = "select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'conversations' and p.proname = 'contact_consents_append_only'";
        var historico = $"select count(*) from conversations.__ef_migrations_history where \"MigrationId\" = '{MigracaoDaE2}'";

        Assert.Equal(2L, await ContarAsync(tabelas));
        Assert.Equal(1L, await ContarAsync(funcao));
        Assert.Equal(1L, await ContarAsync(historico));

        await ExecutarAsync(down);
        Assert.Equal(0L, await ContarAsync(tabelas));
        Assert.Equal(0L, await ContarAsync(funcao));
        Assert.Equal(0L, await ContarAsync(historico));

        // As tabelas anteriores ficam intactas pelo Down (a migration só acrescentou as duas).
        Assert.Equal(1L, await ContarAsync("select count(*) from information_schema.tables where table_schema = 'conversations' and table_name = 'contacts'"));

        await ExecutarAsync(up);
        Assert.Equal(2L, await ContarAsync(tabelas));
        Assert.Equal(1L, await ContarAsync(funcao));
        Assert.Equal(1L, await ContarAsync(historico));

        await transacao.RollbackAsync();

        // Depois do rollback, o banco compartilhado continua com a migration aplicada.
        Assert.Equal(2L, await EscalarAsync<long>(tabelas));
        Assert.Contains(MigracaoDaE2, await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
