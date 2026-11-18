using System.Net;
using System.Net.Http.Json;
using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.IntegrationTests.Api;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>Liga e desliga a falha transitória da trilha de auditoria (deadlock 40P01) nos testes do outbox.</summary>
internal sealed class FalhaDeAuditoria
{
    public volatile bool Ativa;
}

/// <summary>O escritor da trilha de verdade, mas que falha de forma transitória enquanto <see cref="FalhaDeAuditoria.Ativa"/>.</summary>
internal sealed class AuditoriaQueFalha(AuditWriter real, FalhaDeAuditoria falha) : IAuditWriter, IAuditRecoveryWriter
{
    private static PostgresException Falha() => new("deadlock detectado", "ERROR", "ERROR", "40P01");

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
        falha.Ativa ? throw Falha() : real.WriteAsync(entry, cancellationToken);

    public Task WriteForAuthenticationAsync(IaBimeta.SharedKernel.TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) =>
        falha.Ativa ? throw Falha() : real.WriteForAuthenticationAsync(tenantId, actorUserId, entry, cancellationToken);

    public Task WriteRecoveredAsync(AuditEntry entry, AuditProvenance provenance, CancellationToken cancellationToken = default) =>
        falha.Ativa ? throw Falha() : real.WriteRecoveredAsync(entry, provenance, cancellationToken);
}

