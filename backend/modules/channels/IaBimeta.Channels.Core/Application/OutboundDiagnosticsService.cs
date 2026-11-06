using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>Quantos despachos do período há em cada estado (só dos números que o usuário gere).</summary>
public sealed record OutboundCounts(int Sent, int Failed, int SendUnknown, int Dispatching, int Retry);

/// <summary>Uma conta com envio PAUSADO ou esperando o provedor (limite de taxa). Nunca traz credencial.</summary>
public sealed record DiagnosticAccount(
    Guid AccountId, string Name, bool Paused, string? PauseReason, DateTimeOffset? PausedAt, DateTimeOffset? CooldownUntil, int ThroughputPerSecond);

/// <summary>
/// Uma mensagem de saída que pede atenção. <c>Problem</c>: <c>stale-before-sender</c> (o Conversations a liberou e ela não chegou ao Sender: evento pendente ou morto no relay), <c>stale-at-sender</c> (chegou ao
/// Sender e não teve desfecho), <c>stuck-dispatching</c> (despacho parado), <c>unnotified</c> (desfecho gravado, ainda não informado ao Conversations), <c>send-unknown</c> (a Meta PODE ter recebido: confirme no
/// aparelho do cliente, nunca reenvie às cegas) e <c>failed</c> (falha definitiva, com o código). Sem texto de mensagem nem dado de cliente.
/// </summary>
public sealed record DiagnosticMessage(
    Guid MessageId, Guid? SessionId, string Problem, string? SenderState, string? ErrorCode, DateTimeOffset Since, bool? Notified, int? Attempts, Guid? ChannelInstanceId);

/// <summary>Um evento de ENTRADA morto no Inbox (a mensagem do cliente que não chegou ao Conversations): só chaves e motivo.</summary>
public sealed record DiagnosticDeadEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt, int Attempts, string? DeadReason);

/// <summary>
/// O diagnóstico operacional do envio. <see cref="Healthy"/> é falso quando há problema ESTRUTURAL (conta pausada, mensagem parada ou sem desfecho informado, evento de entrada morto); falhas definitivas e envios de
/// resultado incerto aparecem na lista e nas contagens, mas não tornam o sistema "doente": são desfechos que alguém precisa ver.
/// </summary>
public sealed record OutboundDiagnosticsReport(
    DateTimeOffset GeneratedAt,
    int WindowHours,
    int StaleAfterSeconds,
    bool Healthy,
    OutboundCounts Counts,
    IReadOnlyList<DiagnosticAccount> Accounts,
    IReadOnlyList<DiagnosticMessage> Messages,
    IReadOnlyList<DiagnosticDeadEvent> DeadInboundEvents);

/// <summary>
/// A rotina de DIAGNÓSTICO do envio (só leitura): contas pausadas ou esperando o provedor, mensagens paradas, falhas definitivas, envios de resultado incerto e eventos de entrada mortos, só dos números das
/// organizações em que o usuário gere canais (TEN-2). Cruza o que o Conversations diz (mensagem pedida sem desfecho) com o que o Sender gravou, para apontar ONDE a mensagem parou. A única escrita é reenfileirar um
/// evento de entrada morto (auditada).
/// </summary>
public sealed class OutboundDiagnosticsService
{
    private const string Resource = "channel-diagnostics";
    private const int MaxTake = 200;

