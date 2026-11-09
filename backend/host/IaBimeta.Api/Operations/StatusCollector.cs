using System.Diagnostics;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Audit.Infrastructure;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Infrastructure;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.Api.Operations;

internal sealed record StatusLine(string Section, HealthSeverity Severity, string Message, string? Action = null, string? Tenant = null);

internal sealed record StatusReport(DateTimeOffset At, IReadOnlyList<StatusLine> Lines)
{
    public HealthSeverity Overall => Lines.Count == 0 ? HealthSeverity.Ok : Lines.Max(l => l.Severity);
}

internal sealed record StatusOptions(string? TenantSlug, string? ApiUrl, bool CheckApi = true, bool CheckWorker = true)
{
    public const string DefaultApiUrl = "http://localhost:5155/health";
}

/// <summary>
/// Reúne o estado do ambiente em linhas com severidade e ação: PostgreSQL (conexão, versão, tamanho, conexões), migrations de cada módulo, Api (HTTP), Worker (atividade das conexões dele no banco) e as sondas de saúde
/// dos módulos (eventos parados, contas pausadas, turnos vencidos, conversas esperando). Somente leitura. Cada verificação que falha vira uma linha crítica, nunca uma exceção para quem opera.
/// </summary>
internal static class StatusCollector
{
    public const string WorkerApplicationName = "IaBimeta.Worker";

