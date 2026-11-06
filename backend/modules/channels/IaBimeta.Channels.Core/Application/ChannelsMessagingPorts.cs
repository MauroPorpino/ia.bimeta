using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>O que a URL do webhook resolve: o tenant, a conta e o provedor. Sai de uma consulta SEM contexto de tenant, a única do Receiver, e não autentica nada.</summary>
public sealed record WebhookTarget(TenantId TenantId, Guid AccountId, ChannelProvider Provider);

/// <summary>O ponto de entrada do webhook de cada conta (ver <see cref="ChannelWebhook"/>).</summary>
public interface IChannelWebhookStore
{
    /// <summary>Resolve o identificador opaco da URL, SEM contexto de tenant. Nulo se não existe.</summary>
    Task<WebhookTarget?> ResolveAsync(Guid webhookId, CancellationToken cancellationToken);

    /// <summary>O identificador opaco da conta do tenant do contexto, criado se ainda não existe (idempotente).</summary>
    Task<Guid> EnsureAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>O identificador opaco da conta do tenant do contexto, SEM criar. Nulo: ainda não existe.</summary>
    Task<Guid?> FindAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>Um evento de entrada a gravar no Inbox durável (ver <see cref="IInboundEventWriter"/>).</summary>
/// <param name="EventId">A chave de deduplicação: determinística (conta e identificador do provedor), então o mesmo evento repetido é o mesmo registro.</param>
/// <param name="OrderingKey">A lane: eventos da mesma chave são entregues em ordem de gravação.</param>
public sealed record InboundEventRecord(Guid EventId, string EventType, string OrderingKey, object Payload, DateTimeOffset OccurredAt);

/// <summary>
/// O Inbox durável do Receiver (ADR 0011, seção C; BOUNDARIES.md §12.3): grava os eventos do lote numa transação, com <c>INSERT ... ON CONFLICT DO NOTHING</c> (REC-4), no <c>outbox_message</c> do próprio módulo, que o relay
/// do Messaging entrega, em ordem por lane, com lease, espera crescente e mortos. Só grava eventos do tenant do contexto.
/// </summary>
public interface IInboundEventWriter
{
    /// <summary>Quantos eventos eram novos (os repetidos não contam e não são gravados de novo).</summary>
    Task<int> WriteAsync(IReadOnlyList<InboundEventRecord> events, CancellationToken cancellationToken);
}

/// <summary>O armazenamento do despacho das mensagens de saída (as entidades voltam RASTREADAS; <see cref="SaveAsync"/> grava).</summary>
public interface IOutboundMessageStore
{
    Task<OutboundMessage?> FindByOperationAsync(Guid operationId, CancellationToken cancellationToken);

    Task<OutboundMessage?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken);

    /// <summary>Grava a intenção. Outro processo gravou a mesma operação primeiro: devolve falso, sem gravar nada.</summary>
    Task<bool> TryAddAsync(OutboundMessage message, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// O estado de envio de cada conta (SND-2 e SND-6): o balde de fichas, a espera imposta pelo provedor e a pausa, guardados no banco para valerem entre instâncias do Worker. Toda operação trava a linha da conta na
/// transação e só enxerga o tenant do contexto; conta de outro tenant ou inexistente falha como <see cref="InvalidOperationException"/>.
/// </summary>
public interface IAccountSendStateStore
{
    /// <summary>Pede UMA ficha ao balde da conta (cria a linha cheia se ainda não existe). Pausada: nada é gasto.</summary>
    Task<SendAcquireResult> TryAcquireAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Impõe à conta uma espera a partir de agora (o provedor mandou esperar). Só estende.</summary>
    Task ApplyCooldownAsync(Guid accountId, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Pausa a conta. Falso: já estava pausada (vale o motivo da primeira pausa).</summary>
    Task<bool> PauseAsync(Guid accountId, string reason, CancellationToken cancellationToken);

    /// <summary>Retoma a conta. Falso: não estava pausada.</summary>
    Task<bool> ResumeAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Define (ou, com nulo, limpa) a vazão própria da conta.</summary>
    Task SetThroughputAsync(Guid accountId, int? perSecond, CancellationToken cancellationToken);

    /// <summary>O estado atual, ou nulo se a conta nunca enviou nem foi configurada (vale o padrão).</summary>
    Task<AccountSendState?> FindAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>Um evento de entrada morto no Inbox do Channels (visão administrativa: só chaves e motivo, nunca conteúdo de mensagem).</summary>
public sealed record DeadEventRow(Guid EventId, string EventType, string OrderingKey, DateTimeOffset OccurredAt, int Attempts, string? DeadReason, DateTimeOffset? HandledAt);

/// <summary>As leituras do diagnóstico de envio (só do tenant do contexto; sem rastreamento) e a única escrita dele: reenfileirar um evento de entrada morto.</summary>
public interface IChannelDiagnosticsStore
{
    /// <summary>Quantos despachos do período há em cada estado, só dos números dados.</summary>
    Task<IReadOnlyDictionary<OutboundState, int>> CountOutboundSinceAsync(IReadOnlyCollection<ChannelInstanceId> instances, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Os despachos do período que pedem atenção, os mais recentes primeiro: falha definitiva, resultado incerto, despacho parado (<c>Dispatching</c> ou <c>Retry</c> sem andamento desde <paramref name="idleBefore"/>)
    /// e desfecho gravado mas ainda não informado ao Conversations (parado desde <paramref name="idleBefore"/>).
    /// </summary>
    Task<IReadOnlyList<OutboundMessage>> ListOutboundNeedingAttentionAsync(
        IReadOnlyCollection<ChannelInstanceId> instances, DateTimeOffset since, DateTimeOffset idleBefore, int take, CancellationToken cancellationToken);

    /// <summary>Os despachos das mensagens dadas (as do Conversations), de qualquer estado.</summary>
    Task<IReadOnlyList<OutboundMessage>> FindOutboundByIdsAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    /// <summary>Os eventos de entrada mortos do tenant, os mais recentes primeiro.</summary>
    Task<IReadOnlyList<DeadEventRow>> ListDeadEventsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Reenfileira um evento morto do tenant (volta a Pendente, sem tentativas). Falso: não existe ou não está morto.</summary>
    Task<bool> RequeueDeadEventAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <summary>Os adaptadores de provedor registrados, por provedor.</summary>
public sealed class ChannelProviderAdapters
{
    private readonly Dictionary<ChannelProvider, IChannelProviderAdapter> _porProvedor;

    public ChannelProviderAdapters(IEnumerable<IChannelProviderAdapter> adaptadores)
    {
        _porProvedor = adaptadores.ToDictionary(a => a.Provider);
    }

    public IChannelProviderAdapter? Find(ChannelProvider provider) => _porProvedor.GetValueOrDefault(provider);
}
