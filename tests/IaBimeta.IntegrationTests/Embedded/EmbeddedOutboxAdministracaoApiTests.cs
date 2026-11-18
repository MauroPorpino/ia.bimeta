using System.Net;
using System.Net.Http.Json;
using IaBimeta.Audit.Application;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.IntegrationTests.Api;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>Controla a entrega à trilha nos testes de entrada problemática: a entrada marcada (<c>resource_id = "poison"</c>) falha de forma permanente, e a entrega pode falhar DEPOIS de gravar (a trilha aceitou, o processo "caiu").</summary>
internal sealed class ControleDeEntrega
{
    public volatile bool Veneno;

    public volatile bool FalharDepoisDeGravar;
}

internal sealed class AuditoriaComEntradaVenenosa(AuditWriter real, ControleDeEntrega controle) : IAuditWriter, IAuditRecoveryWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => real.WriteAsync(entry, cancellationToken);

    public Task WriteForAuthenticationAsync(IaBimeta.SharedKernel.TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) =>
        real.WriteForAuthenticationAsync(tenantId, actorUserId, entry, cancellationToken);

    public async Task WriteRecoveredAsync(AuditEntry entry, AuditProvenance provenance, CancellationToken cancellationToken = default)
    {
        if (controle.Veneno && entry.ResourceId == "poison")
        {
            // A mensagem carrega o que NÃO pode vazar para o registro da falha: só o tipo é guardado.
            throw new InvalidOperationException("segredo-que-nao-pode-vazar");
        }

        await real.WriteRecoveredAsync(entry, provenance, cancellationToken);
        if (controle.FalharDepoisDeGravar)
        {
            throw new TimeoutException("a trilha gravou, mas o processo caiu antes de apagar a linha");
        }
    }
}

