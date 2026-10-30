using System.Diagnostics;
using System.Text.Json;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IaBimeta.Messaging;

/// <summary>
/// Quem estabelece o contexto confiável do tenant no escopo de um handler. Só o host o implementa (TEN-1): o <see cref="RequestContext"/>
/// só é criado nos hosts, a partir do tenant do envelope gravado pelo módulo emissor, e nunca de um dado do payload.
/// </summary>
public interface IMessagingScopeInitializer
{
    void Initialize(IServiceScope scope, TenantId tenant, string origin);
}

/// <summary>O que um ciclo do relay fez. Sem identificação de tenant nem de conteúdo.</summary>
public readonly record struct RelayRunResult(int Processed, int Retried, int Dead, int LeaseLost)
{
    public static RelayRunResult Empty => default;

    public int Total => Processed + Retried + Dead + LeaseLost;

    public static RelayRunResult operator +(RelayRunResult a, RelayRunResult b) =>
        new(a.Processed + b.Processed, a.Retried + b.Retried, a.Dead + b.Dead, a.LeaseLost + b.LeaseLost);
}

/// <summary>O relay de um outbox, sem o tipo do DbContext (o que o laço do Worker precisa).</summary>
public interface IOutboxRelay
{
    /// <summary>O nome do outbox (o esquema do módulo emissor), para logs e métricas.</summary>
    string Name { get; }

    Task<RelayRunResult> RunOnceAsync(string owner, CancellationToken cancellationToken = default);

