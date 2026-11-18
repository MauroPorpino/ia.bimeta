using System.Net;
using IaBimeta.IntegrationTests.Api;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E1-R, sexta rodada (quinta auditoria do Codex): (1) a auditoria de sucesso de um provisionamento JIT DESFEITO pela corrida não pode ser gravada depois; (2) desativar conexão ou vínculo confirma estado, marca de
/// revogação e auditoria JUNTOS, e a reativação nunca devolve a validade de uma sessão anterior; (3) uma falha REAL do repositório da trilha não contamina a entrega das entradas seguintes. API real, PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedSextaRodadaApiTests(PostgresFixture banco)
{
    private static async Task ExecutarAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> EscalarAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    private static Task<long> ConexaoAsync(Guid tenant, Guid conexao, string filtro) =>
        EscalarAsync($"SELECT count(*) FROM identity.integration_connections WHERE tenant_id = @t AND id = @c AND {filtro}", ("t", tenant), ("c", conexao));

    private static Task<long> VinculoAsync(Guid tenant, Guid vinculo, string filtro) =>
        EscalarAsync($"SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t AND id = @v AND {filtro}", ("t", tenant), ("v", vinculo));

    private static Task<long> SessaoAsync(Guid tenant, Guid sessao, string filtro) =>
        EscalarAsync($"SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND id = @s AND {filtro}", ("t", tenant), ("s", sessao));

    /// <summary>Instala, SÓ para o tenant do teste, um gatilho que faz o UPDATE indicado falhar de verdade no banco (falha determinística entre fases, sem mexer em código de produção).</summary>
    private static async Task<IAsyncDisposable> FalhaNoBancoAsync(string nome, string tabela, Guid tenant, string condicao)
    {
        await ExecutarAsync(
            $@"CREATE OR REPLACE FUNCTION identity.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE UPDATE ON identity.{tabela} FOR EACH ROW WHEN (OLD.tenant_id = '{tenant}'::uuid AND {condicao}) EXECUTE FUNCTION identity.fn_{nome}();");
        return new RemoverFalha(nome, tabela);
    }

    private sealed class RemoverFalha(string nome, string tabela) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await ExecutarAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON identity.{tabela}; DROP FUNCTION IF EXISTS identity.fn_{nome}();");
    }

    private const string MudaAMarca = "NEW.sessions_revoked_at IS DISTINCT FROM OLD.sessions_revoked_at";

    private const string RevogaSessao = "NEW.revoked_at IS NOT NULL AND OLD.revoked_at IS NULL";

    private static bool Sucesso(HttpStatusCode codigo) => (int)codigo is >= 200 and < 300;

    // ------------------------------------------------------------------ 1. JIT

    [Fact]
    public async Task Primeiras_emissoes_concorrentes_do_mesmo_usuario_externo_provisionam_uma_vez_e_auditam_uma_vez_sem_evento_da_tentativa_perdedora()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "sexta-jit-corrida");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId); // as da preparação (conexão e chave)
        var usuariosAntes = await EscalarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t", ("t", tenant));

        // COORDENAÇÃO: um gatilho atrasa em 1 s o INSERT do usuário federado (só deste tenant), então as duas emissões do mesmo usuário externo conferem "não existe vínculo" ANTES de qualquer uma gravar, e as duas
        // tentam o provisionamento de verdade. Uma sequência conta quantas tentativas começaram (ela não desfaz com o rollback, ao contrário de qualquer tabela).
        await ExecutarAsync("DROP SEQUENCE IF EXISTS identity.seq_teste_sexta_jit; CREATE SEQUENCE identity.seq_teste_sexta_jit MINVALUE 0 START 0");
        await ExecutarAsync(
            $@"CREATE OR REPLACE FUNCTION identity.fn_teste_sexta_jit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM nextval('identity.seq_teste_sexta_jit'); PERFORM pg_sleep(1.0); RETURN NEW; END $$;
               CREATE TRIGGER tg_teste_sexta_jit BEFORE INSERT ON identity.users FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}'::uuid) EXECUTE FUNCTION identity.fn_teste_sexta_jit();");
        const int Rodadas = 4;
        long tentativas;
        try
        {
            for (var rodada = 0; rodada < Rodadas; rodada++)
            {
                var externo = $"u-corrida-{rodada}";
                var emitidos = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => c.Erp.EmitirAsync(externo, EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier())))));
                Assert.All(emitidos, e => Assert.StartsWith("ibt_", e.Ticket));
            }

            tentativas = await EscalarAsync("SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM identity.seq_teste_sexta_jit");
        }
        finally
        {
            await ExecutarAsync("DROP TRIGGER IF EXISTS tg_teste_sexta_jit ON identity.users; DROP FUNCTION IF EXISTS identity.fn_teste_sexta_jit(); DROP SEQUENCE IF EXISTS identity.seq_teste_sexta_jit");
        }

        Assert.True(tentativas > Rodadas, $"nenhuma rodada teve tentativa perdedora (tentativas de provisionamento: {tentativas}): o teste não exercitou a corrida");

        // Um único provisionamento POR usuário externo: um vínculo, um usuário novo, uma concessão.
        Assert.Equal(Rodadas, await EscalarAsync("SELECT count(*) FROM identity.external_identities WHERE tenant_id = @t AND source = 'Jit'", ("t", tenant)));
        Assert.Equal(Rodadas, await EscalarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t", ("t", tenant)) - usuariosAntes);
        Assert.Equal(Rodadas, await EscalarAsync("SELECT count(*) FROM identity.user_organization_grants g WHERE g.tenant_id = @t AND g.user_id IN (SELECT user_id FROM identity.external_identities WHERE tenant_id = @t)", ("t", tenant)));

        // EXATAMENTE um evento de sucesso de provisionamento por criação efetiva, e dois tickets por rodada (as duas emissões deram certo). Nada de evento da tentativa perdedora.
        Assert.Equal($"identity.embed.identity.provision={Rodadas};identity.embed.ticket.issue={Rodadas * 2}", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));

        // Cada evento aponta para o usuário e o vínculo que EXISTEM (ator = usuário criado; recurso = vínculo criado). Um evento da perdedora apontaria para ids que nunca foram gravados.
        Assert.Equal(Rodadas, await EscalarAsync(
            @"SELECT count(*) FROM identity.audit_outbox o
              JOIN identity.external_identities i ON i.tenant_id = o.tenant_id AND i.id::text = o.resource_id AND i.user_id = o.actor_user_id
              JOIN identity.users u ON u.tenant_id = i.tenant_id AND u.id = i.user_id
              WHERE o.tenant_id = @t AND o.operation = 'identity.embed.identity.provision' AND o.resource_type = 'external_identity'", ("t", tenant)));
        Assert.Equal(0, await EscalarAsync(
            @"SELECT count(*) FROM identity.audit_outbox o
              WHERE o.tenant_id = @t AND o.operation = 'identity.embed.identity.provision'
                AND NOT EXISTS (SELECT 1 FROM identity.external_identities i WHERE i.tenant_id = o.tenant_id AND i.id::text = o.resource_id)", ("t", tenant)));

        // A entrega leva à trilha exatamente os mesmos eventos.
        Assert.Equal(Rodadas * 3, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        Assert.Equal($"identity.embed.connection.create=1;identity.embed.identity.provision={Rodadas};identity.embed.key.create=1;identity.embed.ticket.issue={Rodadas * 2}", await EmbeddedTestSupport.ResumoDaTrilhaAsync(tenant));
        Assert.Equal("", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
    }

    // ------------------------------------------------------------------ 2. Desativação atômica da CONEXÃO

    [Fact]
    public async Task Desativar_a_conexao_confirma_estado_marca_e_auditoria_juntos_e_a_falha_antes_do_commit_desfaz_tudo()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "sexta-con-atomica");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var (troca, _) = await c.EntrarAsync("u-con-atomica");
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        // A marca de revogação não consegue ser gravada: como ela agora vai na MESMA transação do estado e da auditoria, NADA fica. Antes, o estado e a auditoria confirmavam e só a marca falhava.
        await using (await FalhaNoBancoAsync("con_marca", "integration_connections", tenant, MudaAMarca))
        {
            Assert.False(Sucesso(await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/disable")));
        }

        Assert.Equal(1, await ConexaoAsync(tenant, c.Conexao.Id, "status = 'Active' AND sessions_revoked_at IS NULL"));
        Assert.Equal("", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));

        // Sem a falha, os três confirmam juntos e a sessão deixa de valer.
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/disable"));
        Assert.Equal(1, await ConexaoAsync(tenant, c.Conexao.Id, "status = 'Disabled' AND sessions_revoked_at IS NOT NULL"));
        Assert.Equal("identity.embed.connection.disable=1", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
        var linha = Assert.Single(await EmbeddedTestSupport.LinhasDaOutboxAsync(tenant, "identity.embed.connection.disable"));
        Assert.Equal((c.Admin.Id, "integration_connection", c.Conexao.Id.ToString()), (linha.Ator, linha.Tipo, linha.Recurso));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Falha_na_limpeza_fisica_da_conexao_nao_perde_a_revogacao_a_reativacao_nao_devolve_a_validade_e_repetir_conclui_a_limpeza()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "sexta-con-limpeza");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var (antiga, _) = await c.EntrarAsync("u-con-limpeza");
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        // A limpeza física (a revogação linha a linha das sessões) falha DEPOIS de a transação do estado ter confirmado.
        await using (await FalhaNoBancoAsync("con_limpeza", "sessions", tenant, RevogaSessao))
        {
            Assert.False(Sucesso(await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/disable")));
        }

        // Estado, marca e auditoria já estavam confirmados; só a linha da sessão ficou sem revogar fisicamente.
        Assert.Equal(1, await ConexaoAsync(tenant, c.Conexao.Id, "status = 'Disabled' AND sessions_revoked_at IS NOT NULL"));
        Assert.Equal("identity.embed.connection.disable=1", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
        Assert.Equal(1, await SessaoAsync(tenant, antiga.SessionId, "revoked_at IS NULL"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(antiga.Token, HttpMethod.Get, "/embed/v1/session"));

        // Reativar NUNCA devolve a validade: a sessão de antes da desativação continua recusada, e uma entrada nova vale.
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/enable"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(antiga.Token, HttpMethod.Get, "/embed/v1/session"));
        var (nova, _) = await c.EntrarAsync("u-con-limpeza-2");
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(nova.Token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(antiga.Token, HttpMethod.Get, "/embed/v1/session"));

        // Repetir a desativação conclui a limpeza física (idempotente) e grava a nova entrada da mesma ação.
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/disable"));
        Assert.Equal(1, await SessaoAsync(tenant, antiga.SessionId, "revoked_at IS NOT NULL"));
        Assert.Equal(1, await SessaoAsync(tenant, nova.SessionId, "revoked_at IS NOT NULL"));
        var resumo = await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant);
        Assert.Contains("identity.embed.connection.disable=2", resumo);
        Assert.Contains("identity.embed.connection.enable=1", resumo);
    }

    // ------------------------------------------------------------------ 3. Desativação atômica do VÍNCULO

    private static async Task<VinculoDto> VinculoDoAsync(EmbeddedCenario c, string externo) =>
        (await c.Api.LerAsync<List<VinculoDto>>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/identities")).Single(v => v.ExternalUserId == externo);

    [Fact]
    public async Task Desativar_o_vinculo_confirma_estado_marca_e_auditoria_juntos_e_a_falha_antes_do_commit_desfaz_tudo()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "sexta-vin-atomica");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var (troca, _) = await c.EntrarAsync("u-vin-atomica");
        var vinculo = await VinculoDoAsync(c, "u-vin-atomica");
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        await using (await FalhaNoBancoAsync("vin_marca", "external_identities", tenant, MudaAMarca))
        {
            Assert.False(Sucesso(await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{vinculo.Id}/disable")));
        }

        // Antes, o estado e a auditoria confirmavam primeiro: o vínculo ficava Disabled sem marca, e a reativação devolvia a validade às sessões anteriores.
        Assert.Equal(1, await VinculoAsync(tenant, vinculo.Id, "status = 'Active' AND sessions_revoked_at IS NULL"));
        Assert.Equal("", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{vinculo.Id}/disable"));
        Assert.Equal(1, await VinculoAsync(tenant, vinculo.Id, "status = 'Disabled' AND sessions_revoked_at IS NOT NULL"));
        Assert.Equal("identity.embed.identity.status=1", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
        var linha = Assert.Single(await EmbeddedTestSupport.LinhasDaOutboxAsync(tenant, "identity.embed.identity.status"));
        Assert.Equal((c.Admin.Id, "external_identity", vinculo.Id.ToString()), (linha.Ator, linha.Tipo, linha.Recurso));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));

        // Reativar não devolve a validade da sessão antiga, e o usuário volta a entrar (sessão nova).
        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{vinculo.Id}/enable"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(troca.Token, HttpMethod.Get, "/embed/v1/session"));
        var (nova, _) = await c.EntrarAsync("u-vin-atomica");
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(nova.Token, HttpMethod.Get, "/embed/v1/session"));
    }

    [Fact]
    public async Task Falha_na_limpeza_fisica_do_vinculo_nao_perde_a_revogacao_e_a_reativacao_nao_devolve_a_validade()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "sexta-vin-limpeza");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var (antiga, _) = await c.EntrarAsync("u-vin-limpeza");
        var vinculo = await VinculoDoAsync(c, "u-vin-limpeza");
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        await using (await FalhaNoBancoAsync("vin_limpeza", "sessions", tenant, RevogaSessao))
        {
            Assert.False(Sucesso(await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{vinculo.Id}/disable")));
        }

        Assert.Equal(1, await VinculoAsync(tenant, vinculo.Id, "status = 'Disabled' AND sessions_revoked_at IS NOT NULL"));
        Assert.Equal("identity.embed.identity.status=1", await EmbeddedTestSupport.ResumoDaOutboxAsync(tenant));
        Assert.Equal(1, await SessaoAsync(tenant, antiga.SessionId, "revoked_at IS NULL"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(antiga.Token, HttpMethod.Get, "/embed/v1/session"));

        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{vinculo.Id}/enable"));
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(antiga.Token, HttpMethod.Get, "/embed/v1/session"));

        // Repetir a desativação conclui a limpeza física.
        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/identities/{vinculo.Id}/disable"));
        Assert.Equal(1, await SessaoAsync(tenant, antiga.SessionId, "revoked_at IS NOT NULL"));
    }

    // ------------------------------------------------------------------ 4. Contaminação do contexto da trilha

    private static Task InserirNaOutboxAsync(Guid tenant, string recurso, int minutosAtras) => ExecutarAsync(
        @"INSERT INTO identity.audit_outbox (tenant_id, id, operation, resource_type, resource_id, outcome, actor_user_id, actor_origin, created_at)
          VALUES (@t, @i, 'identity.embed.connection.update', 'integration_connection', @r, 1, NULL, 'authentication', now() - make_interval(mins => @m))",
        ("t", tenant), ("i", Guid.CreateVersion7()), ("r", recurso), ("m", minutosAtras));

    [Fact]
    public async Task Falha_real_de_persistencia_no_repositorio_da_trilha_nao_contamina_a_entrega_das_entradas_seguintes()
    {
        // SEM decoradores: o escritor, o repositório e o contexto da trilha são os de verdade. A falha é do BANCO (restrição que recusa um recurso), não uma exceção lançada antes de chamar o escritor.
        var c = await EmbeddedCenario.CriarAsync(banco, "sexta-trilha-real");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        await InserirNaOutboxAsync(tenant, "poison", 30); // a mais antiga: é a primeira da fila
        await InserirNaOutboxAsync(tenant, "ok-1", 20);
        await InserirNaOutboxAsync(tenant, "ok-2", 10);

        await ExecutarAsync("ALTER TABLE audit.audit_events ADD CONSTRAINT ck_teste_trilha_real CHECK (resource_id IS DISTINCT FROM 'poison') NOT VALID");
        try
        {
            // UM mesmo contexto da trilha atende o lote inteiro. Se o evento recusado continuasse rastreado, o SaveChanges de ok-1 tentaria gravá-lo de novo e falharia, e nenhuma seguinte seria entregue.
            Assert.Equal(2, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        }
        finally
        {
            await ExecutarAsync("ALTER TABLE audit.audit_events DROP CONSTRAINT ck_teste_trilha_real");
        }

        // A trilha tem só as duas boas (nada do evento recusado, nem repetido); a problemática ficou na outbox com a falha REAL registrada (só o tipo).
        Assert.Equal(2, await EscalarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation = 'identity.embed.connection.update'", ("t", tenant)));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND resource_id = 'poison'", ("t", tenant)));
        Assert.Equal(2, await EscalarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND resource_id IN ('ok-1', 'ok-2')", ("t", tenant)));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant)));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t AND resource_id = 'poison' AND attempts = 1 AND last_error = 'DbUpdateException' AND next_attempt_at > now()", ("t", tenant)));

        // O problema some: a MESMA linha é entregue (uma vez) e só então apagada. Nada se perdeu em silêncio.
        await ExecutarAsync("UPDATE identity.audit_outbox SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @t", ("t", tenant));
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND resource_id = 'poison'", ("t", tenant)));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant)));
    }
}
