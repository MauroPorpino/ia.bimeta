using IaBimeta.ContactCenter.Application;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;

namespace IaBimeta.Worker;

/// <summary>
/// Conclui as operações do ContactCenter que ficaram pendentes (o Conversations estava fora do ar, ou o processo caiu entre a
/// intenção e a conclusão). Abre um escopo de sistema por tenant com operação pendente há tempo e pede a reconciliação do módulo, que
/// é idempotente e só retoma o que passou da idade mínima. Falha de um tenant não impede os outros.
/// </summary>
internal sealed class ContactCenterReconciler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessagingScopeInitializer _initializer;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ContactCenterReconciler> _logger;

    public ContactCenterReconciler(
        IServiceScopeFactory scopes, IMessagingScopeInitializer initializer, WorkerOptions options, TimeProvider clock, ILogger<ContactCenterReconciler> logger)
    {
        _scopes = scopes;
        _initializer = initializer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Um ciclo. Devolve quantas operações concluiu, somando os tenants.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var antes = agora - _options.ReconcileMinimumAge;
        IReadOnlyList<Guid> comOperacao;
        IReadOnlyList<Guid> comAuditoria;
        await using (var escopo = _scopes.CreateAsyncScope())
        {
            var fonte = escopo.ServiceProvider.GetRequiredService<IPendingWorkSource>();
            comOperacao = await fonte.ListTenantsWithPendingAsync(antes, agora, _options.ReconcileTenantBatchSize, cancellationToken);
            comAuditoria = await fonte.ListTenantsWithPendingAuditAsync(_options.ReconcileTenantBatchSize, cancellationToken);
        }

        var total = 0;
        foreach (var tenant in comOperacao.Concat(comAuditoria).Distinct())
        {
            // A conclusão das operações pendentes e a entrega da auditoria guardada são independentes: a falha de uma não pula a outra.
            total += await ReconcileTenantAsync(tenant, "operações", async (servicos, token) =>
                comOperacao.Contains(tenant)
                    ? await servicos.GetRequiredService<QueueItemService>().ReconcilePendingAsync(_options.ReconcileMinimumAge, _options.ReconcileBatchSize, token)
                    : 0, cancellationToken);
            await ReconcileTenantAsync(tenant, "auditoria", async (servicos, token) =>
                await servicos.GetRequiredService<IDeferredAudit>().FlushAsync(_options.ReconcileBatchSize, token), cancellationToken);
        }

        return total;
    }

    private async Task<int> ReconcileTenantAsync(
        Guid tenant, string parte, Func<IServiceProvider, CancellationToken, Task<int>> trabalho, CancellationToken cancellationToken)
    {
        try
        {
            await using var escopo = _scopes.CreateAsyncScope();
            _initializer.Initialize(escopo, new TenantId(tenant), "worker-reconciler");
            return await trabalho(escopo.ServiceProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Só o tipo: a mensagem pode carregar dado. O tenant seguinte e o próximo ciclo continuam.
            _logger.LogError("A reconciliação ({Parte}) do ContactCenter falhou para o tenant {TenantId}: {ErrorType}.", parte, tenant, ex.GetType().Name);
            return 0;
        }
    }
}

