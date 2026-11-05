using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;

namespace IaBimeta.Worker;

/// <summary>
/// O executor dos turnos de IA (ADR 0017, seção G.2), hospedado no Worker no molde do reconciliador do ContactCenter: a cada ciclo procura os turnos DEVIDOS (agendado vencido, pronto vencido, em andamento
/// com o lease vencido), abre um contexto de SISTEMA por tenant (o tenant vem do turno gravado pelo módulo, nunca de payload), reivindica até o teto de turnos simultâneos do tenant (contado no banco) e
/// roda cada turno em escopo próprio, em paralelo limitado. Um turno de um tenant nunca espera o de outro, e a falha de um não derruba os demais. Nada aqui lê conteúdo de conversa: só o
/// <see cref="TurnRunner"/>, dentro do escopo do turno.
/// </summary>
internal sealed class AgentsTurnExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessagingScopeInitializer _initializer;
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentsTurnExecutor> _logger;
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public AgentsTurnExecutor(IServiceScopeFactory scopes, IMessagingScopeInitializer initializer, AgentsOptions options, TimeProvider clock, ILogger<AgentsTurnExecutor> logger)
    {
        _scopes = scopes;
        _initializer = initializer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Um ciclo. Devolve quantos turnos reivindicou e rodou, somando os tenants.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var escopo = _scopes.CreateAsyncScope())
        {
            tenants = await escopo.ServiceProvider.GetRequiredService<IPendingTurnSource>()
                .ListTenantsWithDueTurnsAsync(_clock.GetUtcNow(), _options.ExecutorBatchSize, cancellationToken);
        }

        if (tenants.Count == 0)
        {
            return 0;
        }

        // Reivindica por tenant (cada um no próprio contexto), até o teto do ciclo.
        var trabalho = new List<(Guid Tenant, Guid Turn)>();
        foreach (var tenant in tenants)
        {
            var restante = _options.ExecutorBatchSize - trabalho.Count;
            if (restante <= 0)
            {
                break;
            }

            try
            {
                await using var escopo = _scopes.CreateAsyncScope();
                _initializer.Initialize(escopo, new TenantId(tenant), "agents-executor");
                var reivindicados = await escopo.ServiceProvider.GetRequiredService<IAiTurnStore>().ClaimDueAsync(_owner, restante, cancellationToken);
                trabalho.AddRange(reivindicados.Select(t => (tenant, t.Id)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Só o tipo: a mensagem pode carregar dado. O tenant seguinte e o próximo ciclo continuam.
                _logger.LogError("A reivindicação de turnos de IA falhou para o tenant {TenantId}: {ErrorType}.", tenant, ex.GetType().Name);
            }
        }

        await Parallel.ForEachAsync(
            trabalho,
            new ParallelOptions { MaxDegreeOfParallelism = _options.ExecutorParallelism, CancellationToken = cancellationToken },
            async (item, token) => await RunTurnAsync(item.Tenant, item.Turn, token));

        return trabalho.Count;
    }

    private async Task RunTurnAsync(Guid tenant, Guid turnId, CancellationToken cancellationToken)
    {
        try
        {
            await using var escopo = _scopes.CreateAsyncScope();
            _initializer.Initialize(escopo, new TenantId(tenant), "agents-executor");
            var resultado = await escopo.ServiceProvider.GetRequiredService<TurnRunner>().RunAsync(turnId, cancellationToken);
            _logger.LogInformation("Turno de IA {TurnId} do tenant {TenantId}: {Outcome} ({Code}).", turnId, tenant, resultado.Outcome, resultado.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // O lease vence e o turno é reivindicado de novo (gastando uma tentativa): a falha de um turno nunca derruba os outros.
            _logger.LogError("O turno de IA {TurnId} do tenant {TenantId} falhou: {ErrorType}.", turnId, tenant, ex.GetType().Name);
        }
    }
}

/// <summary>
/// O reconciliador do Agents (CON-15): para cada tenant que usa a IA, olha as sessões abertas mais recentes e cria o turno das que têm a IA no comando e mensagens sem resposta, mas nenhum turno aberto
/// (evento perdido ou morto). Usa a consulta de sessões do Conversations, sem contrato novo, e só as sessões cuja última mensagem já passou da idade mínima (não disputa com o handler do evento). Não
/// considera estados de envio: falha de envio nunca cria turno de IA (I10).
/// </summary>
internal sealed class AgentsReconciler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessagingScopeInitializer _initializer;
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentsReconciler> _logger;

    public AgentsReconciler(IServiceScopeFactory scopes, IMessagingScopeInitializer initializer, AgentsOptions options, TimeProvider clock, ILogger<AgentsReconciler> logger)
    {
        _scopes = scopes;
        _initializer = initializer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Um ciclo. Devolve quantos turnos órfãos criou, somando os tenants.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var escopo = _scopes.CreateAsyncScope())
        {
            tenants = await escopo.ServiceProvider.GetRequiredService<IPendingTurnSource>().ListTenantsWithTurnsAsync(_options.ReconcileTenantBatchSize, cancellationToken);
        }

        var criados = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var escopo = _scopes.CreateAsyncScope();
                _initializer.Initialize(escopo, new TenantId(tenant), "agents-reconciler");
                criados += await ReconcileTenantAsync(escopo.ServiceProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Só o tipo: a mensagem pode carregar dado. O tenant seguinte e o próximo ciclo continuam.
                _logger.LogError("A reconciliação do Agents falhou para o tenant {TenantId}: {ErrorType}.", tenant, ex.GetType().Name);
            }
        }

        return criados;
    }

    private async Task<int> ReconcileTenantAsync(IServiceProvider servicos, CancellationToken cancellationToken)
    {
        var limite = _clock.GetUtcNow() - _options.ReconcileMinimumAge;
        var sessoes = await servicos.GetRequiredService<IConversationQueries>()
            .ListSessionsAsync(new ListSessionsQuery(_options.ReconcileSessionsPerTenant, null, SessionStatus.Open), cancellationToken);
        var agendador = servicos.GetRequiredService<TurnScheduler>();

        var criados = 0;
        foreach (var sessao in sessoes.Items.Where(s => s.HandlingMode == HandlingMode.AiAutonomous && s.LastInboundSeq > s.AnsweredThroughSeq && s.LastMessageAt <= limite))
        {
            try
            {
                if (await agendador.EnsureTurnForPendingAsync(sessao.Id, cancellationToken))
                {
                    criados++;
                }
            }
            catch (AiTurnConflictException)
            {
                // Outro agente criou o turno da sessão ao mesmo tempo: é o que se queria.
            }
        }

        return criados;
    }
}

