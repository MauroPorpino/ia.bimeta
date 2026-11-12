using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Worker;

/// <summary>
/// A transferência do chatbot visual, mediada pelo host (ADR 0038): o Workflows não referencia o ContactCenter; este adaptador implementa a porta dele sobre o <c>RequestHandoff</c> do ContactCenter (idempotente
/// pelo identificador do pedido). Nenhuma recusa vira sucesso.
/// </summary>
internal sealed class FlowHandoffAdapter : IFlowHandoffPort
{
    private readonly IHandoffRequests _handoff;

    public FlowHandoffAdapter(IHandoffRequests handoff)
    {
        _handoff = handoff;
    }

    public async Task<FlowHandoffResult> RequestAsync(FlowHandoffRequest request, CancellationToken cancellationToken = default)
    {
        var r = await _handoff.RequestHandoffAsync(new RequestHandoffCommand(request.SessionId, request.RequestId, Sanitize(request.ReasonCode), CheckOnly: request.CheckOnly), cancellationToken);
        var resultado = r.Outcome switch
        {
            RequestHandoffOutcome.Completed => FlowHandoffOutcome.Completed,
            RequestHandoffOutcome.AlreadyApplied => FlowHandoffOutcome.AlreadyApplied,
            RequestHandoffOutcome.Pending => FlowHandoffOutcome.Completed,
            RequestHandoffOutcome.Routable => FlowHandoffOutcome.Routable,
            RequestHandoffOutcome.SessionEnded => FlowHandoffOutcome.SessionEnded,
            _ => FlowHandoffOutcome.Refused,
        };
        return new FlowHandoffResult(resultado, r.Code);
    }

    // O código de motivo do ContactCenter: minúsculas, números e _.- (o desenho do fluxo já valida, mas a porta nunca confia).
    private static string Sanitize(string reason)
    {
        var limpo = new string(reason.ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.' or '-').ToArray());
        return limpo.Length is >= 1 and <= 100 ? limpo : "flow_handoff";
    }
}

/// <summary>
/// O executor dos fluxos (ADR 0037 §4), hospedado no Worker no molde do executor de turnos do Agents: a cada ciclo procura as instâncias DEVIDAS (próximo olhar vencido, sem lease vigente), abre um contexto de
/// SISTEMA por tenant (o tenant vem da linha gravada pelo módulo, nunca de payload), reivindica até o teto por tenant e roda cada instância em escopo próprio, em paralelo limitado. Uma instância de um tenant nunca
/// espera a de outro, e a falha de uma não derruba as demais (o lease vence e ela é reivindicada de novo). Só existe com <c>Workflows:Options:Enabled = true</c>.
/// </summary>
internal sealed class FlowExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessagingScopeInitializer _initializer;
    private readonly WorkflowsOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FlowExecutor> _logger;
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public FlowExecutor(IServiceScopeFactory scopes, IMessagingScopeInitializer initializer, WorkflowsOptions options, TimeProvider clock, ILogger<FlowExecutor> logger)
    {
        _scopes = scopes;
        _initializer = initializer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Um ciclo. Devolve quantas instâncias reivindicou e rodou, somando os tenants.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var escopo = _scopes.CreateAsyncScope())
        {
            tenants = await escopo.ServiceProvider.GetRequiredService<IFlowRuntimeStore>().ListTenantsWithDueAsync(_clock.GetUtcNow(), _options.ExecutorBatchSize, cancellationToken);
        }

        if (tenants.Count == 0)
        {
            return 0;
        }

        var trabalho = new List<(Guid Tenant, Guid Instance)>();
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
                _initializer.Initialize(escopo, new TenantId(tenant), "workflows-executor");
                var reivindicadas = await escopo.ServiceProvider.GetRequiredService<IFlowRuntimeStore>()
                    .ClaimDueAsync(_owner, _options.LeaseDuration, _clock.GetUtcNow(), Math.Min(restante, _options.ExecutorPerTenantBatch), cancellationToken);
                trabalho.AddRange(reivindicadas.Select(i => (tenant, i)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Só o tipo: a mensagem pode carregar dado. O tenant seguinte e o próximo ciclo continuam.
                _logger.LogError("A reivindicação de fluxos falhou para o tenant {TenantId}: {ErrorType}.", tenant, ex.GetType().Name);
            }
        }

        await Parallel.ForEachAsync(
            trabalho,
            new ParallelOptions { MaxDegreeOfParallelism = _options.ExecutorParallelism, CancellationToken = cancellationToken },
            async (item, token) => await RunInstanceAsync(item.Tenant, item.Instance, token));
        return trabalho.Count;
    }

    private async Task RunInstanceAsync(Guid tenant, Guid instanceId, CancellationToken cancellationToken)
    {
        try
        {
            await using var escopo = _scopes.CreateAsyncScope();
            _initializer.Initialize(escopo, new TenantId(tenant), "workflows-executor");
            var resultado = await escopo.ServiceProvider.GetRequiredService<FlowRuntime>().RunAsync(instanceId, cancellationToken);
            _logger.LogInformation("Instância de fluxo {InstanceId} do tenant {TenantId}: {Outcome}.", instanceId, tenant, resultado);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // O lease vence e a instância é reivindicada de novo: a falha de uma nunca derruba as outras (e nunca repete conteúdo no log).
            _logger.LogError("A instância de fluxo {InstanceId} do tenant {TenantId} falhou: {ErrorType}.", instanceId, tenant, ex.GetType().Name);
        }
    }
}

/// <summary>O laço do executor de fluxos (ver <see cref="FlowExecutor"/>).</summary>
internal sealed class FlowExecutorService : BackgroundService
{
    private readonly FlowExecutor _executor;
    private readonly WorkflowsOptions _options;
    private readonly ILogger<FlowExecutorService> _logger;

    public FlowExecutorService(FlowExecutor executor, WorkflowsOptions options, ILogger<FlowExecutorService> logger)
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
                    _logger.LogError("O executor de fluxos falhou neste ciclo: {ErrorType}.", ex.GetType().Name);
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