    public static async Task<StatusReport> CollectAsync(IServiceProvider root, StatusOptions options, CancellationToken cancellationToken = default)
    {
        var agora = root.GetRequiredService<TimeProvider>().GetUtcNow();
        var limites = root.GetRequiredService<HealthThresholds>();
        var linhas = new List<StatusLine>();

        var bancoOk = await CheckDatabaseAsync(root, linhas, cancellationToken);
        if (bancoOk)
        {
            await CheckMigrationsAsync(root, linhas, cancellationToken);
        }

        if (options.CheckApi)
        {
            await CheckApiAsync(options.ApiUrl ?? StatusOptions.DefaultApiUrl, linhas, cancellationToken);
        }

        if (!bancoOk)
        {
            linhas.Add(new StatusLine("Worker e filas", HealthSeverity.Critical, "não verificados: o PostgreSQL está inacessível.", "Restabeleça o PostgreSQL primeiro."));
            return new StatusReport(agora, linhas);
        }

        if (options.CheckWorker)
        {
            await CheckWorkerAsync(root, limites, linhas, cancellationToken);
        }

        Guid? so = null;
        if (options.TenantSlug is not null)
        {
            await using var escopo = root.CreateAsyncScope();
            var tenant = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(options.TenantSlug.Trim(), cancellationToken);
            if (tenant is null)
            {
                linhas.Add(new StatusLine("Empresa", HealthSeverity.Critical, $"empresa '{options.TenantSlug}' não encontrada.", "Confira o slug (o mesmo do login)."));
                return new StatusReport(agora, linhas);
            }

            so = tenant.TenantId.Value;
        }

        var contexto = new HealthContext(limites, agora, so);
        var slugs = new Dictionary<Guid, string>();
        foreach (var sonda in root.GetServices<IHealthProbe>().OrderBy(s => s.Component, StringComparer.Ordinal))
        {
            IReadOnlyList<HealthFinding> achados;
            try
            {
                achados = await sonda.CheckAsync(contexto, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Só o tipo: a mensagem de uma falha de banco pode trazer detalhe de conexão.
                linhas.Add(new StatusLine(sonda.Component, HealthSeverity.Critical, $"a verificação falhou ({ex.GetType().Name}).", "Confira o banco e o log do host; rode de novo."));
                continue;
            }

            foreach (var achado in achados)
            {
                string? slug = null;
                if (achado.TenantId is { } id)
                {
                    slug = await SlugOfAsync(root, slugs, id, cancellationToken);
                }

                linhas.Add(new StatusLine(achado.Component, achado.Severity, achado.Message, achado.Action, slug));
            }
        }

        await CheckFaqSavingsAsync(root, agora, so, linhas, slugs, cancellationToken);

        // A retenção é do ambiente todo: numa conferência de UMA empresa não entra.
        if (so is null)
        {
            await CheckRetentionAsync(root, linhas, cancellationToken);
        }

        return new StatusReport(agora, linhas);
    }

    /// <summary>
    /// A economia da resposta direta do FAQ (ADR 0029): a contagem exata de chamadas evitadas e, separada e rotulada, a estimativa de tokens e custo. Informativa: nunca é atenção nem falha (o FAQ direto é
    /// uma melhoria, não um serviço que possa parar). Se a leitura falha, vira atenção com o tipo da falha, sem derrubar o resto do relatório.
    /// </summary>
    private static async Task CheckFaqSavingsAsync(
        IServiceProvider root, DateTimeOffset agora, Guid? so, List<StatusLine> linhas, Dictionary<Guid, string> slugs, CancellationToken cancellationToken)
    {
        try
        {
            var (contagens, medias) = await FaqSavingsReport.LoadAsync(root, agora, so, cancellationToken);
            var obrigatorias = so is { } uma ? new[] { uma } : Array.Empty<Guid>();
            var textos = FaqSavingsReport.Describe(contagens, medias, obrigatorias);
            if (textos.Count == 0)
            {
                linhas.Add(new StatusLine(FaqSavingsReport.Section, HealthSeverity.Ok, $"nenhum turno respondido direto pelo FAQ nos últimos {HealthText.Age(FaqSavingsReport.Window)}."));
                return;
            }

            foreach (var (tenant, mensagem) in textos)
            {
                var slug = tenant is { } id ? await SlugOfAsync(root, slugs, id, cancellationToken) : null;
                linhas.Add(new StatusLine(FaqSavingsReport.Section, HealthSeverity.Ok, mensagem, null, slug));
            }

            // O que há para REVISAR (ADR 0030): as lacunas abertas de cada empresa.
            var abertas = await FaqSavingsReport.LoadOpenGapsAsync(root, so, cancellationToken);
            foreach (var (tenant, mensagem) in FaqSavingsReport.DescribeOpenGaps(abertas))
            {
                var slug = tenant is { } id ? await SlugOfAsync(root, slugs, id, cancellationToken) : null;
                linhas.Add(new StatusLine(FaqSavingsReport.Section, HealthSeverity.Ok, mensagem, null, slug));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            linhas.Add(new StatusLine(FaqSavingsReport.Section, HealthSeverity.Warning, $"a economia do FAQ não pôde ser calculada ({ex.GetType().Name}).", "Confira o banco e rode de novo."));
        }
    }

    /// <summary>
    /// A retenção técnica em modo relatório (nada é alterado): o que está vencido esperando o expurgo, o que está BLOQUEADO (evento morto antigo que impede o expurgo das marcas do Inbox) e se o agendamento do Worker
    /// está ligado. Vencido com o agendamento desligado é atenção: o dado técnico só se acumula.
    /// </summary>
    private static async Task CheckRetentionAsync(IServiceProvider root, List<StatusLine> linhas, CancellationToken cancellationToken)
    {
        const string secao = "Retenção";
        var opcoes = root.GetRequiredService<RetentionOptions>();
        var agenda = opcoes.Schedule.Enabled ? $"expurgo agendado todo dia a partir das {opcoes.Schedule.TimeOfDayUtc:hh\\:mm} UTC" : "agendamento do expurgo DESLIGADO";
        try
        {
            var relatorio = await RetentionRunner.RunAsync(root, dryRun: true, cancellationToken);
            foreach (var bloqueada in relatorio.Results.Where(r => r.Skipped is not null))
            {
                linhas.Add(new StatusLine(secao, HealthSeverity.Warning, $"{bloqueada.Task} [{bloqueada.Pass}]: {bloqueada.Skipped}",
                    "Trate os eventos mortos (reenfileirar ou descartar com autorização) para o expurgo das marcas do Inbox voltar a andar."));
            }

            var vencidas = relatorio.Results.Where(r => r.Skipped is null).Sum(r => r.Eligible);
            if (vencidas > 0 && !opcoes.Schedule.Enabled)
            {
                linhas.Add(new StatusLine(secao, HealthSeverity.Warning, $"{vencidas} linha(s) técnica(s) vencida(s) esperando expurgo; {agenda}.",
                    "Ligue Retention:Schedule:Enabled no Worker ou rode tools\\ops\\Retencao-De-Dados.ps1 -Expurgar -Confirmar (veja docs/operations/RETENCAO-DE-DADOS.md)."));
            }
            else
            {
                linhas.Add(new StatusLine(secao, HealthSeverity.Ok, $"{vencidas} linha(s) técnica(s) vencida(s) esperando expurgo; {agenda}."));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NpgsqlException)
        {
            // A mensagem das falhas de configuração é nossa (cita o slug da configuração, nunca segredo); a de banco, só o tipo.
            var detalhe = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
            linhas.Add(new StatusLine(secao, HealthSeverity.Warning, $"a política de retenção não pôde ser avaliada: {detalhe}", "Corrija a seção Retention da configuração (docs/operations/RETENCAO-DE-DADOS.md) e rode de novo."));
        }
    }

    private static async Task<string> SlugOfAsync(IServiceProvider root, Dictionary<Guid, string> cache, Guid id, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(id, out var conhecido))
        {
            return conhecido;
        }

        await using var escopo = root.CreateAsyncScope();
        var tenant = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByIdAsync(new TenantId(id), cancellationToken);
        var slug = tenant?.Slug ?? id.ToString("N")[..8];
        cache[id] = slug;
        return slug;
    }

    internal static string ConnectionStringOf(IServiceProvider root)
    {
        var configuracao = root.GetRequiredService<IConfiguration>();
        var texto = configuracao.GetConnectionString("Default") ?? configuracao.GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException("Sem string de conexão para o diagnóstico.");
        var builder = new NpgsqlConnectionStringBuilder(texto) { ApplicationName = "IaBimeta.Status", Timeout = 5, CommandTimeout = 10 };
        return builder.ConnectionString;
    }

    private static async Task<bool> CheckDatabaseAsync(IServiceProvider root, List<StatusLine> linhas, CancellationToken cancellationToken)
    {
        try
        {
            await using var conexao = new NpgsqlConnection(ConnectionStringOf(root));
            var relogio = Stopwatch.StartNew();
            await conexao.OpenAsync(cancellationToken);
            await using (var ping = new NpgsqlCommand("SELECT 1", conexao))
            {
                await ping.ExecuteScalarAsync(cancellationToken);
            }

            var latencia = relogio.ElapsedMilliseconds;
            await using var comando = new NpgsqlCommand(
                "SELECT current_setting('server_version'), current_database(), pg_database_size(current_database()), " +
                "(SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()), current_setting('max_connections')::int",
                conexao);
            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);
            await leitor.ReadAsync(cancellationToken);
            var versao = leitor.GetString(0);
            var banco = leitor.GetString(1);
            var tamanho = leitor.GetInt64(2) / 1024.0 / 1024.0;
            var conexoes = leitor.GetInt64(3);
            var maximo = leitor.GetInt32(4);

            var severidade = latencia > 2000 || conexoes * 100 >= maximo * 90 ? HealthSeverity.Warning : HealthSeverity.Ok;
            var texto = $"conexão em {latencia} ms, PostgreSQL {versao}, banco {banco} ({tamanho:F1} MB), {conexoes}/{maximo} conexões.";
            linhas.Add(new StatusLine("PostgreSQL", severidade, texto, severidade == HealthSeverity.Ok ? null : "Latência alta ou conexões perto do limite: confira o servidor e quem mantém conexões abertas."));
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            linhas.Add(new StatusLine("PostgreSQL", HealthSeverity.Critical, $"inacessível ({ex.GetType().Name}).",
                "Suba o PostgreSQL (porta 5433), confira a string de conexão (ConnectionStrings:Default ou Tenancy) e a rede. Nada processa sem o banco."));
            return false;
        }
    }

    private static async Task CheckMigrationsAsync(IServiceProvider root, List<StatusLine> linhas, CancellationToken cancellationToken)
    {
        await using var escopo = root.CreateAsyncScope();
        var sp = escopo.ServiceProvider;
        var contextos = new (string Modulo, DbContext Db)[]
        {
            ("tenancy", sp.GetRequiredService<TenancyDbContext>()),
            ("audit", sp.GetRequiredService<AuditDbContext>()),
            ("identity", sp.GetRequiredService<IdentityDbContext>()),
            ("conversations", sp.GetRequiredService<ConversationsDbContext>()),
            ("channels", sp.GetRequiredService<ChannelsDbContext>()),
            ("contact-center", sp.GetRequiredService<ContactCenterDbContext>()),
            ("ai-gateway", sp.GetRequiredService<AiGatewayDbContext>()),
            ("agents", sp.GetRequiredService<AgentsDbContext>()),
        };

        var pendentes = new List<string>();
        var adiantados = new List<string>();
        try
        {
            foreach (var (modulo, db) in contextos)
            {
                var conhecidas = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                var aplicadas = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
                if (conhecidas.Except(aplicadas).Any())
                {
                    pendentes.Add($"{modulo} ({conhecidas.Except(aplicadas).Count()})");
                }

                if (aplicadas.Except(conhecidas).Any())
                {
                    adiantados.Add(modulo);
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            linhas.Add(new StatusLine("Migrations", HealthSeverity.Critical, $"não foi possível conferir ({ex.GetType().Name}).", "Confira o banco e rode de novo."));
            return;
        }

        if (pendentes.Count > 0)
        {
            linhas.Add(new StatusLine("Migrations", HealthSeverity.Critical, $"migrations pendentes: {string.Join(", ", pendentes)}.",
                "Faça backup (tools\\ops\\Backup-Banco.ps1) e aplique: o comando provision (ou o Api em Development) aplica as migrations. Código novo contra banco antigo falha."));
        }
        else if (adiantados.Count > 0)
        {
            linhas.Add(new StatusLine("Migrations", HealthSeverity.Warning, $"o banco tem migrations que este código não conhece: {string.Join(", ", adiantados)}.",
                "Este código é mais antigo que o banco: atualize o Api e o Worker antes de operar."));
        }
        else
        {
            linhas.Add(new StatusLine("Migrations", HealthSeverity.Ok, $"{contextos.Length} módulos em dia."));
        }
    }

    private static async Task CheckApiAsync(string url, List<StatusLine> linhas, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            linhas.Add(new StatusLine("Api", HealthSeverity.Critical, "a URL de verificação é inválida.", "Use --api-url http://servidor:porta/health (ou --skip api)."));
            return;
        }

        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
            var relogio = Stopwatch.StartNew();
            using var resposta = await http.GetAsync(uri, cancellationToken);
            var tempo = relogio.ElapsedMilliseconds;
            if (resposta.IsSuccessStatusCode)
            {
                linhas.Add(new StatusLine("Api", HealthSeverity.Ok, $"{uri.GetLeftPart(UriPartial.Path)} respondeu {(int)resposta.StatusCode} em {tempo} ms."));
            }
            else
            {
                linhas.Add(new StatusLine("Api", HealthSeverity.Critical, $"{uri.GetLeftPart(UriPartial.Path)} respondeu {(int)resposta.StatusCode}.", "Confira o log do Api."));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            linhas.Add(new StatusLine("Api", HealthSeverity.Critical, $"{uri.GetLeftPart(UriPartial.Path)} não responde ({ex.GetType().Name}).",
                "Suba o Api (ou confira a porta, o firewall e o proxy). Sem o Api o webhook do WhatsApp e a interface ficam fora do ar."));
        }
    }

    /// <summary>
    /// O Worker não tem porta: a vida dele se vê pelo banco. As conexões dele se identificam por <c>Application Name=IaBimeta.Worker</c> e o laço do relay consulta o banco a cada poucos segundos, então a última atividade
    /// (<c>state_change</c>) é recente enquanto ele anda. Sem nenhuma conexão: parado. Com conexões e silêncio longo: travado. (O papel do banco só enxerga as sessões do próprio papel: use o mesmo do Api e do Worker.)
    /// </summary>
    private static async Task CheckWorkerAsync(IServiceProvider root, HealthThresholds limites, List<StatusLine> linhas, CancellationToken cancellationToken)
    {
        try
        {
            await using var conexao = new NpgsqlConnection(ConnectionStringOf(root));
            await conexao.OpenAsync(cancellationToken);
            await using var comando = new NpgsqlCommand(
                "SELECT count(*), COALESCE(extract(epoch FROM now() - max(state_change)), -1) FROM pg_stat_activity WHERE datname = current_database() AND application_name = @nome", conexao);
            comando.Parameters.AddWithValue("nome", WorkerApplicationName);
            await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);
            await leitor.ReadAsync(cancellationToken);
            var conexoes = leitor.GetInt64(0);
            var silencio = TimeSpan.FromSeconds(Math.Max(0, leitor.GetDouble(1)));

            if (conexoes == 0)
            {
                linhas.Add(new StatusLine("Worker", HealthSeverity.Critical, "nenhuma conexão do Worker no banco: o Worker está PARADO.",
                    "Suba o Worker (dotnet run --project backend/host/IaBimeta.Worker). Sem ele nada é entregue: mensagens recebidas ficam no Inbox, a IA não responde e a saída não é enviada. Ao subir, ele retoma tudo sem perda nem duplicação."));
            }
            else if (silencio > limites.WorkerSilenceAfter)
            {
                linhas.Add(new StatusLine("Worker", HealthSeverity.Critical, $"{conexoes} conexão(ões) do Worker, mas sem atividade há {HealthText.Age(silencio)}: travado?",
                    "Confira o log do Worker e reinicie-o: a retomada é segura (entrega pelo menos uma vez, com idempotência)."));
            }
            else
            {
                linhas.Add(new StatusLine("Worker", HealthSeverity.Ok, $"ativo ({conexoes} conexão(ões), última atividade há {HealthText.Age(silencio)})."));
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            linhas.Add(new StatusLine("Worker", HealthSeverity.Critical, $"não foi possível verificar ({ex.GetType().Name}).", "Confira o banco e rode de novo."));
        }
    }
}