/// <summary>
/// E1-R, quinta rodada: a criação e a rotação de chaves (e os demais fluxos administrativos e de emissão) guardam a auditoria do sucesso na MESMA transação do efeito (<c>identity.audit_outbox</c>), e a entrega de uma entrada
/// problemática não trava as demais. Antes, a gravação síncrona DEPOIS do commit transformava uma falha transitória da trilha em 503 com a chave já criada (o segredo, mostrado uma única vez, se perdia). API real, PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedOutboxAdministracaoApiTests(PostgresFixture banco)
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

    private static Task<long> NoOutboxAsync(Guid tenant, string operacao) =>
        EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t AND operation = @o", ("t", tenant), ("o", operacao));

    private static Task<long> NaTrilhaAsync(Guid tenant, string operacao) =>
        EscalarAsync("SELECT count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation = @o", ("t", tenant), ("o", operacao));

    private static Task<long> ChavesAsync(Guid tenant, Guid conexao, string filtro = "true") =>
        EscalarAsync($"SELECT count(*) FROM identity.integration_keys WHERE tenant_id = @t AND connection_id = @c AND {filtro}", ("t", tenant), ("c", conexao));

    private async Task<(EmbeddedCenario Cenario, FalhaDeAuditoria Falha)> CenarioComFalhaAsync(string slug)
    {
        var falha = new FalhaDeAuditoria();
        var api = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(null), null, s =>
        {
            s.AddSingleton(falha);
            s.AddScoped<AuditWriter>();
            s.AddScoped<IAuditWriter, AuditoriaQueFalha>();
            s.AddScoped<IAuditRecoveryWriter, AuditoriaQueFalha>();
        });
        return (await EmbeddedCenario.CriarAsync(banco, slug, api: api), falha);
    }

    [Fact]
    public async Task Falha_transitoria_da_auditoria_na_criacao_de_chave_nao_vira_503_o_segredo_chega_uma_vez_e_a_auditoria_vai_depois_sem_o_segredo()
    {
        var (c, falha) = await CenarioComFalhaAsync("outbox-chave-cria");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId); // as da preparação
        var antes = await ChavesAsync(tenant, c.Conexao.Id);

        // A trilha cai DEPOIS de a chave poder ser criada. Antes: a chave nascia, a auditoria síncrona estourava, 503, o segredo (mostrado uma só vez) se perdia, e repetir criava OUTRA chave (ou 409 no limite).
        falha.Ativa = true;
        using var resposta = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys", new { });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var criada = (await resposta.Content.ReadFromJsonAsync<ChaveCriadaDto>())!;
        Assert.StartsWith("ibk_", criada.KeyId);
        Assert.False(string.IsNullOrEmpty(criada.Secret));
        Assert.Equal(antes + 1, await ChavesAsync(tenant, c.Conexao.Id));
        Assert.Equal(1, await NoOutboxAsync(tenant, "identity.embed.key.create"));
        Assert.Equal(1, await NaTrilhaAsync(tenant, "identity.embed.key.create")); // só a da preparação: a desta chave ainda não chegou

        // A chave nova funciona de verdade (o segredo que saiu na resposta é o certo).
        falha.Ativa = false;
        var erp = new ErpAssinante(c.Api, criada.KeyId, criada.Secret);
        var emitido = await erp.EmitirAsync("u-chave-nova", EmbeddedTestSupport.Challenge(EmbeddedTestSupport.NovoVerifier()));
        Assert.StartsWith("ibt_", emitido.Ticket);

        // A entrega grava UM evento, sem o segredo, e só então apaga a linha.
        Assert.True(await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId) >= 1);
        Assert.Equal(0, await NoOutboxAsync(tenant, "identity.embed.key.create"));
        Assert.Equal(2, await NaTrilhaAsync(tenant, "identity.embed.key.create")); // a da preparação e esta
        var tudo = await EscalarAsync("SELECT count(*) FROM audit.audit_events e WHERE e.tenant_id = @t AND row_to_json(e)::text LIKE '%' || @s || '%'", ("t", tenant), ("s", criada.Secret));
        Assert.Equal(0, tudo);
    }

    [Fact]
    public async Task Falha_transitoria_da_auditoria_na_rotacao_de_chave_nao_vira_503_e_o_vencimento_programado_confirma_junto()
    {
        var (c, falha) = await CenarioComFalhaAsync("outbox-chave-rotaciona");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        falha.Ativa = true;
        using var resposta = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/rotate", new { graceHours = 2 });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var nova = (await resposta.Content.ReadFromJsonAsync<ChaveCriadaDto>())!;
        Assert.NotNull(nova.OthersExpireAt);
        Assert.Equal(2, await ChavesAsync(tenant, c.Conexao.Id));
        // O vencimento programado da chave anterior e a chave nova confirmaram na mesma transação da linha de auditoria.
        Assert.Equal(1, await ChavesAsync(tenant, c.Conexao.Id, "expires_at IS NOT NULL"));
        Assert.Equal(1, await NoOutboxAsync(tenant, "identity.embed.key.rotate"));

        // Repetir NÃO é necessário (e não criaria uma terceira chave utilizável): a chave anterior continua valendo até o vencimento.
        falha.Ativa = false;
        Assert.True(await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId) >= 1);
        Assert.Equal(1, await NaTrilhaAsync(tenant, "identity.embed.key.rotate"));
    }

    [Fact]
    public async Task Falha_transitoria_da_auditoria_na_revogacao_de_chave_nao_vira_503_e_repetir_responde_404_como_sempre()
    {
        var (c, falha) = await CenarioComFalhaAsync("outbox-chave-revoga");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var segunda = await c.Api.CriarAsync<ChaveCriadaDto>(c.TokenDoAdmin, $"/embedded/connections/{c.Conexao.Id}/keys", new { });
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        falha.Ativa = true;
        Assert.Equal(HttpStatusCode.NoContent, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{segunda.KeyId}/revoke"));

        // O efeito confirmou (a chave está revogada) e a linha de auditoria foi junto; antes, a resposta era 503 e a repetição, 404.
        Assert.Equal(1, await ChavesAsync(tenant, c.Conexao.Id, "status = 'Revoked'"));
        Assert.Equal(1, await NoOutboxAsync(tenant, "identity.embed.key.revoke"));
        Assert.Equal(HttpStatusCode.NotFound, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/{segunda.KeyId}/revoke"));
        Assert.Equal(1, await NoOutboxAsync(tenant, "identity.embed.key.revoke")); // a repetição recusada não grava auditoria de sucesso
    }

    [Fact]
    public async Task Os_demais_fluxos_com_efeito_tambem_nao_viram_503_quando_a_trilha_falha_e_todos_chegam_a_trilha_depois()
    {
        var (c, falha) = await CenarioComFalhaAsync("outbox-demais");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        var verifier = EmbeddedTestSupport.NovoVerifier();
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);

        falha.Ativa = true;
        // Administração: criar conexão, atualizar, revogar sessões; emissão de ticket pelo ERP (com o provisionamento JIT do usuário novo).
        using var nova = await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, "/embedded/connections", new
        {
            organizationId = c.Organizacao.Id,
            name = "Outra",
            mode = "FullAttendant",
            provisioning = "MappedOnly",
            allowedOrigins = new[] { EmbeddedTestSupport.Origem },
            allowStartConversation = false,
        });
        Assert.Equal(HttpStatusCode.Created, nova.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{c.Conexao.Id}", new
        {
            organizationId = c.Organizacao.Id,
            name = "Renomeada",
            mode = "FullAttendant",
            provisioning = "JitAttendant",
            allowedOrigins = new[] { EmbeddedTestSupport.Origem },
            allowStartConversation = false,
        })).StatusCode);
        _ = await c.Erp.EmitirAsync("u-novo", EmbeddedTestSupport.Challenge(verifier)); // provisiona o usuário (JIT) e emite o ticket
        Assert.Equal(HttpStatusCode.OK, (await c.Api.EnviarAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/sessions/revoke", new { })).StatusCode);

        foreach (var operacao in new[] { "identity.embed.connection.create", "identity.embed.connection.update", "identity.embed.ticket.issue", "identity.embed.identity.provision", "identity.embed.session.revoke" })
        {
            Assert.Equal(1, await NoOutboxAsync(tenant, operacao));
            Assert.Equal(operacao == "identity.embed.connection.create" ? 1 : 0, await NaTrilhaAsync(tenant, operacao)); // a única na trilha é a da conexão criada na preparação
        }

        falha.Ativa = false;
        Assert.True(await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId) >= 5);
        foreach (var operacao in new[] { "identity.embed.connection.create", "identity.embed.connection.update", "identity.embed.ticket.issue", "identity.embed.identity.provision", "identity.embed.session.revoke" })
        {
            Assert.Equal(0, await NoOutboxAsync(tenant, operacao));
            Assert.Equal(operacao == "identity.embed.connection.create" ? 2 : 1, await NaTrilhaAsync(tenant, operacao));
        }
    }

    [Fact]
    public async Task A_criacao_e_a_rotacao_de_chave_so_confirmam_com_a_linha_de_auditoria_e_a_falha_ao_gravar_desfaz_tudo()
    {
        var c = await EmbeddedCenario.CriarAsync(banco, "outbox-chave-atomica");
        await using var servidor = c.Api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(c.Api, c.Tenant.TenantId);
        var antes = await ChavesAsync(tenant, c.Conexao.Id);

        // Uma restrição que recusa a linha de auditoria: a criação E a rotação são desfeitas inteiras (nenhuma chave nova, nenhum vencimento programado para a antiga).
        await ExecutarAsync("ALTER TABLE identity.audit_outbox ADD CONSTRAINT ck_teste_recusa_adm CHECK (false) NOT VALID");
        try
        {
            Assert.NotEqual(HttpStatusCode.Created, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys", new { }));
            Assert.NotEqual(HttpStatusCode.Created, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/rotate", new { graceHours = 2 }));
        }
        finally
        {
            await ExecutarAsync("ALTER TABLE identity.audit_outbox DROP CONSTRAINT ck_teste_recusa_adm");
        }

        Assert.Equal(antes, await ChavesAsync(tenant, c.Conexao.Id));
        Assert.Equal(0, await ChavesAsync(tenant, c.Conexao.Id, "expires_at IS NOT NULL"));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant)));

        // Sem a falha, a rotação confirma o efeito e a linha juntos.
        Assert.Equal(HttpStatusCode.Created, await c.Api.StatusAsync(c.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{c.Conexao.Id}/keys/rotate", new { graceHours = 2 }));
        Assert.Equal(1, await NoOutboxAsync(tenant, "identity.embed.key.rotate"));
        Assert.Equal(1, await ChavesAsync(tenant, c.Conexao.Id, "expires_at IS NOT NULL"));
    }

    private async Task<(ApiServer Api, EmbeddedCenario Cenario, ControleDeEntrega Controle)> CenarioComControleAsync(string slug)
    {
        var controle = new ControleDeEntrega();
        var api = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(null), null, s =>
        {
            s.AddSingleton(controle);
            s.AddScoped<AuditWriter>();
            s.AddScoped<IAuditWriter, AuditoriaComEntradaVenenosa>();
            s.AddScoped<IAuditRecoveryWriter, AuditoriaComEntradaVenenosa>();
        });
        return (api, await EmbeddedCenario.CriarAsync(banco, slug, api: api), controle);
    }

    private static Task InserirAsync(Guid tenant, string recurso, int minutosAtras) => ExecutarAsync(
        @"INSERT INTO identity.audit_outbox (tenant_id, id, operation, resource_type, resource_id, outcome, actor_user_id, actor_origin, created_at)
          VALUES (@t, @i, 'identity.embed.connection.update', 'integration_connection', @r, 1, NULL, 'authentication', now() - make_interval(mins => @m))",
        ("t", tenant), ("i", Guid.CreateVersion7()), ("r", recurso), ("m", minutosAtras));

    private static async Task<(int Tentativas, string? Erro, double SegundosAteProxima)> EstadoDaLinhaAsync(Guid tenant, string recurso)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT attempts, last_error, COALESCE(extract(epoch FROM next_attempt_at - now()), 0) FROM identity.audit_outbox WHERE tenant_id = @t AND resource_id = @r", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        comando.Parameters.AddWithValue("r", recurso);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync());
        return (leitor.GetInt32(0), leitor.IsDBNull(1) ? null : leitor.GetString(1), Convert.ToDouble(leitor.GetValue(2)));
    }

    private static async Task<AuditOutboxStatusLido> StatusAsync(ApiServer api)
    {
        using var escopo = api.Services.CreateScope();
        var status = await escopo.ServiceProvider.GetRequiredService<IaBimeta.Identity.Application.IIdentityAuditOutbox>().GetStatusAsync(CancellationToken.None);
        return new AuditOutboxStatusLido(status.Pending, status.Failing, status.MaxAttempts, status.OldestCreatedAt);
    }

    private sealed record AuditOutboxStatusLido(int Pending, int Failing, int MaxAttempts, DateTimeOffset? Oldest);

    [Fact]
    public async Task Entrada_problematica_na_frente_da_fila_nao_trava_as_seguintes_nao_e_descartada_e_o_recuo_cresce()
    {
        var (api, c, controle) = await CenarioComControleAsync("outbox-veneno");
        await using var servidor = api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId); // as da preparação

        // A MAIS ANTIGA é a problemática; duas normais vêm depois. Antes (e no desenho ingênuo), a primeira falha encerrava a entrega do tenant inteiro.
        controle.Veneno = true;
        await InserirAsync(tenant, "poison", 30);
        await InserirAsync(tenant, "ok-1", 20);
        await InserirAsync(tenant, "ok-2", 10);

        Assert.Equal(2, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId));
        Assert.Equal(2, await NaTrilhaAsync(tenant, "identity.embed.connection.update"));

        // A problemática continua LÁ (nunca descartada), com a falha registrada: contagem, SÓ o tipo do erro e a próxima tentativa no futuro.
        var (tentativas, erro, ate) = await EstadoDaLinhaAsync(tenant, "poison");
        Assert.Equal(1, tentativas);
        Assert.Equal("InvalidOperationException", erro);
        Assert.DoesNotContain("segredo", erro);
        Assert.InRange(ate, 20, 31);

        // Indicadores: quantidade, falhas, mais tentativas e a idade da pendência mais antiga.
        var status = await StatusAsync(api);
        Assert.True(status.Pending >= 1);
        Assert.True(status.Failing >= 1);
        Assert.True(status.MaxAttempts >= 1);
        Assert.NotNull(status.Oldest);
        Assert.True(DateTimeOffset.UtcNow - status.Oldest!.Value >= TimeSpan.FromMinutes(29));

        // Dentro do recuo, a entrega não martela a trilha: nada é tentado, a contagem não muda.
        Assert.Equal(0, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId));
        Assert.Equal(1, (await EstadoDaLinhaAsync(tenant, "poison")).Tentativas);

        // Vencido o recuo, tenta de novo, falha de novo e o recuo DOBRA (60 s, e não mais 30 s).
        await ExecutarAsync("UPDATE identity.audit_outbox SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @t AND resource_id = 'poison'", ("t", tenant));
        Assert.Equal(0, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId));
        var segunda = await EstadoDaLinhaAsync(tenant, "poison");
        Assert.Equal(2, segunda.Tentativas);
        Assert.InRange(segunda.SegundosAteProxima, 50, 61);

        // O problema some (a trilha volta a aceitar): a MESMA linha é entregue e só então apagada. Nada foi perdido.
        controle.Veneno = false;
        await ExecutarAsync("UPDATE identity.audit_outbox SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @t AND resource_id = 'poison'", ("t", tenant));
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId));
        Assert.Equal(3, await NaTrilhaAsync(tenant, "identity.embed.connection.update"));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task Falha_entre_gravar_na_trilha_e_apagar_a_linha_reentrega_sem_duplicar_o_evento()
    {
        var (api, c, controle) = await CenarioComControleAsync("outbox-reentrega");
        await using var servidor = api;
        var tenant = c.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId);
        await InserirAsync(tenant, "reentrega-1", 5);

        // A trilha ACEITA o evento e, antes de a linha ser apagada, a entrega falha: a linha fica (com a falha registrada) e o evento já está na trilha.
        controle.FalharDepoisDeGravar = true;
        Assert.Equal(0, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId));
        Assert.Equal(1, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t AND resource_id = 'reentrega-1'", ("t", tenant)));
        Assert.Equal(1, await NaTrilhaAsync(tenant, "identity.embed.connection.update"));

        // Vencido o recuo, a reentrega da MESMA linha NÃO duplica o evento (a chave de idempotência é o id da linha) e a linha some.
        controle.FalharDepoisDeGravar = false;
        await ExecutarAsync("UPDATE identity.audit_outbox SET next_attempt_at = now() - interval '1 second' WHERE tenant_id = @t", ("t", tenant));
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId));
        Assert.Equal(1, await NaTrilhaAsync(tenant, "identity.embed.connection.update"));
        Assert.Equal(0, await EscalarAsync("SELECT count(*) FROM identity.audit_outbox WHERE tenant_id = @t", ("t", tenant)));
    }

    [Fact]
    public async Task Workers_simultaneos_com_uma_entrada_problematica_entregam_as_demais_uma_vez_e_tentam_a_problematica_uma_vez_so_por_ciclo()
    {
        var (api, c, controle) = await CenarioComControleAsync("outbox-veneno-conc");
        await using var servidor = api;
        var outro = await EmbeddedCenario.CriarAsync(banco, "outbox-veneno-outro", api: api);
        var tenant = c.Tenant.TenantId.Value;
        var tenantOutro = outro.Tenant.TenantId.Value;
        await EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId);
        await EmbeddedTestSupport.EntregarAuditoriaAsync(api, outro.Tenant.TenantId);

        controle.Veneno = true;
        await InserirAsync(tenant, "poison", 60);
        for (var i = 0; i < 8; i++)
        {
            await InserirAsync(tenant, $"ok-{i}", 50 - i);
        }

        await InserirAsync(tenantOutro, "poison", 40); // a problemática de OUTRO tenant não interfere no primeiro (e vice-versa)
        await InserirAsync(tenantOutro, "outro-ok", 30);

        var entregues = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => EmbeddedTestSupport.EntregarAuditoriaAsync(api, c.Tenant.TenantId))));

        Assert.Equal(8, entregues.Sum());
        Assert.Equal(8, await NaTrilhaAsync(tenant, "identity.embed.connection.update"));
        Assert.Equal(1, (await EstadoDaLinhaAsync(tenant, "poison")).Tentativas); // um ciclo concorrente não conta várias falhas da mesma linha
        // O outro tenant não foi tocado pela entrega do primeiro.
        Assert.Equal(0, await NaTrilhaAsync(tenantOutro, "identity.embed.connection.update"));
        Assert.Equal(0, (await EstadoDaLinhaAsync(tenantOutro, "poison")).Tentativas);
        Assert.Equal(1, await EmbeddedTestSupport.EntregarAuditoriaAsync(api, outro.Tenant.TenantId));
        Assert.Equal(1, await NaTrilhaAsync(tenantOutro, "identity.embed.connection.update"));
    }

    [Fact]
    public void A_migration_de_retentativa_acrescenta_as_colunas_e_o_down_as_remove()
    {
        var tipo = typeof(IaBimeta.Identity.Infrastructure.IdentityDbContext).Assembly.GetType("IaBimeta.Identity.Infrastructure.Migrations.AuditOutboxRetry")!;
        var migration = (Microsoft.EntityFrameworkCore.Migrations.Migration)Activator.CreateInstance(tipo)!;

        var colunas = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>().ToList();
        Assert.Equal(new[] { "attempts", "last_error", "next_attempt_at" }, colunas.Select(o => o.Name).OrderBy(n => n).ToArray());
        Assert.All(colunas, o => Assert.Equal("audit_outbox", o.Table));
        Assert.Equal(3, migration.DownOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.DropColumnOperation>().Count());
    }
}