/// <summary>O laço do relay do outbox: de tempos em tempos entrega o que está elegível e, uma vez por hora, purga os processados antigos.</summary>
internal sealed class OutboxRelayService : BackgroundService
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly IEnumerable<IOutboxRelay> _relays;
    private readonly MessagingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxRelayService> _logger;

    public OutboxRelayService(IEnumerable<IOutboxRelay> relays, MessagingOptions options, TimeProvider clock, ILogger<OutboxRelayService> logger)
    {
        _relays = relays;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dono = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var ultimaPurga = _clock.GetUtcNow();
        using var relogio = new PeriodicTimer(_options.PollInterval);

        try
        {
            do
            {
                foreach (var relay in _relays)
                {
                    try
                    {
                        var resultado = await relay.RunOnceAsync(dono, stoppingToken);
                        if (resultado.Total > 0)
                        {
                            _logger.LogInformation(
                                "Relay {Outbox}: {Processed} entregues, {Retried} para nova tentativa, {Dead} mortos, {LeaseLost} leases perdidos.",
                                relay.Name, resultado.Processed, resultado.Retried, resultado.Dead, resultado.LeaseLost);
                        }

                        if (_clock.GetUtcNow() - ultimaPurga >= PurgeInterval)
                        {
                            await relay.PurgeProcessedAsync(stoppingToken);
                            ultimaPurga = _clock.GetUtcNow();
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // O laço nunca morre por uma falha do ciclo (banco fora do ar): registra o tipo e tenta no próximo.
                        _logger.LogError("O relay {Outbox} falhou neste ciclo: {ErrorType}.", relay.Name, ex.GetType().Name);
                    }
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

/// <summary>O laço da reconciliação do ContactCenter (ver <see cref="ContactCenterReconciler"/>).</summary>
internal sealed class ContactCenterReconciliationService : BackgroundService
{
    private readonly ContactCenterReconciler _reconciler;
    private readonly WorkerOptions _options;
    private readonly ILogger<ContactCenterReconciliationService> _logger;

    public ContactCenterReconciliationService(ContactCenterReconciler reconciler, WorkerOptions options, ILogger<ContactCenterReconciliationService> logger)
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
                    var concluidas = await _reconciler.RunOnceAsync(stoppingToken);
                    if (concluidas > 0)
                    {
                        _logger.LogInformation("Reconciliação do ContactCenter: {Completed} operações concluídas.", concluidas);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError("A reconciliação do ContactCenter falhou neste ciclo: {ErrorType}.", ex.GetType().Name);
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

/// <summary>
/// O expurgo TÉCNICO agendado (ADR 0026): uma vez por dia, a partir da hora configurada em <c>Retention:Schedule</c>, roda as mesmas tarefas do comando <c>retention purge</c> (sessões vencidas, tokens de redefinição
/// encerrados, contadores de login parados, eventos processados do outbox e marcas do Inbox), com os prazos por empresa e os bloqueios de segurança (marcas do Inbox bloqueadas por evento morto antigo). Nunca toca mensagens,
/// contatos, atendimentos, usuários, auditoria nem ledger de IA: essas tabelas não têm tarefa de expurgo. Desligado por padrão. O log traz só contagens, nunca dado de empresa ou de cliente.
/// </summary>
internal sealed class RetentionSchedulerService : BackgroundService
{
    private readonly IServiceProvider _root;
    private readonly RetentionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionSchedulerService> _logger;

    public RetentionSchedulerService(IServiceProvider root, RetentionOptions options, TimeProvider clock, ILogger<RetentionSchedulerService> logger)
    {
        _root = root;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agenda = _options.Schedule;
        if (!agenda.Enabled)
        {
            _logger.LogInformation("O expurgo técnico agendado está desligado (Retention:Schedule:Enabled).");
            return;
        }

        _logger.LogInformation("Expurgo técnico agendado: diário, a partir das {Hora} UTC.", agenda.TimeOfDayUtc);
        DateTimeOffset? ultimoSucesso = null;
        DateTimeOffset? tentarDepoisDe = null;
        using var relogio = new PeriodicTimer(agenda.CheckInterval);

        try
        {
            do
            {
                var agora = _clock.GetUtcNow();
                if (tentarDepoisDe is { } depois && agora < depois)
                {
                    continue;
                }

                if (!agenda.IsDue(agora, ultimoSucesso))
                {
                    continue;
                }

                try
                {
                    var relatorio = await RetentionRunner.RunAsync(_root, dryRun: false, stoppingToken);
                    var removidos = relatorio.Results.Sum(r => r.Deleted);
                    var bloqueadas = relatorio.Results.Count(r => r.Skipped is not null);
                    _logger.LogInformation("Expurgo técnico concluído: {Removidos} linha(s) removida(s) por {Tarefas} tarefa(s) em {Passadas} passada(s); {Bloqueadas} bloqueada(s).",
                        removidos, relatorio.Results.Select(r => r.Task).Distinct().Count(), relatorio.Passes.Count, bloqueadas);
                    foreach (var bloqueada in relatorio.Results.Where(r => r.Skipped is not null))
                    {
                        _logger.LogWarning("Expurgo técnico bloqueado em {Tarefa}: {Motivo}", bloqueada.Task, bloqueada.Skipped);
                    }

                    ultimoSucesso = agora;
                    tentarDepoisDe = null;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Só o tipo: a mensagem de uma falha de banco ou de configuração pode trazer detalhe. Tenta de novo depois da espera configurada.
                    tentarDepoisDe = agora + agenda.RetryAfter;
                    _logger.LogError("O expurgo técnico agendado falhou ({ErrorType}); nova tentativa depois de {Espera}.", ex.GetType().Name, agenda.RetryAfter);
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
