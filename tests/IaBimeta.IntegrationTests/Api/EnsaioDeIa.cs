using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>Um "servidor da OpenAI" por HTTP de verdade, em porta local aleatória (loopback), que responde pelo roteiro de <see cref="ServidorOpenAiSimulado"/>. Nenhuma rede, nenhuma chave real.</summary>
internal sealed class ServidorOpenAiLoopback : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpMessageInvoker _invoker;

    private ServidorOpenAiLoopback(WebApplication app, ServidorOpenAiSimulado roteiros)
    {
        _app = app;
        Roteiros = roteiros;
        _invoker = new HttpMessageInvoker(roteiros);
    }

    /// <summary>Os roteiros por credencial e as capturas do que a "OpenAI" recebeu.</summary>
    public ServidorOpenAiSimulado Roteiros { get; }

    /// <summary>O endereço base que o adaptador usa em <c>AiGateway:Providers:OpenAi:BaseAddress</c> (HTTP só é aceito em laço local).</summary>
    public string EnderecoBase { get; private set; } = string.Empty;

    public static async Task<ServidorOpenAiLoopback> IniciarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var roteiros = new ServidorOpenAiSimulado();
        var servidor = new ServidorOpenAiLoopback(app, roteiros);
        app.MapPost("/v1/responses", servidor.AtenderAsync);
        await app.StartAsync();

        var endereco = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        servidor.EnderecoBase = endereco.TrimEnd('/') + "/v1/";
        return servidor;
    }

    private async Task AtenderAsync(HttpContext http)
    {
        var corpo = await new StreamReader(http.Request.Body, Encoding.UTF8).ReadToEndAsync(http.RequestAborted);
        var pedido = new HttpRequestMessage(HttpMethod.Post, "http://openai.simulada/v1/responses") { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        if (http.Request.Headers.TryGetValue("Authorization", out var autorizacao))
        {
            pedido.Headers.TryAddWithoutValidation("Authorization", autorizacao.ToString());
        }

        if (http.Request.Headers.TryGetValue("X-Client-Request-Id", out var id))
        {
            pedido.Headers.TryAddWithoutValidation("X-Client-Request-Id", id.ToString());
        }

        using var resposta = await _invoker.SendAsync(pedido, http.RequestAborted);
        http.Response.StatusCode = (int)resposta.StatusCode;
        foreach (var cabecalho in resposta.Headers)
        {
            http.Response.Headers[cabecalho.Key] = cabecalho.Value.ToArray();
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(await resposta.Content.ReadAsStringAsync(http.RequestAborted), http.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _invoker.Dispose();
    }
}

/// <summary>Guarda tudo o que o Api e o Worker registram em log, para provar que a chave do provedor nunca aparece.</summary>
internal sealed class CapturaDeLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _linhas = new();

    public IReadOnlyList<string> Linhas => _linhas.ToList();

    public ILogger CreateLogger(string categoryName) => new Registrador(categoryName, _linhas);

    public void Dispose()
    {
    }

    private sealed class Registrador(string categoria, ConcurrentQueue<string> linhas) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            linhas.Enqueue($"{categoria} [{logLevel}] {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// O ensaio do primeiro teste de IA pelo simulador, com tudo de verdade menos o provedor: o Api real (HTTP), o Worker real (relay, executor de turnos), PostgreSQL, AiGateway com a chave BYOK cifrada,
/// o adaptador REAL da OpenAI e um "servidor da OpenAI" local que responde por roteiro. É exatamente o fluxo que o desenvolvedor roda à mão, só que a OpenAI é trocada por um servidor de laço local.
/// </summary>
internal sealed class EnsaioDeIa : IAsyncDisposable
{
    public const string ChaveCanario = "sk-canario-do-ensaio-0000000000000000000000";
    public const string ModeloDoEnsaio = "modelo-do-ensaio";

    private readonly PostgresFixture _banco;
    private readonly Dictionary<string, string?> _configuracaoDoApi;
    private readonly Func<IHost> _construirWorker;
    private readonly List<IHost> _workersExtras = new();

    private EnsaioDeIa(PostgresFixture banco, string slug, ServidorOpenAiLoopback openAi, ApiServer api, IHost worker, CapturaDeLogs logs, Dictionary<string, string?> configuracaoDoApi, Func<IHost> construirWorker)
    {
        _banco = banco;
        _configuracaoDoApi = configuracaoDoApi;
        _construirWorker = construirWorker;
        Slug = slug;
        OpenAi = openAi;
        Api = api;
        Worker = worker;
        Logs = logs;
    }

    public string Slug { get; }

    public ServidorOpenAiLoopback OpenAi { get; }

    public ApiServer Api { get; }

    public IHost Worker { get; private set; }

    public CapturaDeLogs Logs { get; }

    /// <summary>Sobe o ensaio. Sem <paramref name="modelo"/>, o tenant fica sem modelo (a preparação não inventa um).</summary>
    public static async Task<EnsaioDeIa> IniciarAsync(PostgresFixture banco, string prefixo, string? modelo = ModeloDoEnsaio, bool comChaveMestra = true, IReadOnlyDictionary<string, string?>? adicionais = null)
    {
        await LimparPendenciasAsync(banco);

        var slug = $"{prefixo}-{Guid.NewGuid().ToString("N")[..8]}";
        var openAi = await ServidorOpenAiLoopback.IniciarAsync();
        var logs = new CapturaDeLogs();

        var catalogo = new Dictionary<string, string?>
        {
            ["AiGateway:Catalog:Models:2:ProviderId"] = "openai",
            ["AiGateway:Catalog:Models:2:ModelId"] = ModeloDoEnsaio,
            ["AiGateway:Catalog:Models:2:Profile"] = "atendimento-rapido",
            ["AiGateway:Catalog:Models:2:ContextWindowTokens"] = "400000",
            ["AiGateway:Catalog:Models:2:MaxOutputTokens"] = "128000",
            ["AiGateway:Catalog:Models:2:Currency"] = "USD",
            ["AiGateway:Catalog:Models:2:InputPerMillion"] = "1.00",
            ["AiGateway:Catalog:Models:2:CachedInputPerMillion"] = "0.10",
            ["AiGateway:Catalog:Models:2:CacheWritePerMillion"] = "1.00",
            ["AiGateway:Catalog:Models:2:OutputPerMillion"] = "4.00",
            ["AiGateway:Catalog:Models:2:Status"] = "Active",
            ["AiGateway:Providers:OpenAi:BaseAddress"] = openAi.EnderecoBase,
        };
        if (comChaveMestra)
        {
            catalogo["AiGateway:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeIADeTeste;
            catalogo[$"AiGateway:Secrets:Keys:{PostgresFixture.IdDaChaveDeIADeTeste}"] = PostgresFixture.ChaveDeCifraDeIADeTeste;
        }

        // O Api: o mesmo que o desenvolvedor sobe com o perfil simulador-ia.
        var configuracaoDoApi = ApiServer.ConfiguracaoDeDesenvolvimento();
        configuracaoDoApi["Tenancy:Development:ApplyMigrations"] = "true";
        configuracaoDoApi["Tenancy:Development:SimulatorEnabled"] = "true";
        configuracaoDoApi["Tenancy:Development:SeedTenants:0:Slug"] = slug;
        configuracaoDoApi["Tenancy:Development:SeedTenants:0:Name"] = $"Tenant do ensaio {slug}";
        configuracaoDoApi["Tenancy:Development:Ai:Enabled"] = "true";
        configuracaoDoApi["Tenancy:Development:Ai:TenantSlug"] = slug;
        configuracaoDoApi["Tenancy:Development:Ai:ModelId"] = modelo;
        foreach (var (chave, valor) in catalogo)
        {
            configuracaoDoApi[chave] = valor;
        }

        foreach (var (chave, valor) in adicionais ?? new Dictionary<string, string?>())
        {
            configuracaoDoApi[chave] = valor;
        }

        var api = await ApiServer.StartAsync("Development", configuracaoDoApi, servicos: servicos => servicos.AddSingleton<ILoggerProvider>(logs));

        // O Worker: janelas curtas para o ensaio não esperar de verdade. O reconciliador fica desligado na prática (idade mínima de dez anos): ele olharia sessões antigas de outros testes.
        var configuracaoDoWorker = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = banco.ConnectionString,
            ["Messaging:PollInterval"] = "00:00:00.200",
            ["Agents:Options:QuietWindow"] = "00:00:00.300",
            ["Agents:Options:MaxWait"] = "00:00:01",
            ["Agents:Options:ExecutorInterval"] = "00:00:00.200",
            ["Agents:Options:RetryBackoff"] = "00:00:00.300",
            ["Agents:Options:MaxRetryBackoff"] = "00:00:01",
            ["Agents:Options:CapacityRetry"] = "00:00:00.300",
            ["Agents:Options:CleanShutdownRetry"] = "00:00:00.200",
            ["Agents:Options:ModelCallTimeout"] = "00:00:10",
            ["Agents:Options:TurnMaxDuration"] = "00:00:30",
            ["Agents:Options:ReconcileMinimumAge"] = "3650.00:00:00",
            ["Worker:ReconcileInterval"] = "00:00:01",
            ["Channels:Secrets:ActiveKeyId"] = PostgresFixture.IdDaChaveDeTeste,
            [$"Channels:Secrets:Keys:{PostgresFixture.IdDaChaveDeTeste}"] = PostgresFixture.ChaveDeCifraDeTeste,
        };
        foreach (var (chave, valor) in catalogo)
        {
            configuracaoDoWorker[chave] = valor;
        }

        foreach (var (chave, valor) in adicionais ?? new Dictionary<string, string?>())
        {
            configuracaoDoWorker[chave] = valor;
        }

        IHost ConstruirWorker() => WorkerHost.Build(
            new[] { "--environment=Development" },
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(configuracaoDoWorker);
                builder.Logging.AddProvider(logs);
            });

        var worker = ConstruirWorker();
        await worker.StartAsync();

        return new EnsaioDeIa(banco, slug, openAi, api, worker, logs, configuracaoDoApi, ConstruirWorker);
    }

    /// <summary>
    /// O Worker leva o outbox INTEIRO e os turnos devidos de TODOS os tenants do banco compartilhado: o que sobrou de outros testes é marcado como processado/cancelado antes de começar, como a fixture faz,
    /// para o ensaio ver só o que ele mesmo gera. Nenhuma linha é apagada.
    /// </summary>
    private static async Task LimparPendenciasAsync(PostgresFixture banco)
    {
        await using var escopo = banco.ServicosComAgentes.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<AgentsDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agents.ai_turns SET state = 7, end_reason = 'test-reset', ended_at = now(), next_attempt_at = NULL, lane_token = NULL, claimed_by = NULL, lease_expires_at = NULL, " +
            "version = version + 1, updated_at = now() WHERE state IN (1, 2, 3)");
        await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE conversations.outbox_message SET state = 3, processed_at = now(), lease_token = NULL, lease_owner = NULL, lease_expires_at = NULL WHERE state IN (1, 2)");

        // Os eventos MORTOS de outros testes, sem tratamento, seriam tratados pelo Worker do ensaio (que varre os mortos de TODOS os tenants): ficam marcados como tratados, para o ensaio ver só o que ele gera.
        await escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE conversations.outbox_message SET processed_at = now() WHERE state = 4 AND processed_at IS NULL");
    }

    /// <summary>
    /// Reinicia o Worker (o processo que cai e sobe de novo): para o atual (o que está em andamento é cancelado, sem concluir) e sobe outro com a MESMA configuração. O que ficou no banco (leases, despachos em
    /// andamento) é tudo o que o novo Worker tem para decidir.
    /// </summary>
    public async Task ReiniciarWorkerAsync()
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Worker.StopAsync(limite.Token);
        Worker.Dispose();
        Worker = _construirWorker();
        await Worker.StartAsync();
    }

    /// <summary>
    /// Sobe OUTRO Worker com a mesma configuração, sem parar o atual (dois processos de Worker ao mesmo tempo, como numa implantação com mais de uma instância). É descartado com o ensaio.
    /// </summary>
    public async Task<IHost> SubirOutroWorkerAsync()
    {
        var outro = _construirWorker();
        await outro.StartAsync();
        _workersExtras.Add(outro);
        return outro;
    }

    /// <summary>Um segundo Api com a MESMA configuração (o desenvolvedor que reinicia o Api): a preparação roda de novo e não pode duplicar nada.</summary>
    public Task<ApiServer> SegundoApiAsync() => ApiServer.StartAsync("Development", new Dictionary<string, string?>(_configuracaoDoApi));

    /// <summary>Roda a ação com o contexto de sistema do tenant do ensaio, na composição de testes (PostgreSQL real): para olhar o que o Api não expõe, como o item da fila do ContactCenter.</summary>
    public async Task<T> ComoSistemaAsync<T>(Func<IServiceProvider, Task<T>> acao)
    {
        await using var consulta = _banco.NovoEscopo();
        var tenant = await consulta.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(Slug)
            ?? throw new InvalidOperationException("O tenant do ensaio não existe.");
        await using var escopo = _banco.NovoEscopoComAgentes(new RequestContext(tenant.TenantId, "ensaio"));
        return await acao(escopo.ServiceProvider);
    }

    // O simulador, como o script o chama

    private HttpRequestMessage Requisicao(HttpMethod metodo, string caminho, object? corpo = null) =>
        Api.Requisicao(metodo, caminho, Slug, corpo is null ? null : JsonContent.Create(corpo));

    public async Task<JsonNode> LerAsync(string caminho)
    {
        using var resposta = await Api.Client.SendAsync(Requisicao(HttpMethod.Get, caminho));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!;
    }

    public Task<JsonNode> StatusAsync() => LerAsync("/simulator/ai/status");

    /// <summary>Cadastra a chave pela rota do simulador. Devolve a resposta e o texto bruto dela (o teste prova que a chave não volta).</summary>
    public async Task<(HttpStatusCode Status, string Corpo)> CadastrarChaveAsync(string chave = ChaveCanario)
    {
        using var resposta = await Api.Client.SendAsync(Requisicao(HttpMethod.Put, "/simulator/ai/credential", new { apiKey = chave }));
        return (resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
    }

    /// <summary>A mensagem do cliente pelo simulador. Devolve a sessão em que ela caiu.</summary>
    public async Task<Guid> EntrarAsync(string contato, string texto)
    {
        var organizacao = (await StatusAsync())["organizationId"]!.GetValue<Guid>();
        using var resposta = await Api.Client.SendAsync(Requisicao(
            HttpMethod.Post,
            "/simulator/inbound",
            new { contactRef = contato, contactName = contato, organizationId = organizacao, text = texto }));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return JsonNode.Parse(await resposta.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<Guid>();
    }

    public Task<JsonNode> SessaoAsync(Guid sessao) => LerAsync($"/simulator/sessions/{sessao}");

    public async Task<IReadOnlyList<JsonNode>> MensagensAsync(Guid sessao) => (await SessaoAsync(sessao))["messages"]!.AsArray().Select(m => m!).ToList();

    /// <summary>As respostas que a IA registrou na sessão (autor IA), em ordem.</summary>
    public async Task<IReadOnlyList<string>> RespostasDaIAAsync(Guid sessao) =>
        (await MensagensAsync(sessao)).Where(m => m["author"]!.GetValue<string>() == "Ai").Select(m => m["text"]!.GetValue<string>()).ToList();

    public async Task<JsonNode> LedgerAsync() => (await LerAsync("/simulator/ai/requests"))["items"]!;

    public async Task<JsonNode> TurnosAsync() => (await LerAsync("/simulator/agents/turns"))["items"]!;

    /// <summary>O humano assume a conversa (a mesma transição que o atendente faria), pela rota do simulador.</summary>
    public async Task AssumirComoHumanoAsync(Guid sessao)
    {
        var versao = (await SessaoAsync(sessao))["session"]!["stateVersion"]!.GetValue<long>();
        using var resposta = await Api.Client.SendAsync(Requisicao(
            HttpMethod.Post,
            "/simulator/handling/transition",
            new { sessionId = sessao, to = "HumanAssisted", responderRef = (Guid?)null, expectedStateVersion = versao, transitionId = Guid.NewGuid(), reason = "ensaio-humano-assume" }));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    public static async Task AguardarAsync(Func<Task<bool>> condicao, string descricao, int segundos = 30)
    {
        var limite = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(segundos);
        while (DateTimeOffset.UtcNow < limite)
        {
            if (await condicao())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"O ensaio não concluiu em {segundos} s: {descricao}.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var extra in _workersExtras)
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await extra.StopAsync(limite.Token);
            extra.Dispose();
        }

        await Worker.StopAsync();
        Worker.Dispose();
        await Api.DisposeAsync();
        await OpenAi.DisposeAsync();
    }
}
