using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Identity.Domain;
using IaBimeta.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O expurgo técnico AGENDADO no Worker (ADR 0026), com Worker real e PostgreSQL real: na hora marcada ele remove sozinho só o dado técnico vencido da empresa (prazo por empresa), deixa intactos os mortos, o vigente e
/// todo dado pessoal (mensagens, contatos, atendimentos), executa uma vez por dia e, desligado, não faz nada. O prazo padrão fica no máximo para que só a empresa do teste, com prazo próprio, seja alcançada no banco compartilhado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetencaoAgendadaTests(PostgresFixture banco)
{
    private IHost SubirWorker(string slug, bool agendado, ILoggerProvider? logs = null, Action<Dictionary<string, string?>>? ajustar = null)
    {
        var configuracao = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = banco.ConnectionString,
            ["Messaging:PollInterval"] = "00:00:00.200",

            // O Agents também sobe neste host: o agrupamento longo impede que o executor leve turnos de outros testes.
            ["Agents:Options:QuietWindow"] = "01:00:00",
            ["Agents:Options:MaxWait"] = "01:00:00",
            ["Worker:ReconcileInterval"] = "01:00:00",
            ["Channels:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeTeste,
            [$"Channels:Secrets:Keys:{PostgresFixture.IdDaChaveDeTeste}"] = PostgresFixture.ChaveDeCifraDeTeste,

            // Prazos padrão no teto: nada do banco compartilhado é alcançado; só a empresa do teste tem prazo curto.
            ["Retention:Days:Sessions"] = "3650",
            ["Retention:Days:PasswordResetTokens"] = "3650",
            ["Retention:Days:LoginThrottles"] = "3650",
            ["Retention:Days:ProcessedEvents"] = "3000",
            ["Retention:Days:InboxMarkers"] = "3650",
            [$"Retention:Tenants:{slug}:Sessions"] = "10",
            [$"Retention:Tenants:{slug}:ProcessedEvents"] = "10",
            ["Retention:Schedule:Enabled"] = agendado ? "true" : "false",
            ["Retention:Schedule:TimeOfDayUtc"] = "00:00:00", // sempre já passou: a execução fica devida na primeira conferência
            ["Retention:Schedule:CheckInterval"] = "00:00:00.200",
        };
        ajustar?.Invoke(configuracao);
        return WorkerHost.Build(new[] { "--environment=Development" }, builder =>
        {
            builder.Configuration.AddInMemoryCollection(configuracao);
            if (logs is not null)
            {
                builder.Logging.AddProvider(logs);
            }
        });
    }

    private static DateTimeOffset HaDias(double dias) => DateTimeOffset.UtcNow.AddDays(-dias);

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

    private static Task InserirSessaoAsync(Guid tenant, Guid usuario, DateTimeOffset venceEm) => ExecutarAsync(
        "INSERT INTO identity.sessions (tenant_id, id, user_id, token_hash, created_at, expires_at) VALUES (@t, @id, @u, @hash, @criada, @vence)",
        ("t", tenant), ("id", Guid.NewGuid()), ("u", usuario), ("hash", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), ("criada", venceEm.AddHours(-8)), ("vence", venceEm));

    private static Task InserirEventoAsync(Guid tenant, short estado, DateTimeOffset quando) => ExecutarAsync(
        "INSERT INTO conversations.outbox_message (tenant_id, id, ordering_key, event_type, event_version, correlation_id, occurred_at, payload, state, attempts, next_attempt_at, created_at, processed_at) " +
        "VALUES (@t, @id, 'k', 'teste.evento', 1, @id, @q, '{}'::jsonb, @estado, 0, @q, @q, @processado)",
        ("t", tenant), ("id", Guid.NewGuid()), ("q", quando), ("estado", estado), ("processado", estado is 3 or 4 ? quando : null));

    private static async Task EsperarAsync(Func<Task<bool>> condicao, string descricao, int segundos = 30)
    {
        var limite = DateTimeOffset.UtcNow.AddSeconds(segundos);
        while (DateTimeOffset.UtcNow < limite)
        {
            if (await condicao())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"O Worker não concluiu em {segundos} s: {descricao}.");
    }

    [Fact]
    public async Task O_Worker_agendado_expurga_sozinho_so_o_tecnico_vencido_da_empresa_executa_uma_vez_por_dia_e_nao_toca_dado_pessoal()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "ragenda");
        var t = c.Tenant.TenantId.Value;
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        await c.EntrarAsync("5511999990101", "Mensagem do cliente: dado pessoal que NUNCA sai.");

        await InserirSessaoAsync(t, usuario.Id, HaDias(15)); // vencida há 15 dias: passa dos 10 da empresa
        await InserirSessaoAsync(t, usuario.Id, DateTimeOffset.UtcNow.AddHours(8)); // vigente
        await InserirEventoAsync(t, 3, HaDias(15)); // processado há 15 dias
        await InserirEventoAsync(t, 4, HaDias(400)); // MORTO: nunca sai
        var mensagens = await ContarAsync("SELECT count(*) FROM conversations.messages WHERE tenant_id = @t", ("t", t));
        var contatos = await ContarAsync("SELECT count(*) FROM conversations.contacts WHERE tenant_id = @t", ("t", t));
        var atendimentos = await ContarAsync("SELECT count(*) FROM conversations.service_sessions WHERE tenant_id = @t", ("t", t));
        var usuarios = await ContarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t", ("t", t));
        Assert.True(mensagens >= 1 && contatos >= 1 && atendimentos >= 1);

        using var worker = SubirWorker(c.Tenant.Slug, agendado: true);
        await worker.StartAsync();
        try
        {
            await EsperarAsync(
                async () => await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t", ("t", t)) == 1,
                "a sessão vencida da empresa deveria ser expurgada na hora marcada");
            await EsperarAsync(
                async () => await ContarAsync("SELECT count(*) FROM conversations.outbox_message WHERE tenant_id = @t AND state = 3", ("t", t)) == 0,
                "o evento processado vencido deveria ser expurgado");

            // Preservado: a sessão vigente, o morto, e TODO dado pessoal.
            Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t AND expires_at > now()", ("t", t)));
            Assert.Equal(1, await ContarAsync("SELECT count(*) FROM conversations.outbox_message WHERE tenant_id = @t AND state = 4", ("t", t)));
            Assert.Equal(mensagens, await ContarAsync("SELECT count(*) FROM conversations.messages WHERE tenant_id = @t", ("t", t)));
            Assert.Equal(contatos, await ContarAsync("SELECT count(*) FROM conversations.contacts WHERE tenant_id = @t", ("t", t)));
            Assert.Equal(atendimentos, await ContarAsync("SELECT count(*) FROM conversations.service_sessions WHERE tenant_id = @t", ("t", t)));
            Assert.Equal(usuarios, await ContarAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @t", ("t", t)));

            // Uma vez por dia: depois do sucesso de hoje, uma nova sessão vencida fica até a execução de amanhã.
            await InserirSessaoAsync(t, usuario.Id, HaDias(15));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(2, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t", ("t", t)));
        }
        finally
        {
            await worker.StopAsync();
        }
    }

    [Fact]
    public async Task Desligado_o_Worker_nao_expurga_nada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rdeslig");
        var t = c.Tenant.TenantId.Value;
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        await InserirSessaoAsync(t, usuario.Id, HaDias(15));
        await InserirEventoAsync(t, 3, HaDias(15));
        var logs = new CapturaDeLogs();

        using var worker = SubirWorker(c.Tenant.Slug, agendado: false, logs);
        await worker.StartAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await worker.StopAsync();
        }

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t", ("t", t)));
        Assert.Contains(logs.Linhas, l => l.Contains("O expurgo técnico agendado está desligado"));
    }

    [Fact]
    public async Task Falha_do_expurgo_agendado_e_registrada_sem_dado_de_empresa_e_nao_derruba_o_Worker()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "rfalha");
        var t = c.Tenant.TenantId.Value;
        var usuario = await banco.CriarUsuarioAsync(c.Tenant, c.OrganizacaoId, Role.User);
        await InserirSessaoAsync(t, usuario.Id, HaDias(15));
        var logs = new CapturaDeLogs();

        // A empresa citada na política não existe: o expurgo falha fechado (nunca aplica um prazo suposto).
        using var worker = SubirWorker(c.Tenant.Slug, agendado: true, logs, k => k["Retention:Tenants:empresa-que-nao-existe-segredo:Sessions"] = "2");
        await worker.StartAsync();
        try
        {
            await EsperarAsync(async () => logs.Linhas.Any(l => l.Contains("O expurgo técnico agendado falhou (InvalidOperationException)")), "a falha deveria ser registrada");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await worker.StopAsync();
        }

        Assert.Equal(1, await ContarAsync("SELECT count(*) FROM identity.sessions WHERE tenant_id = @t", ("t", t))); // nada foi removido
        Assert.DoesNotContain(logs.Linhas, l => l.Contains("empresa-que-nao-existe-segredo")); // só o tipo da falha vai ao log
        Assert.Single(logs.Linhas, l => l.Contains("O expurgo técnico agendado falhou")); // e a nova tentativa espera o RetryAfter (15 min), não gira
    }
}