/// <summary>
/// E1-R, quarta rodada (auditoria final do Codex): a auditoria do sucesso da troca e do resgate de contexto é guardada na MESMA transação que consome o ticket (<c>identity.audit_outbox</c>) e entregue depois pelo Worker.
/// Antes, ela era gravada DEPOIS do commit, em outro módulo: uma falha transitória ali virava 503 com o ticket já consumido (a repetição respondia 401). API real, PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedAuditoriaDuravelApiTests(PostgresFixture banco)
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

    private sealed record LinhaOutbox(Guid Id, string Operation, string ResourceType, string? ResourceId, short Outcome, Guid? ActorUserId, string ActorOrigin, DateTimeOffset CreatedAt);

    private static async Task<LinhaOutbox> LerLinhaAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT id, operation, resource_type, resource_id, outcome, actor_user_id, actor_origin, created_at FROM identity.audit_outbox WHERE tenant_id = @t", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync());
        return new LinhaOutbox(
            leitor.GetGuid(0), leitor.GetString(1), leitor.GetString(2), leitor.IsDBNull(3) ? null : leitor.GetString(3), leitor.GetInt16(4),
            leitor.IsDBNull(5) ? null : leitor.GetGuid(5), leitor.GetString(6), leitor.GetFieldValue<DateTimeOffset>(7));
    }

    private static Task ReinserirLinhaAsync(Guid tenant, LinhaOutbox l) => ExecutarAsync(
        "INSERT INTO identity.audit_outbox (tenant_id, id, operation, resource_type, resource_id, outcome, actor_user_id, actor_origin, created_at) VALUES (@t, @i, @o, @rt, @ri, @oc, @u, @ao, @c)",
        ("t", tenant), ("i", l.Id), ("o", l.Operation), ("rt", l.ResourceType), ("ri", l.ResourceId), ("oc", l.Outcome), ("u", l.ActorUserId), ("ao", l.ActorOrigin), ("c", l.CreatedAt));

    private static Task<long> NoOutboxAsync(Guid tenant) => EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant));

    private static Task<long> NaTrilhaAsync(Guid tenant, string operacao, string resultado = "Success") =>
        EscalarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation = @o AND outcome = @r", ("t", tenant), ("o", operacao), ("r", (short)(resultado == "Success" ? 1 : 2)));

    private async Task<(EmbeddedCenario Cenario, FalhaDeAuditoria Falha)> CenarioComFalhaAsync(string slug, Action<Dictionary<string, string?>>? configurar = null)
    {
        var falha = new FalhaDeAuditoria();
        var api = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(configurar), null, s =>
        {
            s.AddSingleton(falha);
            s.AddScoped<AuditWriter>();
            s.AddScoped<IAuditWriter, AuditoriaQueFalha>();
            s.AddScoped<IAuditRecoveryWriter, AuditoriaQueFalha>();
        });
        return (await EmbeddedCenario.CriarAsync(banco, slug, api: api), falha);
    }

    [Fact]
    public async Task Falha_transitoria_da_auditoria_depois_da_operacao_nao_muda_a_resposta_da_troca_e_a_entrega_acontece_depois_sem_duplicar()
    {
        var (c, falha) = await CenarioComFalhaAsync("audit-troca");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await c.Erp.EmitirAsync("u-aud", EmbeddedTestSupport.Challenge(verifier));
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId); // as da preparação (conexão, chave, emissão): fica só a da troca

        // A trilha cai DEPOIS da emissão. Antes: o sucesso da troca era auditado depois do commit, a exceção virava 503 e o ticket (já consumido) respondia 401 na repetição.
        falha.Ativa = true;
        var troca = await EmbeddedTestSupport.TrocarAsync(c.Api, ticket.Ticket, verifier);

        Assert.StartsWith("ibe_", troca.Token);
        Assert.Equal(1, await NoOutboxAsync(tenant));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND id = @s", ("t", tenant), ("s", troca.SessionId)));
        Assert.Equal(0, await NaTrilhaAsync(tenant, "identity.embed.exchange"));

        // Com a trilha ainda fora do ar, a entrega não apaga a linha (só depois da confirmação).
        Assert.Equal(0, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        Assert.Equal(1, await NoOutboxAsync(tenant));

        // A trilha volta: a entrega grava UM evento, com o ator, a origem e o instante ORIGINAIS, e só então apaga a linha.
        falha.Ativa = false;
        await ExecutarAsync("UPDATE identity.audit_outbox SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @t", ("t", tenant)); // vence o recuo da tentativa que falhou
        var instanteOriginal = await EscalarAsync("SELECT (extract(epoch FROM created_at) * 1000000)::bigint FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant));
        var linha = await LerLinhaAsync(tenant);
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        Assert.Equal(0, await NoOutboxAsync(tenant));
        Assert.Equal(1, await NaTrilhaAsync(tenant, "identity.embed.exchange"));
        Assert.Equal(1, await EscalarAsync(
            "SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation = 'identity.embed.exchange' AND actor_origin = 'authentication' AND actor_user_id = @u AND resource_id = @s AND (extract(epoch FROM occurred_at) * 1000000)::bigint = @i",
            ("t", tenant), ("u", linha.ActorUserId), ("s", troca.SessionId.ToString()), ("i", instanteOriginal)));

        // Entrega repetida (o processo caiu entre gravar na trilha e apagar a linha): a mesma linha de novo NÃO duplica o evento.
        await ReinserirLinhaAsync(tenant, linha);
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        Assert.Equal(1, await NaTrilhaAsync(tenant, "identity.embed.exchange"));
        Assert.Equal(0, await NoOutboxAsync(tenant));
    }

    [Fact]
    public async Task Falha_transitoria_da_auditoria_depois_do_resgate_de_contexto_nao_muda_a_resposta_e_o_ticket_segue_de_uso_unico()
    {
        var (c, falha) = await CenarioComFalhaAsync("audit-ctx");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var (sessao, _) = await c.EntrarAsync("u-aud-ctx");
        var ticket = await c.Erp.EmitirContextoAsync("u-aud-ctx", "CLI-1", sessao.SessionId);
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId); // a da troca anterior

        falha.Ativa = true;
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticket.Ticket }));

        Assert.Equal(1, await NoOutboxAsync(tenant));
        Assert.Equal(new[] { "CLI-1" }, (await c.Api.LerAsync<SessaoEmbutidaDto>(sessao.Token, "/embed/v1/session")).CustomerRefs);
        Assert.Equal(HttpStatusCode.Unauthorized, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticket.Ticket }));

        falha.Ativa = false;
        var linha = await LerLinhaAsync(tenant);
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId));
        // O ator e a origem são os da SESSÃO que resgatou, e não os do Worker que entregou.
        Assert.Equal(1, await EscalarAsync(
            "SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation = 'identity.embed.context' AND actor_origin = 'embedded-session' AND actor_user_id = @u",
            ("t", tenant), ("u", linha.ActorUserId)));
        Assert.Equal("embedded-session", linha.ActorOrigin);
    }

    [Fact]
    public async Task A_auditoria_so_existe_se_a_operacao_confirmou_e_a_falha_ao_gravar_o_outbox_desfaz_a_operacao_inteira()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "outbox-atomico", configurar: k => k["Identity:EmbeddedLockTimeout"] = "00:00:01");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var (sessao, _) = await c.EntrarAsync("u-atom");
        var ticketA = await c.Erp.EmitirContextoAsync("u-atom", "CLI-A", sessao.SessionId);
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId); // as da preparação: fica só a do resgate

        // (1) Contenção: a transação é desfeita, o ticket segue pendente e NENHUMA linha de auditoria nasce.
        await using (var presa = new NpgsqlConnection(TestDatabase.ConnectionString))
        {
            await presa.OpenAsync();
            await using var tx = await presa.BeginTransactionAsync();
            await using (var trava = new NpgsqlCommand("SELECT 1 FROM identity.sessions WHERE tenant_id = @t AND id = @s FOR UPDATE", presa, tx))
            {
                trava.Parameters.AddWithValue("t", tenant);
                trava.Parameters.AddWithValue("s", sessao.SessionId);
                await trava.ExecuteNonQueryAsync();
            }

            Assert.Equal(HttpStatusCode.ServiceUnavailable, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticketA.Ticket }));
            await tx.RollbackAsync();
        }

        Assert.Equal(0, await NoOutboxAsync(tenant));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t AND kind = 'Context' AND used_at IS NULL", ("t", tenant)));

        // (2) Falha AO GRAVAR o outbox (uma restrição que recusa a linha): a operação inteira é desfeita (ticket pendente, nenhum cliente em foco).
        await ExecutarAsync("ALTER TABLE identity.audit_outbox ADD CONSTRAINT ck_teste_recusa CHECK (false) NOT VALID");
        try
        {
            var status = await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticketA.Ticket });
            Assert.NotEqual(HttpStatusCode.OK, status);
        }
        finally
        {
            await ExecutarAsync("ALTER TABLE identity.audit_outbox DROP CONSTRAINT ck_teste_recusa");
        }

        Assert.Equal(0, await NoOutboxAsync(tenant));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t AND kind = 'Context' AND used_at IS NULL", ("t", tenant)));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.session_customer_grants WHERE tenant_id = @t AND session_id = @s", ("t", tenant), ("s", sessao.SessionId)));

        // (3) Sem a falha: confirma o efeito e a linha juntos, uma única vez.
        Assert.Equal(HttpStatusCode.OK, await c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticketA.Ticket }));
        Assert.Equal(1, await NoOutboxAsync(tenant));
    }

    [Fact]
    public async Task Workers_simultaneos_entregam_cada_linha_uma_vez_e_o_tenant_de_uma_entrega_nao_toca_nas_linhas_de_outro()
    {
        var a = await EmbeddedCenario.CriarAsync(banco, "outbox-tenant-a");
        await using var servidorA = a.Api;
        var b = await EmbeddedCenario.CriarAsync(banco, "outbox-tenant-b");
        await using var servidorB = b.Api;
        for (var i = 0; i < 6; i++)
        {
            _ = await a.EntrarAsync($"u-a-{i}");
        }

        _ = await b.EntrarAsync("u-b-0");
        var tenantA = a.Tenant.TenantId.Value;
        var tenantB = b.Tenant.TenantId.Value;
        var totalA = await NoOutboxAsync(tenantA);
        Assert.True(totalA >= 6 + 6, "preparação + 6 emissões + 6 trocas");
        var totalB = await NoOutboxAsync(tenantB);
        Assert.True(totalB >= 1 + 1, "preparação + emissão + troca");

        // Três "Workers" entregam o MESMO tenant ao mesmo tempo (FOR UPDATE SKIP LOCKED): 6 linhas, 6 eventos, nenhuma entregue duas vezes.
        var entregues = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => EmbeddedTestSupport.EntregarAuditoriaAsync(a.Api, a.Tenant.TenantId))));

        Assert.Equal(totalA, entregues.Sum());
        Assert.Equal(0, await NoOutboxAsync(tenantA));
        Assert.Equal(6, await NaTrilhaAsync(tenantA, "identity.embed.exchange"));

        // O tenant B não foi tocado pela entrega do A.
        Assert.Equal(totalB, await NoOutboxAsync(tenantB));
        Assert.Equal(0, await NaTrilhaAsync(tenantB, "identity.embed.exchange"));
        Assert.Equal(totalB, await EmbeddedTestSupport.EntregarAuditoriaAsync(b.Api, b.Tenant.TenantId));
        Assert.Equal(1, await NaTrilhaAsync(tenantB, "identity.embed.exchange"));
    }

    [Fact]
    public async Task Chave_revogada_entre_a_conferencia_e_a_gravacao_recusa_o_resgate_e_nenhum_cliente_e_acrescentado()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "chave-na-transacao");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var segunda = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
        var erpB = new ErpAssinante(c.Api, segunda.KeyId, segunda.Secret);
        var (sessao, _) = await c.EntrarAsync("u-chave-tx");
        var ticketB = await erpB.EmitirContextoAsync("u-chave-tx", "CLI-B", sessao.SessionId);

        // A transação externa segura a linha da chave B (como uma revogação a caminho). A conferência ANTES da transação final (leitura simples) ainda vê a chave ativa; a reconferência DENTRO dela espera a trava.
        await using var externa = new NpgsqlConnection(TestDatabase.ConnectionString);
        await externa.OpenAsync();
        await using var tx = await externa.BeginTransactionAsync();
        Guid chaveB;
        await using (var busca = new NpgsqlCommand("SELECT id FROM identity.integration_keys WHERE tenant_id = @t AND connection_id = @c ORDER BY created_at DESC LIMIT 1 FOR UPDATE", externa, tx))
        {
            busca.Parameters.AddWithValue("t", tenant);
            busca.Parameters.AddWithValue("c", c.Conexao.Id);
            chaveB = (Guid)(await busca.ExecuteScalarAsync())!;
        }

        var resgate = Task.Run(() => c.Api.StatusAsync(sessao.Token, HttpMethod.Post, "/embed/v1/context", new { ticket = ticketB.Ticket }));
        await EsperarAlguemNaFilaDeTravaAsync();

        // A revogação confirma ANTES de o resgate gravar (a limpeza dos tickets pendentes ainda não rodou): o resgate precisa recusar.
        await using (var revoga = new NpgsqlCommand("UPDATE identity.integration_keys SET status = 'Revoked', revoked_at = now() WHERE tenant_id = @t AND id = @k", externa, tx))
        {
            revoga.Parameters.AddWithValue("t", tenant);
            revoga.Parameters.AddWithValue("k", chaveB);
            await revoga.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        // Antes: a conferência acontecia só antes da transação, e o cliente em foco era acrescentado por um ticket de chave já revogada.
        Assert.Equal(HttpStatusCode.Unauthorized, await resgate.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty((await c.Api.LerAsync<SessaoEmbutidaDto>(sessao.Token, "/embed/v1/session")).CustomerRefs);
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t AND operation = 'identity.embed.context'", ("t", tenant)));
    }

    private static async Task EsperarAlguemNaFilaDeTravaAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (await EscalarAsync("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()") > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("ninguém entrou na fila de trava em 10 s");
    }

    [Fact]
    public async Task Verifier_malformado_ou_origem_invalida_com_ticket_identificavel_queima_o_ticket_com_a_mesma_resposta_de_sempre()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "malformado");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;

        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket1 = await c.Erp.EmitirAsync("u-mal", EmbeddedTestSupport.Challenge(verifier));
        var ticket2 = await c.Erp.EmitirAsync("u-mal", EmbeddedTestSupport.Challenge(verifier));
        var ticket3 = await c.Erp.EmitirAsync("u-mal", EmbeddedTestSupport.Challenge(verifier));

        // Referência: verifier BEM-FORMADO e errado (sempre queimou).
        using var errado = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket = ticket1.Ticket, verifier = EmbeddedTestSupport.NovoVerifier(), parentOrigin = EmbeddedTestSupport.Origem });
        // Verifier malformado e origem inválida: antes, a validação de formato vinha ANTES de qualquer acesso ao ticket e ele NÃO era queimado.
        using var malformado = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket = ticket2.Ticket, verifier = "curto", parentOrigin = EmbeddedTestSupport.Origem });
        using var origemInvalida = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket = ticket3.Ticket, verifier, parentOrigin = "nao-e-uma-origem" });

        // A resposta é a mesma (status e código) nos três casos: nada diferencia o que falhou.
        foreach (var r in new[] { errado, malformado, origemInvalida })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("embedded-authentication-failed", await EmbeddedTestSupport.CodigoDoProblemaAsync(r));
        }

        // Os três tickets estão queimados: nem com tudo certo depois.
        foreach (var t in new[] { ticket1, ticket2, ticket3 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await EmbeddedTestSupport.TentarTrocarAsync(c.Api, t.Ticket, verifier));
        }

        Assert.Equal(3, await EscalarAsync("SELECT count(*) FROM identity.launch_tickets WHERE tenant_id = @t AND kind = 'Launch' AND used_at IS NOT NULL", ("t", tenant)));
        Assert.Equal(3, await NaTrilhaAsync(tenant, "identity.embed.exchange", "Denied"));

        // Ticket desconhecido com verifier malformado: a mesma resposta, e nada é gravado na trilha (não há tenant a revelar).
        var antes = await NaTrilhaAsync(tenant, "identity.embed.exchange", "Denied");
        using var desconhecido = await c.Api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket = "ibt_" + new string('a', 43), verifier = "curto", parentOrigin = EmbeddedTestSupport.Origem });
        Assert.Equal(HttpStatusCode.Unauthorized, desconhecido.StatusCode);
        Assert.Equal("embedded-authentication-failed", await EmbeddedTestSupport.CodigoDoProblemaAsync(desconhecido));
        Assert.Equal(antes, await NaTrilhaAsync(tenant, "identity.embed.exchange", "Denied"));
    }

    [Fact]
    public void A_migration_cria_e_remove_a_tabela_do_outbox_e_o_inventario_de_retencao_a_conhece_sem_expurgo()
    {
        var tipo = typeof(IaBimeta.Identity.Infrastructure.IdentityDbContext).Assembly.GetType("IaBimeta.Identity.Infrastructure.Migrations.AuditOutbox")!;
        var migration = (Microsoft.EntityFrameworkCore.Migrations.Migration)Activator.CreateInstance(tipo)!;

        var cria = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("audit_outbox", cria.Name);
        Assert.Equal("identity", cria.Schema);
        Assert.Contains(cria.Columns, c => c.Name == "actor_origin" && !c.IsNullable);
        Assert.Contains(cria.Columns, c => c.Name == "created_at" && !c.IsNullable);
        var remove = Assert.Single(migration.DownOperations.OfType<DropTableOperation>());
        Assert.Equal("audit_outbox", remove.Name);
        Assert.Equal("identity", remove.Schema);

        // A tabela guarda registro de referência (auditoria pendente): está no inventário e nenhuma tarefa de expurgo a apaga por prazo.
        Assert.True(IaBimeta.Api.Retention.RetentionInventory.Tables.TryGetValue("identity.audit_outbox", out var entrada));
        Assert.Equal(IaBimeta.Api.Retention.DataClass.LegalRecord, entrada.Class);
    }
}
