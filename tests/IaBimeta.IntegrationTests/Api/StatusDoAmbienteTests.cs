using IaBimeta.Api.Operations;
using IaBimeta.Channels.Application;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O comando <c>status</c> (supervisão e diagnóstico, ADR 0026) com PostgreSQL real: cada sonda de saúde acusa o que parou, a empresa certa aparece nomeada, o que está em ordem diz que está, e o que não pode ser verificado
/// (PostgreSQL fora do ar, Api que não responde) vira falha crítica com a ação, nunca exceção. Cada teste olha UMA empresa (<c>--tenant</c>): o banco de teste é compartilhado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StatusDoAmbienteTests(PostgresFixture banco)
{
    private const short Pendente = 1;
    private const short Processando = 2;
    private const short Processado = 3;
    private const short Morto = 4;

    private static Dictionary<string, string?> Configuracao(Action<Dictionary<string, string?>>? ajustar = null)
    {
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        ajustar?.Invoke(configuracao);
        return configuracao;
    }

    private static DateTimeOffset HaMinutos(double minutos) => DateTimeOffset.UtcNow.AddMinutes(-minutos);

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

    private static Task<int> InserirEventoAsync(string esquema, Guid tenant, short estado, DateTimeOffset quando, DateTimeOffset? lease = null) =>
        ExecutarAsync(
            $"INSERT INTO {esquema}.outbox_message (tenant_id, id, ordering_key, event_type, event_version, correlation_id, occurred_at, payload, state, attempts, next_attempt_at, created_at, lease_token, lease_owner, lease_expires_at, processed_at) " +
            "VALUES (@t, @id, 'k', 'teste.evento', 1, @id, @q, '{}'::jsonb, @estado, 0, @q, @q, @tokenDoLease, @dono, @lease, @processado)",
            ("t", tenant), ("id", Guid.NewGuid()), ("q", quando), ("estado", estado),
            ("tokenDoLease", estado == Processando ? Guid.NewGuid() : null), ("dono", estado == Processando ? "worker-que-caiu" : null), ("lease", lease), ("processado", estado == Processado ? quando : null));

    private static async Task<(int Codigo, string Saida)> StatusAsync(ApiServer api, string? slug, bool api_ = false, bool worker = false)
    {
        var saida = new StringWriter();
        var codigo = await StatusCommand.ExecuteAsync(api.Services, new StatusOptions(slug, api_ ? api.Client.BaseAddress + "health" : null, api_, worker), saida);
        return (codigo, saida.ToString());
    }

    // O que está em ordem

    [Fact]
    public async Task Empresa_sem_nada_parado_responde_tudo_em_ordem_com_codigo_zero()
    {
        var tenant = await banco.RegistrarTenantAsync("stok");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());

        var (codigo, saida) = await StatusAsync(api, tenant.Slug);

        Assert.Equal(0, codigo);
        Assert.Contains("RESULTADO: tudo em ordem.", saida);
        Assert.Contains("POSTGRESQL", saida);
        Assert.Contains("[OK]      conexão em", saida);
        Assert.Contains("8 módulos em dia.", saida);
        foreach (var componente in new[] { "OUTBOX:CONVERSATIONS", "OUTBOX:CHANNELS", "CHANNELS", "AGENTS", "CONTACT-CENTER" })
        {
            Assert.Contains(componente, saida);
        }

        Assert.DoesNotContain("[CRITICO]", saida);
        Assert.DoesNotContain("[ATENCAO]", saida);
    }

    // Eventos do outbox

    [Fact]
    public async Task Evento_elegivel_parado_e_lease_vencido_sao_criticos_e_o_morto_e_atencao_com_a_empresa_nomeada()
    {
        var tenant = await banco.RegistrarTenantAsync("stevt");
        var t = tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        await InserirEventoAsync("channels", t, Pendente, HaMinutos(20)); // elegível há 20 min e ninguém pegou
        await InserirEventoAsync("channels", t, Pendente, HaMinutos(1)); // recente: ainda é "em andamento"
        await InserirEventoAsync("conversations", t, Processando, HaMinutos(30), lease: HaMinutos(20)); // o relay caiu no meio
        await InserirEventoAsync("conversations", t, Morto, HaMinutos(60 * 24 * 3));
        await InserirEventoAsync("conversations", t, Processado, HaMinutos(60 * 24 * 3)); // processado nunca é constatação

        var (codigo, saida) = await StatusAsync(api, tenant.Slug);

        Assert.Equal(2, codigo);
        Assert.Contains($"[CRITICO] (empresa {tenant.Slug}) 1 evento(s) elegível(is) sem ser entregue(s) há mais de 5 min", saida);
        Assert.Contains($"[CRITICO] (empresa {tenant.Slug}) 1 evento(s) com a entrega interrompida", saida);
        Assert.Contains($"[ATENCAO] (empresa {tenant.Slug}) 1 evento(s) morto(s)", saida);
        Assert.Contains("o relay do Worker não está andando", saida);
        Assert.Contains("-> ", saida); // cada falha traz a ação
        Assert.Contains("RESULTADO: 2 falha(s) que precisam de intervenção e 1 ponto(s) de atenção.", saida);
    }

    [Fact]
    public async Task O_limite_vem_da_configuracao_e_uma_empresa_nao_enxerga_a_outra()
    {
        var comProblema = await banco.RegistrarTenantAsync("stlimite");
        var semProblema = await banco.RegistrarTenantAsync("stlivre");
        await using var api = await ApiServer.StartAsync("Production", Configuracao(k => k["Health:EventStuckAfter"] = "00:30:00"));
        await InserirEventoAsync("channels", comProblema.TenantId.Value, Pendente, HaMinutos(20)); // vale menos que os 30 min configurados
        var (dentroDoLimite, _) = await StatusAsync(api, comProblema.Slug);
        await InserirEventoAsync("channels", comProblema.TenantId.Value, Pendente, HaMinutos(40));

        var (alemDoLimite, saida) = await StatusAsync(api, comProblema.Slug);
        var (outraEmpresa, saidaDaOutra) = await StatusAsync(api, semProblema.Slug);

        Assert.Equal(0, dentroDoLimite);
        Assert.Equal(2, alemDoLimite);
        Assert.Contains("há mais de 30 min", saida);
        Assert.Equal(0, outraEmpresa);
        Assert.DoesNotContain(comProblema.Slug, saidaDaOutra);
    }

    // Canais

    [Fact]
    public async Task Conta_pausada_e_critica_com_o_motivo_e_a_acao_de_retomar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "stconta");
        var conta = await c.CriarContaAsync();
        await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IAccountSendStateStore>().PauseAsync(conta.Id, "account-error.190", default));
        await using var api = await ApiServer.StartAsync("Production", Configuracao());

        var (codigo, saida) = await StatusAsync(api, c.Tenant.Slug);

        Assert.Equal(2, codigo);
        Assert.Contains($"[CRITICO] (empresa {c.Tenant.Slug}) conta {conta.Id.ToString("N")[..8]} com o envio PAUSADO", saida);
        Assert.Contains("account-error.190", saida);
        Assert.Contains("O atendente não consegue responder ao cliente por ela.", saida);
        Assert.Contains("POST /channels/accounts/{id}/sending/resume", saida);
    }

    [Fact]
    public async Task Saida_sem_desfecho_e_critica_chamada_sem_desfecho_e_resultado_ambiguo_sao_atencao()
    {
        var tenant = await banco.RegistrarTenantAsync("stsaida");
        var t = tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        Task Saida(short estado, int tentativas, double minutos) => ExecutarAsync(
            "INSERT INTO channels.outbound_messages (tenant_id, id, operation_id, channel_instance_id, state, attempts, notified, created_at, updated_at) VALUES (@t, @id, @op, @ci, @estado, @n, false, @q, @q)",
            ("t", t), ("id", Guid.NewGuid()), ("op", Guid.NewGuid()), ("ci", Guid.NewGuid()), ("estado", estado), ("n", tentativas), ("q", HaMinutos(minutos)));
        await Saida(1, 0, 20); // intenção gravada e nunca despachada
        await Saida(5, 1, 20); // nova tentativa que ninguém fez
        await Saida(1, 1, 20); // a chamada ao provedor ficou sem desfecho
        await Saida(4, 1, 60); // resultado ambíguo recente
        await Saida(4, 1, 60 * 48); // ambíguo antigo: fora da janela de 24 h
        await Saida(2, 1, 60 * 48); // enviado: nada a dizer

        var (codigo, saida) = await StatusAsync(api, tenant.Slug);

        Assert.Equal(2, codigo);
        Assert.Contains("2 mensagem(ns) de saída sem desfecho há mais de 5 min", saida);
        Assert.Contains("1 mensagem(ns) com a chamada ao provedor sem desfecho", saida);
        Assert.Contains("1 mensagem(ns) de saída com resultado ambíguo", saida);
        Assert.Contains("nunca são reenviadas sozinhas", saida);
    }

    // IA e fila

    [Fact]
    public async Task Turno_de_IA_vencido_ou_interrompido_e_critico_e_o_morto_recente_e_atencao_com_o_motivo()
    {
        var tenant = await banco.RegistrarTenantAsync("stia");
        var t = tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        Task Turno(short estado, DateTimeOffset fireAt, DateTimeOffset? lease = null, DateTimeOffset? fim = null, string? motivo = null) => ExecutarAsync(
            "INSERT INTO agents.ai_turns (tenant_id, id, session_id, state, first_received_at, last_received_at, fire_at, attempts, supersede_count, input_pending, lane_token, claimed_by, lease_expires_at, notice_done, end_reason, created_at, updated_at, ended_at, version) " +
            "VALUES (@t, @id, @sessao, @estado, @q, @q, @fire, 0, 0, false, @lane, @dono, @lease, false, @motivo, @q, @q, @fim, 1)",
            ("t", t), ("id", Guid.NewGuid()), ("sessao", Guid.NewGuid()), ("estado", estado), ("q", fireAt), ("fire", fireAt), ("lane", estado == 3 ? 1L : null),
            ("dono", estado == 3 ? "worker-que-caiu" : null), ("lease", lease), ("motivo", motivo), ("fim", fim));
        await Turno(1, HaMinutos(20)); // agendado e vencido há 20 min
        await Turno(1, HaMinutos(1)); // recente
        await Turno(3, HaMinutos(40), lease: HaMinutos(20)); // o executor caiu
        await Turno(8, HaMinutos(30), fim: HaMinutos(30), motivo: "ai-quota-or-billing");
        await Turno(8, HaMinutos(30), fim: HaMinutos(30), motivo: "ai-quota-or-billing");
        await Turno(8, HaMinutos(60 * 48), fim: HaMinutos(60 * 48), motivo: "ai-not-configured"); // antigo: fora da janela

        var (codigo, saida) = await StatusAsync(api, tenant.Slug);

        Assert.Equal(2, codigo);
        Assert.Contains("1 turno(s) de IA vencido(s) sem executar há mais de 5 min", saida);
        Assert.Contains("1 turno(s) de IA com o lease vencido", saida);
        Assert.Contains("2 turno(s) de IA morto(s) nas últimas 24 h (ai-quota-or-billing=2)", saida);
        Assert.DoesNotContain("ai-not-configured=1", saida); // o morto antigo está fora da janela
    }

    [Fact]
    public async Task Conversa_esperando_alem_do_limite_e_auditoria_parada_sao_atencao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "stfila");
        var fila = await c.CriarFilaAsync();
        var tenant = c.Tenant;
        var t = tenant.TenantId.Value;
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        Task Item(short status, double minutos) => ExecutarAsync(
            "INSERT INTO contact_center.queue_items (id, tenant_id, session_id, organization_id, queue_id, status, version, entered_at, updated_at, pending_attempts, assigned_user_id, assigned_at) VALUES (@id, @t, @s, @o, @q, @st, 1, @quando, @quando, 0, @atendente, @atribuido)",
            ("id", Guid.NewGuid()), ("t", t), ("s", Guid.NewGuid()), ("o", c.OrganizacaoId), ("q", fila.Id), ("st", status), ("quando", HaMinutos(minutos)), ("atendente", status == 2 ? Guid.NewGuid() : null), ("atribuido", status == 2 ? HaMinutos(minutos) : null));
        await Item(1, 45); // esperando há 45 min
        await Item(1, 5); // recente
        await Item(2, 120); // já tem atendente: não é espera
        await ExecutarAsync(
            "INSERT INTO contact_center.audit_outbox (id, tenant_id, operation, resource_type, outcome, created_at, actor_origin) VALUES (@id, @t, 'contact-center.item.claim', 'queue-item', 1, @q, 'teste')",
            ("id", Guid.NewGuid()), ("t", t), ("q", HaMinutos(20)));

        var (codigo, saida) = await StatusAsync(api, tenant.Slug);

        Assert.Equal(1, codigo); // só atenção: o cliente espera, mas nada parou
        Assert.Contains("1 conversa(s) esperando atendente há mais de 30 min", saida);
        Assert.Contains("1 registro(s) de auditoria do ContactCenter sem entrega ao Audit", saida);
        Assert.Contains("RESULTADO: sem falha crítica; 2 ponto(s) de atenção.", saida);
    }

    // Verificações do ambiente

    [Fact]
    public async Task Api_que_nao_responde_e_Api_no_ar_sao_distinguidos_e_empresa_inexistente_e_falha()
    {
        var tenant = await banco.RegistrarTenantAsync("stapi");
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var saidaDoNoAr = new StringWriter();
        var saidaForaDoAr = new StringWriter();
        var saidaDaEmpresa = new StringWriter();

        var noAr = await StatusCommand.ExecuteAsync(api.Services, new StatusOptions(tenant.Slug, api.Client.BaseAddress + "health", true, false), saidaDoNoAr);
        var foraDoAr = await StatusCommand.ExecuteAsync(api.Services, new StatusOptions(tenant.Slug, "http://127.0.0.1:1/health", true, false), saidaForaDoAr);
        var semEmpresa = await StatusCommand.ExecuteAsync(api.Services, new StatusOptions("empresa-que-nao-existe", null, false, false), saidaDaEmpresa);

        Assert.Equal(0, noAr);
        Assert.Contains("respondeu 200", saidaDoNoAr.ToString());
        Assert.Equal(2, foraDoAr);
        Assert.Contains("[CRITICO] http://127.0.0.1:1/health não responde", saidaForaDoAr.ToString());
        Assert.Contains("Suba o Api", saidaForaDoAr.ToString());
        Assert.Equal(2, semEmpresa);
        Assert.Contains("empresa 'empresa-que-nao-existe' não encontrada", saidaDaEmpresa.ToString());
    }

    [Fact]
    public async Task Postgres_inacessivel_e_falha_critica_com_a_acao_e_sem_vazar_a_string_de_conexao()
    {
        await using var api = await ApiServer.StartAsync("Production", Configuracao());
        var configuracao = api.Services.GetRequiredService<IConfiguration>();
        var original = configuracao["ConnectionStrings:Default"];
        var saida = new StringWriter();

        // Um provedor de configuração que aponta o diagnóstico para uma porta sem servidor, com uma senha que não pode aparecer na saída.
        configuracao["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=nenhum;Username=u;Password=senha-secreta-do-teste;Timeout=2";
        try
        {
            var codigo = await StatusCommand.ExecuteAsync(api.Services, new StatusOptions(null, null, false, true), saida);

            Assert.Equal(2, codigo);
            Assert.Contains("[CRITICO] inacessível", saida.ToString());
            Assert.Contains("Suba o PostgreSQL (porta 5433)", saida.ToString());
            Assert.Contains("Worker e filas", saida.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("senha-secreta-do-teste", saida.ToString());
            Assert.DoesNotContain("Password", saida.ToString());
        }
        finally
        {
            configuracao["ConnectionStrings:Default"] = original;
        }
    }

    [Fact]
    public async Task Uso_invalido_do_comando_responde_64_e_limites_invalidos_impedem_a_partida()
    {
        Assert.Equal(StatusCommand.UsageExitCode, await StatusCommand.RunAsync(["--bogus"], new StringWriter()));
        Assert.Equal(StatusCommand.UsageExitCode, await StatusCommand.RunAsync(["--skip", "banco"], new StringWriter()));
        Assert.Equal(StatusCommand.UsageExitCode, await StatusCommand.RunAsync(["--tenant"], new StringWriter()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var api = await ApiServer.StartAsync("Production", Configuracao(k => k["Health:EventStuckAfter"] = "00:00:00"));
        });
    }
}
