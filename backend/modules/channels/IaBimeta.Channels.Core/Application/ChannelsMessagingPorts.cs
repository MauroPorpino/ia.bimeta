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