    /// <summary>Apaga os processados mais antigos que a retenção. Devolve quantos apagou.</summary>
    Task<int> PurgeProcessedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// O relay do outbox de UM módulo emissor (BOUNDARIES.md §12.5 e §12.12, v0): pega os eventos elegíveis com lease, entrega aos
/// handlers pelo tipo e pela versão, e conclui com Processado, nova tentativa (espera exponencial com jitter) ou Morto.
/// <list type="bullet">
/// <item><b>Pelo menos uma vez:</b> um evento cujo lease venceu (o processo caiu) volta a ser elegível. Os handlers são idempotentes
/// por <c>EventId</c>.</item>
/// <item><b>Ordem por chave:</b> um evento só é elegível quando não há outro MAIS ANTIGO da mesma chave (<c>OrderingKey</c>) ainda
/// pendente ou em andamento. A escolha dos elegíveis é serializada por um lock consultivo do banco, então a regra vale mesmo com
/// vários relays. Um evento em nova tentativa segura os seguintes da mesma chave; um morto não segura (a fila segue).</item>
/// <item><b>Fencing:</b> concluir exige o token do lease vigente. Um relay que perdeu o lease não sobrescreve o novo.</item>
/// <item><b>Sem lanes avançadas, sem escalonador justo, sem broker:</b> só o que foi pedido para a etapa 4.</item>
/// </list>
/// O tenant do handler vem do envelope gravado pelo módulo emissor, pelo <see cref="IMessagingScopeInitializer"/> do host.
/// </summary>
public sealed class OutboxRelay<TContext> : IOutboxRelay
    where TContext : DbContext
{
    // Teto de ciclos por execução: um módulo com muito acúmulo não monopoliza o worker (ORD-3); o laço do host volta logo depois.
    private const int MaxCyclesPerRun = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly IMessagingScopeInitializer _initializer;
    private readonly MessagingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxRelay<TContext>> _logger;

    public OutboxRelay(
        IServiceScopeFactory scopes,
        IMessagingScopeInitializer initializer,
        MessagingOptions options,
        TimeProvider clock,
        ILogger<OutboxRelay<TContext>> logger)
    {
        _scopes = scopes;
        _initializer = initializer;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public string Name => typeof(TContext).Name;

    public async Task<RelayRunResult> RunOnceAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var total = RelayRunResult.Empty;
        for (var ciclo = 0; ciclo < MaxCyclesPerRun; ciclo++)
        {
            var (lote, considerados, mortos) = await LeaseAsync(owner, cancellationToken);
            if (considerados == 0)
            {
                break;
            }

            // Os que esgotaram as tentativas ao pegar o lease contam como mortos neste ciclo (já confirmados pelo commit do lease).
            total += new RelayRunResult(0, 0, mortos, 0);
            foreach (var mensagem in lote)
            {
                total += await DeliverAsync(mensagem, cancellationToken);
            }
        }

        return total;
    }

    /// <summary>Reenfileira um evento morto (ação de administrador): volta a Pendente, sem tentativas. A auditoria é de quem chama.</summary>
    /// <returns><c>false</c> se o evento não existe ou não está morto.</returns>
    public async Task<bool> RequeueDeadAsync(Guid tenantId, Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var agora = _clock.GetUtcNow();
        var afetadas = await db.Set<OutboxMessage>()
            .Where(o => o.TenantId == tenantId && o.Id == eventId && o.State == OutboxState.Dead)
            .ExecuteUpdateAsync(
                s => s.SetProperty(o => o.State, OutboxState.Pending)
                    .SetProperty(o => o.Attempts, 0)
                    .SetProperty(o => o.NextAttemptAt, agora)
                    .SetProperty(o => o.DeadReason, (string?)null),
                cancellationToken);
        return afetadas == 1;
    }

    /// <summary>A profundidade do outbox por estado (observabilidade e testes).</summary>
    public async Task<IReadOnlyDictionary<OutboxState, int>> DepthAsync(CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var linhas = await db.Set<OutboxMessage>().AsNoTracking().GroupBy(o => o.State).Select(g => new { Estado = g.Key, Total = g.Count() }).ToListAsync(cancellationToken);
        return linhas.ToDictionary(l => l.Estado, l => l.Total);
    }

    public async Task<int> PurgeProcessedAsync(CancellationToken cancellationToken = default)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var limite = _clock.GetUtcNow() - _options.ProcessedRetention;
        return await db.Set<OutboxMessage>().Where(o => o.State == OutboxState.Processed && o.ProcessedAt < limite).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<(IReadOnlyList<OutboxMessage> Entregar, int Considerados, int Mortos)> LeaseAsync(string owner, CancellationToken cancellationToken)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        // Só um relay escolhe por vez: a regra de ordem por chave depende de a escolha ser serializada. O lock é da transação e
        // cai sozinho no commit; o lease (não este lock) é o que protege durante a entrega.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { AdvisoryLockKey() }, cancellationToken);

        var agora = _clock.GetUtcNow();
        var fila = db.Set<OutboxMessage>();
        var candidatos = await fila
            .Where(o => ((o.State == OutboxState.Pending && o.NextAttemptAt <= agora) || (o.State == OutboxState.Processing && o.LeaseExpiresAt < agora))
                && !fila.Any(anterior => anterior.TenantId == o.TenantId && anterior.OrderingKey == o.OrderingKey && anterior.IngestSeq < o.IngestSeq
                    && (anterior.State == OutboxState.Pending || anterior.State == OutboxState.Processing)))
            .OrderBy(o => o.IngestSeq)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var entregar = new List<OutboxMessage>(candidatos.Count);
        var mortos = new List<(Guid Id, Guid TenantId, string EventType)>();
        foreach (var mensagem in candidatos)
        {
            // A tentativa conta no lease (e não só na falha): um evento que derruba o processo a cada vez chega aos mortos. E o
            // máximo vale também para quem nunca devolveu um resultado (o processo caiu, o lease venceu): quem já gastou todas as
            // tentativas não é entregue mais uma vez, vai direto para os mortos.
            if (mensagem.Attempts >= _options.MaxAttempts)
            {
                mensagem.State = OutboxState.Dead;
                mensagem.DeadReason = "max-attempts:lease-expired";
                mensagem.LastError ??= "lease-expired";
                mensagem.LeaseOwner = null;
                mensagem.LeaseToken = null;
                mensagem.LeaseExpiresAt = null;
                mortos.Add((mensagem.Id, mensagem.TenantId, mensagem.EventType));
                continue;
            }

            mensagem.State = OutboxState.Processing;
            mensagem.Attempts++;
            mensagem.LeaseOwner = owner;
            mensagem.LeaseToken = Guid.NewGuid();
            mensagem.LeaseExpiresAt = agora + _options.LeaseDuration;
            entregar.Add(mensagem);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        // A métrica e o registro dos mortos só existem depois do commit: se a transação falhar, nenhum evento foi para os mortos
        // (todos continuam como estavam) e nada disso é contado.
        foreach (var (id, tenant, tipo) in mortos)
        {
            MessagingMetrics.Dead.Add(1);
            _logger.LogError("O evento {EventType} ({EventId}) do tenant {TenantId} esgotou as tentativas sem concluir e foi para os mortos.", tipo, id, tenant);
        }

        // As mortas pelo máximo contam como trabalho feito (o laço continua), mas não são entregues.
        return (entregar, candidatos.Count, mortos.Count);
    }

    private async Task<RelayRunResult> DeliverAsync(OutboxMessage mensagem, CancellationToken cancellationToken)
    {
        var relogio = Stopwatch.StartNew();
        HandlerResult resultado;
        try
        {
            resultado = await DispatchAsync(mensagem, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // O processo está parando: o lease vence sozinho e outro relay retoma.
            throw;
        }
        catch (Exception ex)
        {
            // Exceção não tratada é falha transitória; só o tipo vai para o registro (nunca a mensagem, que pode ter conteúdo).
            resultado = HandlerResult.Retry(null, ex.GetType().Name);
            _logger.LogWarning("Falha ao entregar o evento {EventType} ({EventId}) do tenant {TenantId}: {ErrorType}.", mensagem.EventType, mensagem.Id, mensagem.TenantId, ex.GetType().Name);
        }

        var concluido = await CompleteAsync(mensagem, resultado, cancellationToken);
        MessagingMetrics.ProcessingSeconds.Record(relogio.Elapsed.TotalSeconds);
        return concluido;
    }

    private async Task<HandlerResult> DispatchAsync(OutboxMessage mensagem, CancellationToken cancellationToken)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        _initializer.Initialize(escopo, new TenantId(mensagem.TenantId), "messaging-relay");

        var handlers = escopo.ServiceProvider.GetServices<IEventHandler>()
            .Where(h => h.EventType == mensagem.EventType && h.EventVersion == mensagem.EventVersion)
            .ToList();

        // Ninguém consome este tipo (ainda): o evento fica como processado, sem acúmulo. Quando um consumidor nascer, vale daí em diante.
        if (handlers.Count == 0)
        {
            return HandlerResult.Success;
        }

        HandlerResult? morto = null;
        HandlerResult? repetir = null;
        foreach (var handler in handlers)
        {
            object payload;
            try
            {
                payload = MessagingJson.Deserialize(mensagem.Payload, handler.PayloadType);
            }
            catch (JsonException)
            {
                morto ??= HandlerResult.Dead("payload-invalid");
                continue;
            }

            var envelope = new EventEnvelope(
                mensagem.Id, mensagem.EventType, mensagem.EventVersion, new TenantId(mensagem.TenantId),
                mensagem.CorrelationId, mensagem.CausationId, mensagem.OrderingKey, mensagem.OccurredAt, payload);

            var resultado = await handler.HandleAsync(envelope, cancellationToken);
            switch (resultado.Outcome)
            {
                case HandlerOutcome.Dead:
                    morto ??= resultado;
                    break;
                case HandlerOutcome.Retry:
                    repetir = repetir is null || (resultado.RetryAfter ?? TimeSpan.Zero) > (repetir.Value.RetryAfter ?? TimeSpan.Zero) ? resultado : repetir;
                    break;
            }
        }

        // A falha permanente vale mais que a transitória, e esta mais que o sucesso: os handlers que já concluíram são idempotentes.
        return morto ?? repetir ?? HandlerResult.Success;
    }

    private async Task<RelayRunResult> CompleteAsync(OutboxMessage mensagem, HandlerResult resultado, CancellationToken cancellationToken)
    {
        await using var escopo = _scopes.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<TContext>();
        var agora = _clock.GetUtcNow();
        var alvo = db.Set<OutboxMessage>().Where(o => o.TenantId == mensagem.TenantId && o.Id == mensagem.Id && o.LeaseToken == mensagem.LeaseToken);

        int afetadas;
        RelayRunResult contagem;
        string? motivoDaMorte = null;
        var motivo = Truncate(resultado.Reason, 300);
        switch (resultado.Outcome)
        {
            case HandlerOutcome.Success:
                afetadas = await alvo.ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.State, OutboxState.Processed)
                        .SetProperty(o => o.ProcessedAt, agora)
                        .SetProperty(o => o.LeaseToken, (Guid?)null)
                        .SetProperty(o => o.LeaseOwner, (string?)null)
                        .SetProperty(o => o.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(o => o.LastError, (string?)null),
                    cancellationToken);
                contagem = new RelayRunResult(1, 0, 0, 0);
                break;

            case HandlerOutcome.Retry when mensagem.Attempts < _options.MaxAttempts:
                var espera = Backoff.For(mensagem.Attempts, _options, Random.Shared.NextDouble());
                if (resultado.RetryAfter is { } minima && minima > espera)
                {
                    espera = minima;
                }

                var proxima = agora + espera;
                afetadas = await alvo.ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.State, OutboxState.Pending)
                        .SetProperty(o => o.NextAttemptAt, proxima)
                        .SetProperty(o => o.LeaseToken, (Guid?)null)
                        .SetProperty(o => o.LeaseOwner, (string?)null)
                        .SetProperty(o => o.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(o => o.LastError, motivo ?? "retry"),
                    cancellationToken);
                contagem = new RelayRunResult(0, 1, 0, 0);
                break;

            default:
                // Falha permanente, ou transitória que esgotou as tentativas (veneno).
                var morte = resultado.Outcome == HandlerOutcome.Dead ? motivo ?? "dead" : $"max-attempts:{motivo ?? "retry"}";
                afetadas = await alvo.ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.State, OutboxState.Dead)
                        .SetProperty(o => o.DeadReason, Truncate(morte, 300))
                        .SetProperty(o => o.LeaseToken, (Guid?)null)
                        .SetProperty(o => o.LeaseOwner, (string?)null)
                        .SetProperty(o => o.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(o => o.LastError, motivo),
                    cancellationToken);
                contagem = new RelayRunResult(0, 0, 1, 0);
                motivoDaMorte = morte;
                break;
        }

        if (afetadas == 0)
        {
            // O lease foi perdido (venceu e outro relay pegou o evento): o resultado desta entrega não vale, e nada dela é contado
            // como entregue, repetido ou morto.
            MessagingMetrics.LeaseLost.Add(1);
            return new RelayRunResult(0, 0, 0, 1);
        }

        // As métricas e o registro só contam o que o fencing confirmou (a atualização do estado com o token do lease vigente).
        if (contagem.Processed > 0)
        {
            MessagingMetrics.Processed.Add(1);
        }
        else if (contagem.Retried > 0)
        {
            MessagingMetrics.Retried.Add(1);
        }
        else
        {
            MessagingMetrics.Dead.Add(1);
            _logger.LogError("O evento {EventType} ({EventId}) do tenant {TenantId} foi para os mortos: {Reason}.", mensagem.EventType, mensagem.Id, mensagem.TenantId, motivoDaMorte);
        }

        return contagem;
    }

    private static string? Truncate(string? texto, int max) => texto is null || texto.Length <= max ? texto : texto[..max];

    /// <summary>Chave estável (FNV-1a) do lock consultivo deste outbox: não pode variar entre processos nem execuções.</summary>
    private static long AdvisoryLockKey()
    {
        var hash = 14695981039346656037UL;
        foreach (var caractere in $"outbox-relay:{typeof(TContext).FullName}")
        {
            hash = (hash ^ caractere) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }
}
