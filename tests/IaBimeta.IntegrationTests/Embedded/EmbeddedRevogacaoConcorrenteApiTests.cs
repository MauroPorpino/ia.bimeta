using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IaBimeta.IntegrationTests.Api;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E1-R, segunda rodada (achados da 2.ª auditoria do Codex): ticket de contexto nascido durante a revogação da chave, deadlock entre logout e revogação de chave, e contenção do banco sob muitos resgates. API real,
/// PostgreSQL real. O que cada teste descreve é a falha que existia.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedRevogacaoConcorrenteApiTests(PostgresFixture banco)
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

    [Fact]
    public async Task Ticket_de_contexto_de_chave_revogada_nao_e_resgatado_mesmo_que_a_sessao_seja_de_outra_chave_e_o_ticket_tenha_nascido_depois_da_revogacao()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "ctx-chave-revogada");
        await using var servidor = c.Api;
        var segunda = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
        var erpB = new ErpAssinante(c.Api, segunda.KeyId, segunda.Secret);

        // A sessão é aberta com a chave A; o ticket de contexto é emitido pela chave B. A revogação de B não derruba a sessão (ela é de A).
        var (sessao, _) = await c.EntrarAsync("u-ctx-chave");
        var doB = await erpB.EmitirContextoAsync("u-ctx-chave", "CLI-B", sessao.SessionId);
        var doA = await c.Erp.EmitirContextoAsync("u-ctx-chave", "CLI-A", sessao.SessionId);
        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{segunda.KeyId}/revoke"));

        // O estado que a corrida produz: o ticket gravado DEPOIS do instantâneo da revogação fica pendente (a revogação já o consumiu; aqui o devolvemos a pendente, como se tivesse nascido depois dela).
        await ExecutarAsync("UPDATE identity.launch_tickets SET used_at = NULL WHERE tenant_id = @t AND kind = 'Context'", ("t", c.Tenant.TenantId.Value));

        // Antes: o resgate não conferia a chave do ticket e aceitava (200). Agora recusa.
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = doB.Ticket }));
        Assert.Empty((await c.Api.LerAsync<SessaoEmbutidaDto>(sessao.Token, "/embed/v1/session")).CustomerRefs);

        // O ticket da chave que segue ativa continua valendo (controle).
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = doA.Ticket }));
    }

    [Fact]
    public async Task Emissoes_de_contexto_concorrentes_com_a_revogacao_da_chave_nunca_deixam_um_ticket_resgatavel()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "emite-revoga");
        await using var servidor = c.Api;
        var (sessao, _) = await c.EntrarAsync("u-emite");

        for (var rodada = 0; rodada < 5; rodada++)
        {
            var nova = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
            var erpB = new ErpAssinante(c.Api, nova.KeyId, nova.Secret);

            var emissoes = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                using var resposta = await erpB.EnviarAsync("/embed/v1/context-tickets", new { externalUserId = "u-emite", sessionId = sessao.SessionId, customer = new { @ref = $"CLI-{rodada}-{i}" } });
                Assert.True(resposta.StatusCode is HttpStatusCode.Created or HttpStatusCode.Unauthorized, $"emissão respondeu {(int)resposta.StatusCode}");
                return resposta.StatusCode == HttpStatusCode.Created ? (await resposta.Content.ReadFromJsonAsync<TicketDto>())!.Ticket : null;
            })).ToList();
            var revogacao = Task.Run(async () =>
                Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{nova.KeyId}/revoke")));
            await Task.WhenAll(emissoes.Cast<Task>().Append(revogacao));

            // Terminada a revogação, NENHUM ticket que a chave emitiu (inclusive os que nasceram durante ela) pode ser resgatado.
            foreach (var ticket in (await Task.WhenAll(emissoes)).OfType<string>())
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket }));
            }
        }

        Assert.Empty((await c.Api.LerAsync<SessaoEmbutidaDto>(sessao.Token, "/embed/v1/session")).CustomerRefs);
    }

    [Fact]
    public async Task Logout_e_revogacao_de_chave_concorrentes_com_sessoes_e_tickets_em_comum_nao_dao_deadlock_nem_500()
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        var apiComLog = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(), null, s => s.AddLogging(b => b.AddProvider(new CapturaDeErros(bag))));
        var c = await EmbeddedCenario.CriarAsync(banco, "deadlock-revoga", api: apiComLog);
        await using var servidor = c.Api;

        for (var rodada = 0; rodada < 12; rodada++)
        {
            var nova = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
            var erpB = new ErpAssinante(c.Api, nova.KeyId, nova.Secret);

            // Sessões e tickets pendentes do mesmo usuário, todos emitidos pela chave que vai ser revogada: linhas em comum entre as duas operações.
            var sessoes = new List<TrocaDto>();
            for (var i = 0; i < 3; i++)
            {
                var verifier = EmbeddedTestSupport.NovoVerifier();
                var ticket = await erpB.EmitirAsync("u-dl", EmbeddedTestSupport.Challenge(verifier));
                sessoes.Add(await EmbeddedTestSupport.TrocarAsync(c.Api, ticket.Ticket, verifier));
            }

            for (var i = 0; i < 3; i++)
            {
                _ = await erpB.EmitirAsync("u-dl", EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()));
                _ = await erpB.EmitirContextoAsync("u-dl", $"CLI-{i}", sessoes[0].SessionId);
            }

            var operacoes = new List<Task<HttpStatusCode>>
            {
                c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{nova.KeyId}/revoke"),
                Task.Run(async () => { using var r = await c.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "u-dl" }); return r.StatusCode; }),
                Task.Run(async () => { using var r = await c.Erp.EnviarAsync("/embed/v1/sessions/revoke", new { externalUserId = "u-dl" }); return r.StatusCode; }),
                c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/sessions/revoke", new { }),
            };
            var estados = await Task.WhenAll(operacoes);

            // Antes: o logout (tickets e depois sessões) e a revogação de chave (sessões e depois tickets) travavam em ordem oposta e o PostgreSQL abortava uma delas (40P01 => 500).
            Assert.True(
                estados.All(e => e is HttpStatusCode.OK or HttpStatusCode.NoContent),
                $"rodada {rodada}: {string.Join(",", estados.Select(e => (int)e))}. Erros do servidor: " + string.Join(" ---- ", bag.Where(x => !x.TrimEnd().EndsWith("::")).Take(3)));
            foreach (var sessao in sessoes)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(sessao.Token, HttpMethod.Get, "/embed/v1/session"));
            }
        }

        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t AND used_at IS NULL AND key_id IN (SELECT id FROM identity.integration_keys WHERE tenant_id = @t AND status = 'Revoked')", ("t", c.Tenant.TenantId.Value)));
    }

    [Fact]
    public async Task Resgates_de_contexto_em_rajada_na_mesma_sessao_nunca_dao_500_e_o_que_nao_cabe_vira_503()
    {
        // 60 resgates simultâneos na MESMA sessão (todos esperam a trava da linha), no pool padrão dos testes: cada um é 200 ou 503 controlado, nunca 500, e o teto de 20 clientes em foco vale.
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        var apiComLog = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(), null, s => s.AddLogging(b => b.AddProvider(new CapturaDeErros(bag))));
        var c = await EmbeddedCenario.CriarAsync(banco, "pool-resgates", api: apiComLog);
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-pool");
        var tickets = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            tickets.Add((await c.Erp.EmitirContextoAsync("u-pool", $"CLI-{i:00}", troca.SessionId)).Ticket);
        }

        var estados = await Task.WhenAll(tickets.Select(t => c.Api.StatusAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = t })));

        Assert.True(estados.All(e => e is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable), $"estados: {string.Join(",", estados.Select(e => (int)e).Distinct())}. Erros do servidor: " + string.Join(" ---- ", bag.Where(x => !x.TrimEnd().EndsWith("::")).Take(3)));
        var aplicados = estados.Count(e => e == HttpStatusCode.OK);
        Assert.Equal(Math.Min(20, aplicados), await EscalarAsync("SELECT count(*) FROM identity.session_customer_grants WHERE tenant_id = @t AND session_id = @s", ("t", c.Tenant.TenantId.Value), ("s", troca.SessionId)));
    }

    [Fact]
    public async Task Trava_presa_numa_sessao_vira_503_com_Retry_After_depois_do_tempo_e_nunca_pendura_nem_vira_500()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "lock-timeout", configurar: k => k["Identity:EmbeddedLockTimeout"] = "00:00:01");
        await using var servidor = c.Api;
        var (troca, _) = await c.EntrarAsync("u-lock");
        var ticket = await c.Erp.EmitirContextoAsync("u-lock", "CLI-1", troca.SessionId);

        // Outra transação segura a linha da sessão (como um resgate lento da mesma sessão).
        await using var presa = new NpgsqlConnection(TestDatabase.ConnectionString);
        await presa.OpenAsync();
        await using var transacao = await presa.BeginTransactionAsync();
        await using (var trava = new NpgsqlCommand("SELECT 1 FROM identity.sessions WHERE tenant_id = @t AND id = @s FOR UPDATE", presa, transacao))
        {
            trava.Parameters.AddWithValue("t", c.Tenant.TenantId.Value);
            trava.Parameters.AddWithValue("s", troca.SessionId);
            await trava.ExecuteNonQueryAsync();
        }

        using var cancelamento = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var resposta = await c.Api.EnviarAsync(troca.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticket.Ticket });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.True(resposta.Headers.RetryAfter is not null, "falta o Retry-After");
        Assert.Equal("embedded-busy", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
        await transacao.RollbackAsync();
    }
}
