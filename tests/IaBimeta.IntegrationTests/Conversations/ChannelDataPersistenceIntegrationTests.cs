using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Conversations;

/// <summary>
/// Fatia A3 com PostgreSQL real, DEPOIS de a migração <c>MessageChannelData</c> ser aplicada (a <c>PostgresFixture</c> a aplica ao iniciar): o bloco opaco de metadados do canal é
/// gravado com a mensagem do cliente, volta igual, não vaza entre tenants, não é sobrescrito por uma repetição do evento e é protegido pela restrição do banco. Os testes do
/// MODELO, que não usam banco, estão em <see cref="MessageChannelDataModelTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelDataPersistenceIntegrationTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

    private static ChannelData Midia(int versao = 1) => new(
        versao,
        new Dictionary<string, string>
        {
            ["media.kind"] = "image",
            ["media.mime"] = "image/jpeg",
            ["media.size"] = "48213",
            ["media.provider-id"] = "1234567890",
        });

    private async Task<IngestInboundMessageResult> IngerirAsync(
        CenarioDeConversas c, string contato, string evento, ChannelData? dados, MessageKind tipo = MessageKind.Text, string? texto = "Olá")
    {
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        return await escopo.ServiceProvider.GetRequiredService<IConversationIngestion>().IngestInboundMessageAsync(
            new IngestInboundMessageCommand(c.CanalId, contato, null, c.OrganizacaoId, null, evento, DateTimeOffset.UtcNow, tipo, texto, ChannelData: dados));
    }

    private async Task<int> SqlAsync(RequestContext contexto, string sql, params object[] parametros)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Database.ExecuteSqlRawAsync(sql, parametros);
    }

    private async Task<T> ConsultaAsync<T>(RequestContext contexto, Func<ConversationsDbContext, Task<T>> consulta)
    {
        await using var escopo = banco.NovoEscopo(contexto);
        return await consulta(escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>());
    }

    private Task<Message> LerAsync(CenarioDeConversas c, Guid mensagem) =>
        ConsultaAsync(c.ContextoDeSistema, db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == mensagem));

    // Gravação e leitura

    [Fact]
    public async Task A_mensagem_com_ChannelData_guarda_a_versao_e_os_pares_e_os_devolve_iguais()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdgrava");

        var r = await IngerirAsync(c, Contato, "evt-1", Midia(3));

        Assert.Equal(IngestOutcome.Registered, r.Outcome);
        var mensagem = await LerAsync(c, r.MessageId);
        Assert.Equal(3, mensagem.ChannelDataVersion);
        Assert.Equal(Midia().Values, mensagem.ChannelDataValues);
        Assert.Equal(3, mensagem.ChannelData!.Version);
        Assert.Equal(Midia().Values, mensagem.ChannelData.Values);

        // E o banco guarda os dois campos como o contrato diz: a versão numa coluna e os pares como um objeto jsonb.
        var guardado = await ConsultaAsync(c.ContextoDeSistema, db => db.Database
            .SqlQueryRaw<string>(
                "select channel_data_version::text || '|' || jsonb_typeof(channel_data) || '|' || (channel_data->>'media.kind') as \"Value\" from conversations.messages where tenant_id = {0} and id = {1}",
                c.Tenant.TenantId.Value, r.MessageId)
            .SingleAsync());
        Assert.Equal("3|object|image", guardado);
    }

    [Fact]
    public async Task A_mensagem_sem_ChannelData_continua_com_nulo_nos_dois_campos_como_antes()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdsem");

        var r = await IngerirAsync(c, Contato, "evt-1", null);

        var mensagem = await LerAsync(c, r.MessageId);
        Assert.Null(mensagem.ChannelDataVersion);
        Assert.Null(mensagem.ChannelDataValues);
        Assert.Null(mensagem.ChannelData);
        var nulos = await ConsultaAsync(c.ContextoDeSistema, db => db.Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from conversations.messages where tenant_id = {0} and id = {1} and channel_data is null and channel_data_version is null", c.Tenant.TenantId.Value, r.MessageId)
            .SingleAsync());
        Assert.Equal(1, nulos);
    }

    [Fact]
    public async Task Midia_pendente_guarda_o_ChannelData_e_continua_pendente_e_sem_texto()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdmidia");

        var r = await IngerirAsync(c, Contato, "evt-midia", Midia(), MessageKind.Image, texto: null);

        Assert.Equal(ContentState.Pending, r.ContentState);
        var mensagem = await LerAsync(c, r.MessageId);
        Assert.Equal((ContentState.Pending, (string?)null), (mensagem.ContentState, mensagem.Text));
        Assert.Equal(Midia().Values, mensagem.ChannelDataValues);
    }

    [Fact]
    public async Task Cada_mensagem_da_mesma_sessao_tem_o_seu_proprio_bloco_ou_nenhum()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdvarias");

        var a = await IngerirAsync(c, Contato, "evt-a", Midia(1));
        var b = await IngerirAsync(c, Contato, "evt-b", null);
        var d = await IngerirAsync(c, Contato, "evt-d", new ChannelData(2, new Dictionary<string, string> { ["outro"] = "valor" }));

        Assert.Equal(new[] { a.SessionId, a.SessionId }, new[] { b.SessionId, d.SessionId });
        Assert.Equal((1, Midia().Values.Count), ((await LerAsync(c, a.MessageId)).ChannelDataVersion, (await LerAsync(c, a.MessageId)).ChannelDataValues!.Count));
        Assert.Null((await LerAsync(c, b.MessageId)).ChannelData);
        var ultima = await LerAsync(c, d.MessageId);
        Assert.Equal(2, ultima.ChannelDataVersion);
        Assert.Equal(new Dictionary<string, string> { ["outro"] = "valor" }, ultima.ChannelDataValues);
    }

    [Fact]
    public async Task Valores_com_acentos_aspas_barra_emoji_e_vazio_e_os_limites_de_entradas_e_de_total_voltam_exatamente_iguais()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdfidelidade");
        var dificeis = new ChannelData(
            7,
            new Dictionary<string, string>
            {
                ["acentos"] = "João da Silva — «ação» ç ã é",
                ["aspas"] = "com \"aspas\" e 'apóstrofo'",
                ["barra"] = @"C:\temp\x e / normal",
                ["emoji"] = "😀👍🏽 你好 مرحبا Привет",
                ["html"] = "<b>&amp;</b> + = ? #",
                ["vazio"] = string.Empty,
                ["espacos"] = "   nas pontas   ",
            });
        var trintaEDuas = new ChannelData(1, Enumerable.Range(0, ConversationLimits.ChannelDataMaxEntries).ToDictionary(i => $"chave.{i:00}", i => $"valor {i} " + new string('v', 100)));
        var noLimiteDoTotal = new ChannelData(1, Enumerable.Range(0, 8).ToDictionary(i => $"k{i}", i => new string('x', i < 7 ? 1024 : 1008)));
        Assert.Equal(ConversationLimits.ChannelDataMaxTotalLength, noLimiteDoTotal.Values.Sum(p => p.Key.Length + p.Value.Length));

        var casos = new[] { dificeis, trintaEDuas, noLimiteDoTotal };
        for (var i = 0; i < casos.Length; i++)
        {
            var r = await IngerirAsync(c, Contato, $"evt-fidelidade-{i}", casos[i]);

            var volta = await LerAsync(c, r.MessageId);
            Assert.Equal(casos[i].Version, volta.ChannelDataVersion);
            Assert.Equal(casos[i].Values, volta.ChannelDataValues);
        }
    }

    // Idempotência e duplicatas

    [Fact]
    public async Task A_repeticao_do_evento_devolve_a_primeira_mensagem_e_nunca_sobrescreve_o_bloco()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdduplicata");
        var primeira = await IngerirAsync(c, Contato, "evt-dup", Midia(1));
        var eventosDepoisDaPrimeira = c.TotalDeEventosDoTenant;

        var repetida = await IngerirAsync(c, Contato, "evt-dup", new ChannelData(9, new Dictionary<string, string> { ["media.kind"] = "audio", ["extra"] = "x" }));

        Assert.Equal(IngestOutcome.Duplicate, repetida.Outcome);
        Assert.Equal(primeira.MessageId, repetida.MessageId);
        Assert.Equal(eventosDepoisDaPrimeira, c.TotalDeEventosDoTenant); // nenhum evento novo
        var guardada = await LerAsync(c, primeira.MessageId);
        Assert.Equal(1, guardada.ChannelDataVersion);
        Assert.Equal(Midia(1).Values, guardada.ChannelDataValues); // o da primeira gravação
        var total = await ConsultaAsync(c.ContextoDeSistema, db => db.Messages.CountAsync(m => m.ProviderEventId == "evt-dup"));
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Se_a_primeira_chegada_nao_tinha_bloco_a_repeticao_com_bloco_nao_o_acrescenta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdduplicatasem");
        var primeira = await IngerirAsync(c, Contato, "evt-dup2", null);

        var repetida = await IngerirAsync(c, Contato, "evt-dup2", Midia());

        Assert.Equal(IngestOutcome.Duplicate, repetida.Outcome);
        Assert.Null((await LerAsync(c, primeira.MessageId)).ChannelData);
    }

    [Fact]
    public async Task O_mesmo_evento_com_blocos_diferentes_chegando_em_paralelo_grava_uma_so_mensagem_com_o_bloco_de_quem_ganhou()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdparalelo");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i => (Indice: i, Resultado: await IngerirAsync(c, Contato, "evt-paralelo", Midia(i + 1)))));

        var vencedora = Assert.Single(resultados, r => r.Resultado.Outcome == IngestOutcome.Registered);
        Assert.All(resultados, r => Assert.Equal(vencedora.Resultado.MessageId, r.Resultado.MessageId));
        var guardada = await LerAsync(c, vencedora.Resultado.MessageId);
        Assert.Equal(vencedora.Indice + 1, guardada.ChannelDataVersion);
        Assert.Equal(Midia(vencedora.Indice + 1).Values, guardada.ChannelDataValues);
        Assert.Equal(1, await ConsultaAsync(c.ContextoDeSistema, db => db.Messages.CountAsync(m => m.ProviderEventId == "evt-paralelo")));
    }

    // Recusa sem efeito colateral

    [Fact]
    public async Task Um_bloco_invalido_recusa_a_ingestao_sem_gravar_contato_sessao_nem_mensagem()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdinvalido");
        var invalidos = new[]
        {
            new ChannelData(0, new Dictionary<string, string>()),
            new ChannelData(1, new Dictionary<string, string> { ["Chave Invalida"] = "v" }),
            new ChannelData(1, new Dictionary<string, string> { ["k"] = new string('v', ConversationLimits.ChannelDataValueMaxLength + 1) }),
            new ChannelData(1, new Dictionary<string, string> { ["k"] = "sozinho\uD83D" }),
        };

        foreach (var bloco in invalidos)
        {
            await Assert.ThrowsAsync<ConversationsValidationException>(() => IngerirAsync(c, Contato, "evt-invalido", bloco));
        }

        var (contatos, sessoes, mensagens) = await ConsultaAsync(c.ContextoDeSistema, async db => (await db.Contacts.CountAsync(), await db.Sessions.CountAsync(), await db.Messages.CountAsync()));
        Assert.Equal((0, 0, 0), (contatos, sessoes, mensagens));
    }

    // Isolamento entre tenants

    [Fact]
    public async Task O_bloco_de_um_tenant_nao_e_visivel_a_outro_nem_por_SQL_com_o_tenant_errado()
    {
        var a = await CenarioDeConversas.CriarAsync(banco, "cdisoa");
        var b = await CenarioDeConversas.CriarAsync(banco, "cdisob");
        var ra = await IngerirAsync(a, Contato, "evt-iso", new ChannelData(1, new Dictionary<string, string> { ["marca"] = "so-do-tenant-a" }));
        var rb = await IngerirAsync(b, Contato, "evt-iso", new ChannelData(2, new Dictionary<string, string> { ["marca"] = "so-do-tenant-b" }));

        // Cada tenant lê o seu.
        Assert.Equal("so-do-tenant-a", (await LerAsync(a, ra.MessageId)).ChannelDataValues!["marca"]);
        Assert.Equal("so-do-tenant-b", (await LerAsync(b, rb.MessageId)).ChannelDataValues!["marca"]);

        // O tenant B não enxerga a mensagem de A pelo EF (filtro global), nem por SQL cru que filtre pelo tenant dele.
        Assert.Equal(0, await ConsultaAsync(b.ContextoDeSistema, db => db.Messages.CountAsync(m => m.Id == ra.MessageId)));
        var cruzado = await ConsultaAsync(b.ContextoDeSistema, db => db.Database
            .SqlQueryRaw<int>("select count(*)::int as \"Value\" from conversations.messages where tenant_id = {0} and (id = {1} or channel_data::text like {2})", b.Tenant.TenantId.Value, ra.MessageId, "%so-do-tenant-a%")
            .SingleAsync());
        Assert.Equal(0, cruzado);
    }

    // A restrição do banco

    [Theory]
    [InlineData("channel_data_version = 1")] // versão sem pares
    [InlineData("channel_data = jsonb_build_object()")] // pares sem versão
    [InlineData("channel_data = jsonb_build_object(), channel_data_version = 0")] // versão menor que 1
    [InlineData("channel_data = jsonb_build_object(), channel_data_version = -3")]
    [InlineData("channel_data = jsonb_build_array(), channel_data_version = 1")] // pares que não são um objeto
    [InlineData("channel_data = to_jsonb('texto'::text), channel_data_version = 1")]
    public async Task O_banco_recusa_versao_sem_pares_pares_sem_versao_versao_invalida_e_pares_que_nao_sao_objeto(string alteracao)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdck");
        var r = await IngerirAsync(c, Contato, "evt-ck", null);

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, $"update conversations.messages set {alteracao} where tenant_id = {{0}} and id = {{1}}", c.Tenant.TenantId.Value, r.MessageId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, falha.SqlState);
        Assert.Equal("ck_messages_channel_data", falha.ConstraintName);
        Assert.Null((await LerAsync(c, r.MessageId)).ChannelData); // nada mudou
    }

    [Fact]
    public async Task O_banco_aceita_o_objeto_vazio_com_versao_um_e_o_bloco_com_pares_e_aceita_voltar_a_nenhum()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdckok");
        var r = await IngerirAsync(c, Contato, "evt-ckok", null);

        Assert.Equal(1, await SqlAsync(c.ContextoDeSistema, "update conversations.messages set channel_data = jsonb_build_object(), channel_data_version = 1 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.MessageId));
        var vazio = await LerAsync(c, r.MessageId);
        Assert.Equal((1, 0), (vazio.ChannelDataVersion, vazio.ChannelDataValues!.Count));

        Assert.Equal(1, await SqlAsync(c.ContextoDeSistema, "update conversations.messages set channel_data = jsonb_build_object('k', 'v'), channel_data_version = 2 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.MessageId));
        Assert.Equal("v", (await LerAsync(c, r.MessageId)).ChannelDataValues!["k"]);

        Assert.Equal(1, await SqlAsync(c.ContextoDeSistema, "update conversations.messages set channel_data = null, channel_data_version = null where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.MessageId));
        Assert.Null((await LerAsync(c, r.MessageId)).ChannelData);
    }

    // O catálogo do banco

    [Fact]
    public async Task O_banco_tem_as_duas_colunas_nulas_sem_padrao_e_a_restricao_do_bloco()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "cdcatalogo");

        var colunas = await ConsultaAsync(c.ContextoDeSistema, db => db.Database
            .SqlQueryRaw<string>(
                "select column_name || ' | ' || data_type || ' | ' || is_nullable || ' | ' || coalesce(column_default, 'sem padrao') as \"Value\" from information_schema.columns " +
                "where table_schema = 'conversations' and table_name = 'messages' and column_name in ('channel_data', 'channel_data_version') order by column_name")
            .ToListAsync());
        var restricoes = await ConsultaAsync(c.ContextoDeSistema, db => db.Database
            .SqlQueryRaw<string>("select conname as \"Value\" from pg_constraint where conname = {0}", "ck_messages_channel_data")
            .ToListAsync());

        Assert.Equal(new[] { "channel_data | jsonb | YES | sem padrao", "channel_data_version | integer | YES | sem padrao" }, colunas);
        Assert.Single(restricoes);
    }
}