    private readonly IChannelRepository _repository;
    private readonly IChannelDiagnosticsStore _store;
    private readonly IAccountSendStateStore _state;
    private readonly IOutboundDeliveryQueries _conversations;
    private readonly ChannelSendingOptions _sending;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public OutboundDiagnosticsService(
        IChannelRepository repository,
        IChannelDiagnosticsStore store,
        IAccountSendStateStore state,
        IOutboundDeliveryQueries conversations,
        ChannelSendingOptions sending,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _repository = repository;
        _store = store;
        _state = state;
        _conversations = conversations;
        _sending = sending;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <param name="windowHours">O período de falhas e de contagens (1 a 168 horas).</param>
    /// <param name="staleAfter">Depois de quanto tempo sem desfecho uma mensagem é considerada parada (30 s a 24 h).</param>
    /// <param name="take">O máximo de itens por lista (1 a 200).</param>
    /// <exception cref="AccessDeniedException">O usuário não gere canais em organização alguma.</exception>
    public async Task<OutboundDiagnosticsReport> GetAsync(int windowHours, TimeSpan staleAfter, int take, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var organizacoes = contexto.OrganizationsWithPermission(Permission.ChannelManage);
        if (organizacoes is { Count: 0 })
        {
            await _audit.WriteAsync(new AuditEntry("channels.diagnostics.read", Resource, null, AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(Permission.ChannelManage);
        }

        windowHours = Math.Clamp(windowHours, 1, 168);
        staleAfter = staleAfter < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : staleAfter > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : staleAfter;
        take = Math.Clamp(take, 1, MaxTake);

        var agora = _clock.GetUtcNow();
        var desde = agora.AddHours(-windowHours);
        var parado = agora - staleAfter;

        var instancias = await _repository.ListInstancesAsync(organizacoes, cancellationToken);
        var contas = await _repository.ListAccountsAsync(organizacoes, cancellationToken);
        var idsDasInstancias = instancias.Select(i => i.Id).ToList();
        var conjunto = idsDasInstancias.Select(i => i.Value).ToHashSet();

        // As contas com envio pausado ou esperando o provedor.
        var visaoDasContas = new List<DiagnosticAccount>();
        foreach (var conta in contas)
        {
            var estado = await _state.FindAsync(conta.Id, cancellationToken);
            var esperando = estado?.CooldownUntil is { } fim && fim > agora;
            if (estado is { IsPaused: true } || esperando)
            {
                visaoDasContas.Add(new DiagnosticAccount(
                    conta.Id, conta.Name, estado!.IsPaused, estado.PauseReason, estado.PausedAt, esperando ? estado.CooldownUntil : null, estado.EffectiveThroughput(_sending.DefaultThroughputPerSecond)));
            }
        }

        // As contagens e os despachos que pedem atenção (só dos números do usuário).
        var porEstado = await _store.CountOutboundSinceAsync(idsDasInstancias, desde, cancellationToken);
        var contagens = new OutboundCounts(
            porEstado.GetValueOrDefault(OutboundState.Sent),
            porEstado.GetValueOrDefault(OutboundState.Failed),
            porEstado.GetValueOrDefault(OutboundState.SendUnknown),
            porEstado.GetValueOrDefault(OutboundState.Dispatching),
            porEstado.GetValueOrDefault(OutboundState.Retry));

        var mensagens = new List<DiagnosticMessage>();
        var atencao = await _store.ListOutboundNeedingAttentionAsync(idsDasInstancias, desde, parado, take, cancellationToken);
        foreach (var linha in atencao)
        {
            mensagens.Add(new DiagnosticMessage(
                linha.Id, null, ClassifyDispatch(linha, parado), linha.State.ToString(), linha.ErrorCode, linha.UpdatedAt, linha.Notified, linha.Attempts, linha.ChannelInstanceId.Value));
        }

        // O que o Conversations liberou para envio e não teve desfecho: o sinal independente do mecanismo (relay, Sender, conta limitada).
        var pendentes = (await _conversations.ListUndeliveredAsync(parado, take, cancellationToken)).Where(p => conjunto.Contains(p.ChannelInstanceId.Value)).ToList();
        var jaListadas = mensagens.Select(m => m.MessageId).ToHashSet();
        var doSender = (await _store.FindOutboundByIdsAsync(pendentes.Select(p => p.MessageId).ToList(), cancellationToken)).ToDictionary(o => o.Id);
        foreach (var pendente in pendentes.Where(p => !jaListadas.Contains(p.MessageId)))
        {
            doSender.TryGetValue(pendente.MessageId, out var despacho);
            mensagens.Add(new DiagnosticMessage(
                pendente.MessageId,
                pendente.SessionId,
                despacho is null ? "stale-before-sender" : "stale-at-sender",
                despacho?.State.ToString(),
                despacho?.ErrorCode,
                pendente.CreatedAt,
                despacho?.Notified,
                despacho?.Attempts,
                pendente.ChannelInstanceId.Value));
        }

        // Os eventos de ENTRADA mortos: a mensagem do cliente que não chegou ao Conversations.
        var prefixos = idsDasInstancias.Select(i => i.Value.ToString("N") + ":").ToList();
        var mortos = (await _store.ListDeadEventsAsync(take, cancellationToken))
            .Where(m => prefixos.Any(p => m.OrderingKey.StartsWith(p, StringComparison.Ordinal)))
            .Select(m => new DiagnosticDeadEvent(m.EventId, m.EventType, m.OccurredAt, m.Attempts, m.DeadReason))
            .ToList();

        var estrutural = visaoDasContas.Any(c => c.Paused)
            || mensagens.Any(m => m.Problem is "stale-before-sender" or "stale-at-sender" or "stuck-dispatching" or "unnotified")
            || mortos.Count > 0;

        await _audit.WriteAsync(new AuditEntry("channels.diagnostics.read", Resource, null, AuditOutcome.Success), cancellationToken);
        return new OutboundDiagnosticsReport(
            agora, windowHours, (int)staleAfter.TotalSeconds, !estrutural, contagens, visaoDasContas, mensagens.OrderByDescending(m => m.Since).ToList(), mortos);
    }

    /// <summary>
    /// Reenfileira um evento de ENTRADA morto (volta a Pendente, sem tentativas), para o relay tentar de novo (a deduplicação por identificador do provedor impede mensagem duplicada). Só de número que o usuário gere;
    /// o resto responde como inexistente (TEN-2). Auditado.
    /// </summary>
    /// <exception cref="AccessDeniedException">O usuário não gere canais em organização alguma.</exception>
    /// <exception cref="ChannelsResourceNotFoundException">O evento não existe, não está morto ou não é de um número que o usuário gere.</exception>
    public async Task RequeueDeadEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var organizacoes = contexto.OrganizationsWithPermission(Permission.ChannelManage);
        if (organizacoes is { Count: 0 })
        {
            await _audit.WriteAsync(new AuditEntry("channels.diagnostics.requeue", Resource, eventId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new AccessDeniedException(Permission.ChannelManage);
        }

        var instancias = await _repository.ListInstancesAsync(organizacoes, cancellationToken);
        var prefixos = instancias.Select(i => i.Id.Value.ToString("N") + ":").ToList();
        var mortos = await _store.ListDeadEventsAsync(MaxTake, cancellationToken);
        var alvo = mortos.FirstOrDefault(m => m.EventId == eventId && prefixos.Any(p => m.OrderingKey.StartsWith(p, StringComparison.Ordinal)));
        if (alvo is null || !await _store.RequeueDeadEventAsync(eventId, cancellationToken))
        {
            throw new ChannelsResourceNotFoundException("Evento");
        }

        await _audit.WriteAsync(new AuditEntry("channels.diagnostics.requeue", Resource, eventId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    private static string ClassifyDispatch(OutboundMessage despacho, DateTimeOffset parado) => despacho.State switch
    {
        OutboundState.Failed when despacho.Notified || despacho.UpdatedAt >= parado => "failed",
        OutboundState.Failed => "unnotified",
        OutboundState.SendUnknown when despacho.Notified || despacho.UpdatedAt >= parado => "send-unknown",
        OutboundState.SendUnknown => "unnotified",
        OutboundState.Sent => "unnotified",
        _ => "stuck-dispatching",
    };
}
