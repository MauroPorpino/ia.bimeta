using System.Net;
using System.Net.Http.Json;
using IaBimeta.Api.Retention;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.Identity.Domain;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A política de retenção (ADR 0025) com PostgreSQL real: relatório sem efeito, expurgo só do TÉCNICO vencido, sobrescrita por empresa nos dois sentidos, mortos que nunca saem, bloqueio do Inbox por morto antigo,
/// configuração inválida que falha na partida e o inventário que obriga a classificar toda tabela. Dados pessoais e de referência não têm tarefa de expurgo, e o teste do inventário garante que continuam assim.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetencaoDeDadosTests(PostgresFixture banco)
{
    private const short Pendente = 1;
    private const short Processado = 3;
    private const short Morto = 4;

    private sealed record Cenario(TenantInfo Tenant, UsuarioCriado Admin, UsuarioCriado Usuario);

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, "Organização de teste");
        var usuario = await banco.CriarUsuarioAsync(tenant, organizacao.Id, Role.User);
        return new Cenario(tenant, admin, usuario);
    }

    private static Dictionary<string, string?> Configuracao(params (string Slug, RetentionCategory Categoria, int Dias)[] sobrescritas)
    {
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:LoginRateLimit:PermitLimit"] = "1000";
        foreach (var (slug, categoria, dias) in sobrescritas)
        {
            configuracao[$"Retention:Tenants:{slug}:{categoria}"] = dias.ToString();
        }

        return configuracao;
    }

    private static DateTimeOffset HaDias(double dias) => DateTimeOffset.UtcNow.AddDays(-dias);

    // SQL direto no banco de teste (o que o teste precisa envelhecer não tem caminho pela aplicação)

    private static async Task<int> ExecutarAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(string sql, params (string Nome, object? Valor)[] parametros)
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

    private static async Task<List<Guid>> IdsAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        var ids = new List<Guid>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            ids.Add(leitor.GetGuid(0));
        }

        return ids;
    }

    private static Task<int> InserirEventoAsync(string esquema, Guid tenant, short estado, DateTimeOffset criadoEm, DateTimeOffset? processadoEm = null) =>
        ExecutarAsync(
            $"INSERT INTO {esquema}.outbox_message (tenant_id, id, ordering_key, event_type, event_version, correlation_id, occurred_at, payload, state, attempts, next_attempt_at, created_at, processed_at) " +
            "VALUES (@t, @id, 'k', 'teste.evento', 1, @id, @quando, '{}'::jsonb, @estado, 0, @quando, @quando, @processado)",
            ("t", tenant), ("id", Guid.NewGuid()), ("quando", criadoEm), ("estado", estado), ("processado", processadoEm));

    private static Task<int> InserirMarcaAsync(string esquema, Guid tenant, DateTimeOffset processadaEm) =>
        ExecutarAsync($"INSERT INTO {esquema}.inbox_message (tenant_id, consumer, event_id, processed_at) VALUES (@t, 'teste', @id, @quando)", ("t", tenant), ("id", Guid.NewGuid()), ("quando", processadaEm));

    private static Task<long> EventosAsync(string esquema, Guid tenant, short estado) =>
        ContarAsync($"SELECT count(*) FROM {esquema}.outbox_message WHERE tenant_id = @t AND state = @s", ("t", tenant), ("s", estado));

    private static Task<long> MarcasAsync(string esquema, Guid tenant) =>
        ContarAsync($"SELECT count(*) FROM {esquema}.inbox_message WHERE tenant_id = @t", ("t", tenant));

    private static async Task<(int Codigo, string Saida)> ExecutarComandoAsync(ApiServer api, bool expurgar)
    {
        var saida = new StringWriter();
        var codigo = await RetentionCommand.ExecuteAsync(api.Services, expurgar, saida);
        return (codigo, saida.ToString());
    }

    // O expurgo técnico

    [Fact]
    public async Task O_relatorio_so_conta_e_o_expurgo_remove_o_tecnico_vencido_e_preserva_o_vigente()
    {
        var c = await MontarAsync("ret");
        var t = c.Tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao(
            (c.Tenant.Slug, RetentionCategory.Sessions, 10),
            (c.Tenant.Slug, RetentionCategory.PasswordResetTokens, 10),
            (c.Tenant.Slug, RetentionCategory.LoginThrottles, 10),
            (c.Tenant.Slug, RetentionCategory.ProcessedEvents, 10)));

        // Sessões: vigente, vencida há 5 dias (recente), vencida há 15 (antiga), revogada há 20 (antiga) e revogada há 2 (recente).
        var adminToken = await api.LoginAsync(c.Admin);
        for (var i = 0; i < 5; i++)
        {
            await api.LoginAsync(c.Usuario);
        }

        var sessoes = await IdsAsync("SELECT id FROM identity.sessions WHERE tenant_id = @t AND user_id = @u ORDER BY created_at, id", ("t", t), ("u", c.Usuario.Id));
        Assert.Equal(5, sessoes.Count);
        await ExecutarAsync("UPDATE identity.sessions SET expires_at = @e WHERE id = @id", ("e", HaDias(5)), ("id", sessoes[1]));
        await ExecutarAsync("UPDATE identity.sessions SET expires_at = @e WHERE id = @id", ("e", HaDias(15)), ("id", sessoes[2]));
        await ExecutarAsync("UPDATE identity.sessions SET revoked_at = @r WHERE id = @id", ("r", HaDias(20)), ("id", sessoes[3]));
        await ExecutarAsync("UPDATE identity.sessions SET revoked_at = @r WHERE id = @id", ("r", HaDias(2)), ("id", sessoes[4]));

        // Tokens de redefinição: um usado há 20 dias (antigo), um vencido há 15 (antigo) e um ainda utilizável.
        async Task<string> EmitirAsync()
        {
            using var resposta = await api.EnviarAsync(adminToken, HttpMethod.Post, $"/organizations/{(await OrganizacaoDoUsuarioAsync(c))}/users/{c.Usuario.Id}/password-reset");
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            return (await resposta.Content.ReadFromJsonAsync<EmissaoDto>())!.Token;
        }

        await EmitirAsync();
        await ExecutarAsync("UPDATE identity.password_reset_tokens SET used_at = @u WHERE tenant_id = @t AND used_at IS NULL", ("u", HaDias(20)), ("t", t));
        await EmitirAsync();
        await ExecutarAsync("UPDATE identity.password_reset_tokens SET expires_at = @e WHERE tenant_id = @t AND used_at IS NULL", ("e", HaDias(15)), ("t", t));
        var utilizavel = await EmitirAsync();

        // Contadores de login: um com janela e bloqueio antigos, um com janela antiga mas bloqueio AINDA vigente, e um recente.
        for (var i = 0; i < 5; i++)
        {
            await FalharLoginAsync(api, c.Usuario.TenantSlug, c.Usuario.Email);
            await FalharLoginAsync(api, c.Usuario.TenantSlug, "bloqueado@teste.local");
        }

        await FalharLoginAsync(api, c.Usuario.TenantSlug, c.Admin.Email);
        await ExecutarAsync("UPDATE identity.login_throttles SET window_started_at = @w, locked_until = @l WHERE tenant_id = @t AND throttle_key = @k", ("w", HaDias(20)), ("l", HaDias(20)), ("t", t), ("k", c.Usuario.Email));
        await ExecutarAsync("UPDATE identity.login_throttles SET window_started_at = @w, locked_until = @l WHERE tenant_id = @t AND throttle_key = @k", ("w", HaDias(20)), ("l", DateTimeOffset.UtcNow.AddHours(1)), ("t", t), ("k", "bloqueado@teste.local"));

        // Eventos do canal: processado há 15 dias (antigo), processado há 5 (recente), pendente e morto antigos (nunca saem).
        await InserirEventoAsync("channels", t, Processado, HaDias(16), HaDias(15));
        await InserirEventoAsync("channels", t, Processado, HaDias(6), HaDias(5));
        await InserirEventoAsync("channels", t, Pendente, HaDias(30));
        await InserirEventoAsync("channels", t, Morto, HaDias(30));
        await InserirEventoAsync("conversations", t, Processado, HaDias(16), HaDias(15));

        // 1) O relatório só conta.
        var (codigoDoRelatorio, relatorio) = await ExecutarComandoAsync(api, expurgar: false);
        Assert.Equal(0, codigoDoRelatorio);
        Assert.Contains($"identity.sessions [empresa '{c.Tenant.Slug}', 10 d]: 2 elegível(is)", relatorio);
        Assert.Contains($"identity.password_reset_tokens [empresa '{c.Tenant.Slug}', 10 d]: 2 elegível(is)", relatorio);
        Assert.Contains($"identity.login_throttles [empresa '{c.Tenant.Slug}', 10 d]: 1 elegível(is)", relatorio);
        Assert.Contains($"channels.outbox_message [empresa '{c.Tenant.Slug}', 10 d]: 1 elegível(is)", relatorio);
        Assert.Contains($"conversations.outbox_message [empresa '{c.Tenant.Slug}', 10 d]: 1 elegível(is)", relatorio);
        Assert.Equal(5, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND user_id = @u", ("t", t), ("u", c.Usuario.Id)));
        Assert.Equal(3, await ContarAsync("SELECT count(*) FROM identity.password_reset_tokens WHERE tenant_id = @t", ("t", t)));
        Assert.Equal(3, await ContarAsync("SELECT count(*) FROM identity.login_throttles WHERE tenant_id = @t", ("t", t)));
        Assert.Equal(2, await EventosAsync("channels", t, Processado));

        // 2) O expurgo remove só o vencido.
        var (codigoDoExpurgo, saida) = await ExecutarComandoAsync(api, expurgar: true);
        Assert.Equal(0, codigoDoExpurgo);
        Assert.Contains($"identity.sessions [empresa '{c.Tenant.Slug}', 10 d]: 2 removido(s) de 2 elegível(is)", saida);

        var restantes = await IdsAsync("SELECT id FROM identity.sessions WHERE tenant_id = @t AND user_id = @u", ("t", t), ("u", c.Usuario.Id));
        Assert.Equivalent(new[] { sessoes[0], sessoes[1], sessoes[4] }, restantes); // vigente, vencida há 5 e revogada há 2
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.password_reset_tokens WHERE tenant_id = @t", ("t", t)));
        Assert.Equal(2, await ContarAsync("SELECT count(*) FROM identity.login_throttles WHERE tenant_id = @t", ("t", t)));
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.login_throttles WHERE tenant_id = @t AND throttle_key = @k", ("t", t), ("k", "bloqueado@teste.local"))); // o bloqueio vigente ficou
        Assert.Equal(1, await EventosAsync("channels", t, Processado));
        Assert.Equal(1, await EventosAsync("channels", t, Pendente));
        Assert.Equal(1, await EventosAsync("channels", t, Morto)); // pendente e morto nunca saem, por mais antigos que sejam
        Assert.Equal(0, await EventosAsync("conversations", t, Processado));

        // O token ainda utilizável e a sessão vigente continuam funcionando.
        using var uso = await api.Client.PostAsJsonAsync("/auth/password-reset/redeem", new { tenant = c.Tenant.Slug, email = c.Usuario.Email, token = utilizavel, newPassword = "Outra-Senha-Longa-456!" });
        Assert.Equal(HttpStatusCode.NoContent, uso.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(adminToken, HttpMethod.Get, "/auth/me"));
    }

    private sealed record EmissaoDto(string Token, DateTimeOffset ExpiresAt);

    private async Task<Guid> OrganizacaoDoUsuarioAsync(Cenario c)
    {
        var ids = await IdsAsync("SELECT organization_id FROM identity.user_organization_grants WHERE tenant_id = @t AND user_id = @u AND organization_id IS NOT NULL", ("t", c.Tenant.TenantId.Value), ("u", c.Usuario.Id));
        return ids.Single();
    }

    private static async Task FalharLoginAsync(ApiServer api, string tenant, string email)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/auth/login", new { tenant, email, password = "senha-errada-123" });
        Assert.True(resposta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_sobrescrita_por_empresa_vale_para_mais_e_para_menos_que_o_padrao()
    {
        var longa = await MontarAsync("longa");
        var curta = await MontarAsync("curta");
        await using var api = await ApiServer.StartAsync("Production", Configuracao(
            (longa.Tenant.Slug, RetentionCategory.Sessions, 365),
            (curta.Tenant.Slug, RetentionCategory.Sessions, 1)));

        // A mesma sessão vencida: de 100 dias na empresa de prazo longo (o padrão de 30 dias a removeria) e de 5 dias na de prazo curto (o padrão a manteria).
        await api.LoginAsync(longa.Usuario);
        await api.LoginAsync(curta.Usuario);
        await ExecutarAsync("UPDATE identity.sessions SET expires_at = @e WHERE tenant_id = @t AND user_id = @u", ("e", HaDias(100)), ("t", longa.Tenant.TenantId.Value), ("u", longa.Usuario.Id));
        await ExecutarAsync("UPDATE identity.sessions SET expires_at = @e WHERE tenant_id = @t AND user_id = @u", ("e", HaDias(5)), ("t", curta.Tenant.TenantId.Value), ("u", curta.Usuario.Id));

        var (codigo, _) = await ExecutarComandoAsync(api, expurgar: true);

        Assert.Equal(0, codigo);
        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND user_id = @u", ("t", longa.Tenant.TenantId.Value), ("u", longa.Usuario.Id)));
        Assert.Equal(0, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND user_id = @u", ("t", curta.Tenant.TenantId.Value), ("u", curta.Usuario.Id)));
    }

    [Fact]
    public async Task As_marcas_do_Inbox_vencidas_saem_quando_nao_ha_morto_antigo_e_a_recente_fica()
    {
        var c = await MontarAsync("inbox");
        var t = c.Tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao((c.Tenant.Slug, RetentionCategory.InboxMarkers, 20)));
        foreach (var esquema in new[] { "channels", "agents", "contact_center" })
        {
            await InserirMarcaAsync(esquema, t, HaDias(25)); // vencida (corte de 20 dias)
            await InserirMarcaAsync(esquema, t, HaDias(10)); // recente
        }

        var (codigo, saida) = await ExecutarComandoAsync(api, expurgar: true);

        Assert.Equal(0, codigo);
        Assert.DoesNotContain("BLOQUEADO", saida.Split('\n').Single(l => l.Contains($"channels.inbox_message [empresa '{c.Tenant.Slug}'")));
        foreach (var esquema in new[] { "channels", "agents", "contact_center" })
        {
            Assert.Equal(1, await MarcasAsync(esquema, t));
        }
    }

    [Fact]
    public async Task Evento_morto_antigo_bloqueia_o_expurgo_das_marcas_e_nunca_e_expurgado()
    {
        var c = await MontarAsync("morto");
        var t = c.Tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao(
            (c.Tenant.Slug, RetentionCategory.ProcessedEvents, 5),
            (c.Tenant.Slug, RetentionCategory.InboxMarkers, 10)));
        await InserirEventoAsync("conversations", t, Morto, HaDias(400)); // um morto muito antigo
        await InserirMarcaAsync("agents", t, HaDias(30)); // a marca que ele ainda pode precisar

        var (codigo, saida) = await ExecutarComandoAsync(api, expurgar: true);

        Assert.Equal(0, codigo);
        var linha = saida.Split('\n').Single(l => l.Contains($"agents.inbox_message [empresa '{c.Tenant.Slug}'"));
        Assert.Contains("BLOQUEADO", linha);
        Assert.Contains("conversations.outbox_message", linha);
        Assert.Equal(1, await MarcasAsync("agents", t)); // a marca ficou
        Assert.Equal(1, await EventosAsync("conversations", t, Morto)); // e o morto também
        Assert.Contains("conversations.outbox_message: ", saida); // o relatório lista os mortos
    }

    [Fact]
    public async Task O_relay_do_Worker_expurga_os_processados_com_o_prazo_de_cada_empresa_e_nunca_os_mortos()
    {
        var comPrazo = await MontarAsync("prazo");
        var padrao = await MontarAsync("padrao");
        await using var api = await ApiServer.StartAsync("Production", Configuracao((comPrazo.Tenant.Slug, RetentionCategory.ProcessedEvents, 10)));
        var tp = comPrazo.Tenant.TenantId.Value;
        var td = padrao.Tenant.TenantId.Value;
        await InserirEventoAsync("channels", tp, Processado, HaDias(16), HaDias(15)); // vencido para 10 dias, e para 7
        await InserirEventoAsync("channels", tp, Processado, HaDias(9), HaDias(8)); // dentro dos 10 dias da empresa (o padrão de 7 o removeria)
        await InserirEventoAsync("channels", td, Processado, HaDias(9), HaDias(8)); // vencido no padrão de 7 dias
        await InserirEventoAsync("channels", td, Processado, HaDias(7), HaDias(6)); // dentro do padrão
        await InserirEventoAsync("channels", td, Morto, HaDias(400));
        var relay = new OutboxRelay<ChannelsDbContext>(
            api.Services.GetRequiredService<IServiceScopeFactory>(),
            new SemInicializador(),
            new MessagingOptions(),
            TimeProvider.System,
            NullLogger<OutboxRelay<ChannelsDbContext>>.Instance,
            api.Services.GetRequiredService<IRetentionPolicy>());

        await relay.PurgeProcessedAsync();

        Assert.Equal(1, await EventosAsync("channels", tp, Processado));
        Assert.Equal(1, await EventosAsync("channels", td, Processado));
        Assert.Equal(1, await EventosAsync("channels", td, Morto));
    }

    private sealed class SemInicializador : IMessagingScopeInitializer
    {
        public void Initialize(IServiceScope scope, TenantId tenant, string origin)
        {
        }
    }

    // Segurança do procedimento

    [Fact]
    public async Task Empresa_inexistente_na_configuracao_impede_a_execucao_sem_apagar_nada()
    {
        var c = await MontarAsync("fantasma");
        var configuracao = Configuracao((c.Tenant.Slug, RetentionCategory.Sessions, 10));
        configuracao["Retention:Tenants:empresa-que-nao-existe:Sessions"] = "2";
        await using var api = await ApiServer.StartAsync("Production", configuracao);
        await api.LoginAsync(c.Usuario);
        await ExecutarAsync("UPDATE identity.sessions SET expires_at = @e WHERE tenant_id = @t", ("e", HaDias(100)), ("t", c.Tenant.TenantId.Value));

        var (codigo, saida) = await ExecutarComandoAsync(api, expurgar: true);

        Assert.Equal(1, codigo);
        Assert.Contains("empresa-que-nao-existe", saida);
        Assert.True(await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND expires_at < @e", ("t", c.Tenant.TenantId.Value), ("e", HaDias(50))) >= 1); // nada foi apagado
    }

    [Theory]
    [InlineData("Retention:Days:InboxMarkers", "5")] // menor que os eventos processados (7): a marca expiraria antes do evento que ela protege
    [InlineData("Retention:Days:Sessions", "0")]
    [InlineData("Retention:Days:ProcessedEvents", "99999")]
    public async Task Configuracao_de_retencao_invalida_falha_na_partida(string chave, string valor)
    {
        var configuracao = Configuracao();
        configuracao[chave] = valor;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var api = await ApiServer.StartAsync("Production", configuracao);
        });
    }

    [Fact]
    public async Task O_comando_exige_confirmacao_para_expurgar_e_recusa_uso_invalido()
    {
        var saida = new StringWriter();

        Assert.Equal(2, await RetentionCommand.RunAsync(["purge"], saida));
        Assert.Contains("--confirm", saida.ToString());
        Assert.Equal(2, await RetentionCommand.RunAsync([], new StringWriter()));
        Assert.Equal(2, await RetentionCommand.RunAsync(["report", "--confirm"], new StringWriter()));
        Assert.Equal(2, await RetentionCommand.RunAsync(["apagar-tudo"], new StringWriter()));
    }

    // Inventário

    [Fact]
    public async Task Toda_tabela_do_banco_esta_classificada_e_so_as_tecnicas_tem_tarefa_de_expurgo()
    {
        await using var api = await ApiServer.StartAsync("Production", Configuracao());

        var linhas = await RetentionCommand.ReadInventoryAsync(api.Services, default);

        Assert.DoesNotContain(linhas, l => l.Table.Contains("NÃO CLASSIFICADA"));
        var tecnicas = RetentionInventory.Tables.Where(e => e.Value.Class == DataClass.Technical).Select(e => e.Key).Order().ToList();
        var tarefas = api.Services.GetServices<IRetentionTask>().Select(t => t.Name).Order().ToList();

        // Toda tarefa é de uma tabela técnica, ou é uma exceção NOMEADA que age só sobre objetos do armazenamento privado (a linha pessoal nunca é apagada por prazo).
        Assert.Equal(tecnicas, tarefas.Where(t => !RetentionInventory.ObjectStoreTasks.Contains(t)).ToList());
        Assert.Equal(RetentionInventory.ObjectStoreTasks.Order().ToList(), tarefas.Where(RetentionInventory.ObjectStoreTasks.Contains).ToList());
        Assert.DoesNotContain(RetentionInventory.ObjectStoreTasks, nome => RetentionInventory.Tables.ContainsKey(nome));

        // Toda tabela classificada existe de fato (sem entrada esquecida de tabela que já saiu).
        var existentes = linhas.Select(l => l.Table).ToHashSet();
        Assert.All(RetentionInventory.Tables.Keys, tabela => Assert.Contains(tabela, existentes));
    }

    [Fact]
    public async Task O_relatorio_traz_o_inventario_com_o_aviso_de_que_dado_pessoal_nao_tem_expurgo_automatico()
    {
        await using var api = await ApiServer.StartAsync("Production", Configuracao());

        var (codigo, saida) = await ExecutarComandoAsync(api, expurgar: false);

        Assert.Equal(0, codigo);
        Assert.Contains("nada foi alterado", saida);
        Assert.Contains("conversations.messages", saida);
        Assert.Contains("PESSOAL (só relatório)", saida);
        Assert.Contains("audit.audit_events", saida);
        Assert.Contains("REFERÊNCIA (só relatório)", saida);
    }
}
