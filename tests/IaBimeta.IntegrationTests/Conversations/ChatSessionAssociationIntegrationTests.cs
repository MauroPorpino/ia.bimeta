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
/// Fatia A2 com PostgreSQL real, DEPOIS de a migração <c>ChatSessionAssociationAndLimits</c> ser aplicada (a <c>PostgresFixture</c> a aplica ao iniciar): o índice único
/// parcial do chat no lugar do antigo, a restrição estrita de associação, os valores padrão das colunas novas, os limites alargados ponta a ponta e a recusa do modo de
/// associação que não existe. Os testes do MODELO, que não usam banco, estão em <see cref="ChatSessionModelTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChatSessionAssociationIntegrationTests(PostgresFixture banco)
{
    private const string Contato = "5511999990001";

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

    private Task<List<string>> TextosAsync(RequestContext contexto, string sql, params object[] parametros) =>
        ConsultaAsync(contexto, db => db.Database.SqlQueryRaw<string>(sql, parametros).ToListAsync());

    // O catálogo do banco

    [Fact]
    public async Task O_banco_tem_o_indice_unico_parcial_do_chat_e_nao_tem_mais_o_indice_antigo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "idxchat");

        var novo = await TextosAsync(c.ContextoDeSistema, "select indexdef as \"Value\" from pg_indexes where schemaname = 'conversations' and indexname = {0}", "ux_service_sessions_open_chat");
        var antigo = await TextosAsync(c.ContextoDeSistema, "select indexdef as \"Value\" from pg_indexes where schemaname = 'conversations' and indexname = {0}", "ix_service_sessions_one_open_per_contact");

        var definicao = Assert.Single(novo);
        Assert.Contains("CREATE UNIQUE INDEX", definicao);
        Assert.Contains("(tenant_id, contact_id)", definicao);
        Assert.Contains("status = 1", definicao);
        Assert.Contains("thread_key IS NULL", definicao);
        Assert.Empty(antigo);
    }

    [Theory]
    [InlineData("contacts", "external_ref", 254)]
    [InlineData("messages", "provider_event_id", 512)]
    public async Task As_colunas_alargadas_tem_o_tamanho_novo_no_banco(string tabela, string coluna, int esperado)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "tamanho" + coluna[..4]);

        var tamanho = await ConsultaAsync(c.ContextoDeSistema, db => db.Database
            .SqlQueryRaw<int>("select character_maximum_length as \"Value\" from information_schema.columns where table_schema = 'conversations' and table_name = {0} and column_name = {1}", tabela, coluna)
            .SingleAsync());

        Assert.Equal(esperado, tamanho);
    }

    [Fact]
    public async Task A_restricao_de_associacao_existe_e_as_colunas_novas_tem_padrao_de_chat()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "colassoc");

        var restricoes = await TextosAsync(c.ContextoDeSistema, "select conname as \"Value\" from pg_constraint where conname = {0}", "ck_service_sessions_association");
        var colunas = await TextosAsync(
            c.ContextoDeSistema,
            "select column_name || '=' || coalesce(column_default, 'sem padrao') || '/' || is_nullable as \"Value\" from information_schema.columns " +
            "where table_schema = 'conversations' and table_name = 'service_sessions' and column_name in ('association_mode', 'thread_key') order by column_name");

        Assert.Single(restricoes);
        Assert.Equal(new[] { "association_mode=1/NO", "thread_key=sem padrao/YES" }, colunas);
    }

    // O comportamento

    [Fact]
    public async Task A_sessao_criada_pela_ingestao_e_de_chat_sem_chave_de_thread()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "sessaochat");

        var r = await c.EntrarAsync(Contato);

        var (modo, chave) = await ConsultaAsync(c.ContextoDeSistema, async db =>
        {
            var sessao = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == r.SessionId);
            return (sessao.AssociationMode, sessao.ThreadKey);
        });
        Assert.Equal(AssociationMode.Contact, modo);
        Assert.Null(chave);
    }

    [Fact]
    public async Task Uma_linha_inserida_sem_as_colunas_novas_recebe_o_padrao_de_chat()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "padraochat");
        var r = await c.EntrarAsync(Contato);
        var contato = await ConsultaAsync(c.ContextoDeSistema, db => db.Sessions.Where(s => s.Id == r.SessionId).Select(s => s.ContactId).SingleAsync());

        // Encerra a sessão para abrir espaço no índice e insere outra sem informar association_mode nem thread_key (como todo código anterior à A2 inseria).
        await SqlAsync(c.ContextoDeSistema, "update conversations.service_sessions set status = 2 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, r.SessionId);
        var nova = Guid.NewGuid();
        await SqlAsync(
            c.ContextoDeSistema,
            "insert into conversations.service_sessions (tenant_id, id, organization_id, contact_id, channel_instance_id, status, handling_mode, state_version, last_message_seq, last_inbound_seq, answered_through_seq, turn_epoch, last_lease_token, reply_delivery_failed, created_at, last_message_at) " +
            "values ({0}, {1}, {2}, {3}, {4}, 1, 1, 1, 0, 0, 0, 0, 0, false, now(), now())",
            c.Tenant.TenantId.Value, nova, c.OrganizacaoId, contato, c.CanalId.Value);

        var (modo, chave) = await ConsultaAsync(c.ContextoDeSistema, async db =>
        {
            var sessao = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == nova);
            return (sessao.AssociationMode, sessao.ThreadKey);
        });
        Assert.Equal(AssociationMode.Contact, modo);
        Assert.Null(chave);
    }

    [Theory]
    [InlineData("association_mode = 2")]
    [InlineData("association_mode = 0")]
    [InlineData("thread_key = 'thread-1'")]
    [InlineData("association_mode = 2, thread_key = 'thread-1'")]
    public async Task O_banco_recusa_modo_de_associacao_que_nao_e_chat_e_chave_de_thread_preenchida(string alteracao)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ckassoc");
        var r = await c.EntrarAsync(Contato);

        var falha = await Assert.ThrowsAsync<PostgresException>(() =>
            SqlAsync(c.ContextoDeSistema, $"update conversations.service_sessions set {alteracao} where tenant_id = {{0}} and id = {{1}}", c.Tenant.TenantId.Value, r.SessionId));

        Assert.Equal(PostgresErrorCodes.CheckViolation, falha.SqlState);
        Assert.Equal("ck_service_sessions_association", falha.ConstraintName);
    }

    [Fact]
    public async Task O_banco_recusa_segunda_sessao_aberta_de_chat_pelo_indice_novo_e_aceita_uma_nova_depois_de_encerrar_a_anterior()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "umachat");
        var primeira = await c.EntrarAsync(Contato);
        var contato = await ConsultaAsync(c.ContextoDeSistema, db => db.Sessions.Where(s => s.Id == primeira.SessionId).Select(s => s.ContactId).SingleAsync());

        // Aberta e aberta: o índice novo recusa, e é ele quem aparece na violação.
        var falha = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(
            c.ContextoDeSistema,
            "insert into conversations.service_sessions (tenant_id, id, organization_id, contact_id, channel_instance_id, status, handling_mode, state_version, last_message_seq, last_inbound_seq, answered_through_seq, turn_epoch, last_lease_token, reply_delivery_failed, created_at, last_message_at) " +
            "values ({0}, {1}, {2}, {3}, {4}, 1, 1, 1, 0, 0, 0, 0, 0, false, now(), now())",
            c.Tenant.TenantId.Value, Guid.NewGuid(), c.OrganizacaoId, contato, c.CanalId.Value));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, falha.SqlState);
        Assert.Equal("ux_service_sessions_open_chat", falha.ConstraintName);

        // O predicado só vale para a sessão aberta: encerrada a primeira, a mesma pessoa abre outra pela ingestão (o ON CONFLICT novo cria, e não engole).
        await SqlAsync(c.ContextoDeSistema, "update conversations.service_sessions set status = 2 where tenant_id = {0} and id = {1}", c.Tenant.TenantId.Value, primeira.SessionId);
        var segunda = await c.EntrarAsync(Contato);

        Assert.Equal(IngestOutcome.Registered, segunda.Outcome);
        Assert.NotEqual(primeira.SessionId, segunda.SessionId);
        var abertas = await ConsultaAsync(c.ContextoDeSistema, db => db.Sessions.CountAsync(s => s.ContactId == contato && s.Status == SessionStatus.Open));
        Assert.Equal(1, abertas);
    }

    [Fact]
    public async Task A_mesma_pessoa_continua_com_uma_so_sessao_aberta_e_o_mesmo_contato_para_mensagens_seguidas()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "seguidas");

        var a = await c.EntrarAsync(Contato);
        var b = await c.EntrarAsync(Contato);

        // O comportamento de antes da A2: a segunda mensagem reaproveita a sessão aberta (o ON CONFLICT com o predicado novo não duplica nem recusa).
        Assert.Equal(a.SessionId, b.SessionId);
        Assert.Equal((1L, 2L), (a.InboundSeq, b.InboundSeq));
    }

    // Limites alargados, ponta a ponta

    [Fact]
    public async Task Referencia_de_254_caracteres_e_evento_de_512_sao_gravados_inteiros_e_a_idempotencia_segue_valendo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "limites");
        var referencia = new string('9', ConversationLimits.ContactRefMaxLength);
        var evento = new string('e', ConversationLimits.ProviderEventIdMaxLength);

        var primeira = await c.EntrarAsync(referencia, evento: evento);
        var repetida = await c.EntrarAsync(referencia, evento: evento);

        Assert.Equal(IngestOutcome.Registered, primeira.Outcome);
        Assert.Equal(IngestOutcome.Duplicate, repetida.Outcome);
        Assert.Equal(primeira.MessageId, repetida.MessageId);

        var gravados = await ConsultaAsync(c.ContextoDeSistema, async db => (
            await db.Contacts.AsNoTracking().Select(x => x.ExternalRef.Length).SingleAsync(),
            await db.Messages.AsNoTracking().Where(m => m.Id == primeira.MessageId).Select(m => m.ProviderEventId!.Length).SingleAsync()));
        Assert.Equal((254, 512), gravados);
    }

    [Fact]
    public async Task Referencia_e_evento_acima_do_limite_novo_sao_recusados_sem_gravar_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "acimalimite");

        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(new string('9', ConversationLimits.ContactRefMaxLength + 1)));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => c.EntrarAsync(Contato, evento: new string('e', ConversationLimits.ProviderEventIdMaxLength + 1)));

        var contatos = await ConsultaAsync(c.ContextoDeSistema, db => db.Contacts.CountAsync());
        Assert.Equal(0, contatos);
    }

    [Fact]
    public async Task O_modo_de_associacao_que_nao_existe_e_recusado_sem_gravar_contato_nem_sessao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "assocrecusada");
        await using var escopo = banco.NovoEscopo(c.ContextoDeSistema);
        var ingestao = escopo.ServiceProvider.GetRequiredService<IConversationIngestion>();

        var comando = new IngestInboundMessageCommand(
            c.CanalId, Contato, null, c.OrganizacaoId, null, "evt-assoc-invalida", DateTimeOffset.UtcNow, MessageKind.Text, "oi", AssociationMode: (AssociationMode)2);
        await Assert.ThrowsAsync<ConversationsValidationException>(() => ingestao.IngestInboundMessageAsync(comando));

        var (contatos, sessoes) = await ConsultaAsync(c.ContextoDeSistema, async db => (await db.Contacts.CountAsync(), await db.Sessions.CountAsync()));
        Assert.Equal((0, 0), (contatos, sessoes));
    }
}