/// <summary>O laço do executor de turnos (ver <see cref="AgentsTurnExecutor"/>): o intervalo é curto, porque a espera do agrupamento é de segundos.</summary>
internal sealed class AgentsTurnExecutorService : BackgroundService
{
    private readonly AgentsTurnExecutor _executor;
    private readonly AgentsOptions _options;
    private readonly ILogger<AgentsTurnExecutorService> _logger;

    public AgentsTurnExecutorService(AgentsTurnExecutor executor, AgentsOptions options, ILogger<AgentsTurnExecutorService> logger)
    {
        _executor = executor;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var relogio = new PeriodicTimer(_options.ExecutorInterval);
        try
        {
            do
            {
                try
                {
                    await _executor.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // O laço nunca morre por uma falha do ciclo (banco fora do ar): registra o tipo e tenta no próximo.
                    _logger.LogError("O executor de turnos de IA falhou neste ciclo: {ErrorType}.", ex.GetType().Name);
                }
            }
            while (await relogio.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal do host.
        }
    }
}

/// <summary>O laço da reconciliação do Agents (ver <see cref="AgentsReconciler"/>).</summary>
internal sealed class AgentsReconciliationService : BackgroundService
{
    private readonly AgentsReconciler _reconciler;
    private readonly AgentsOptions _options;
    private readonly ILogger<AgentsReconciliationService> _logger;

    public AgentsReconciliationService(AgentsReconciler reconciler, AgentsOptions options, ILogger<AgentsReconciliationService> logger)
    {
        _reconciler = reconciler;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var relogio = new PeriodicTimer(_options.ReconcileInterval);
        try
        {
            do
            {
                try
                {
                    var criados = await _reconciler.RunOnceAsync(stoppingToken);
                    if (criados > 0)
                    {
                        _logger.LogInformation("Reconciliação do Agents: {Created} turnos órfãos criados.", criados);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError("A reconciliação do Agents falhou neste ciclo: {ErrorType}.", ex.GetType().Name);
                }
            }
            while (await relogio.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal do host.
        }
    }
}
