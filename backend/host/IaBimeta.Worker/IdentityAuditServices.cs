using IaBimeta.Identity.Application;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;

namespace IaBimeta.Worker;

/// <summary>
/// Entrega à trilha a auditoria do Embedded guardada na transação do consumo do ticket (<c>identity.audit_outbox</c>, E1-R quarta rodada), no mesmo padrão da entrega do ContactCenter. Abre um escopo de sistema por tenant com
/// linhas pendentes e pede a entrega ao módulo, que é pelo menos uma vez, idempotente na trilha e só apaga a linha depois de a trilha aceitar. Falha de um tenant não impede os outros; dois Workers não pegam a mesma linha
/// (<c>FOR UPDATE SKIP LOCKED</c>); se o processo cai no meio, a linha volta no próximo ciclo.
/// </summary>
internal sealed class IdentityAuditReconciler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessagingScopeInitializer _initializer;
    private readonly WorkerOptions _options;
    private readonly ILogger<IdentityAuditReconciler> _logger;

    public IdentityAuditReconciler(IServiceScopeFactory scopes, IMessagingScopeInitializer initializer, WorkerOptions options, ILogger<IdentityAuditReconciler> logger)
    {
        _scopes = scopes;
        _initializer = initializer;
        _options = options;
        _logger = logger;
    }

    /// <summary>Um ciclo. Devolve quantas entradas entregou, somando os tenants.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var escopo = _scopes.CreateAsyncScope())
        {
            tenants = await escopo.ServiceProvider.GetRequiredService<IIdentityAuditOutbox>().ListTenantsWithPendingAsync(_options.ReconcileTenantBatchSize, cancellationToken);
        }

        var total = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var escopo = _scopes.CreateAsyncScope();
                _initializer.Initialize(escopo, new TenantId(tenant), "worker-reconciler");
                total += await escopo.ServiceProvider.GetRequiredService<IIdentityAuditOutbox>().FlushAsync(_options.ReconcileBatchSize, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Só o tipo: a mensagem pode carregar dado. O tenant seguinte e o próximo ciclo continuam.
                _logger.LogError("A entrega da auditoria do Identity falhou para o tenant {TenantId}: {ErrorType}.", tenant, ex.GetType().Name);
            }
        }

        return total;
    }
}

/// <summary>O laço da entrega da auditoria do Identity (ver <see cref="IdentityAuditReconciler"/>), no mesmo intervalo da reconciliação do ContactCenter.</summary>
internal sealed class IdentityAuditDeliveryService : BackgroundService
{
    private readonly IdentityAuditReconciler _reconciler;
    private readonly WorkerOptions _options;
    private readonly ILogger<IdentityAuditDeliveryService> _logger;

    public IdentityAuditDeliveryService(IdentityAuditReconciler reconciler, WorkerOptions options, ILogger<IdentityAuditDeliveryService> logger)
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
                    var entregues = await _reconciler.RunOnceAsync(stoppingToken);
                    if (entregues > 0)
                    {
                        _logger.LogInformation("Auditoria do Identity: {Delivered} entradas entregues à trilha.", entregues);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError("A entrega da auditoria do Identity falhou neste ciclo: {ErrorType}.", ex.GetType().Name);
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
