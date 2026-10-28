using System.Collections.Concurrent;

namespace IaBimeta.SharedKernel;

/// <summary>
/// Publicador de eventos entre módulos. O módulo emissor entrega os eventos depois de gravar o seu estado. A
/// implementação durável (outbox transacional, relay e inbox do consumidor, no bloco <c>Messaging</c>) nasce na etapa 4
/// (BOUNDARIES.md §12.12); até lá, vale o dublê em memória <see cref="InMemoryEventPublisher"/>.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dublê em memória da etapa 3. NÃO é durável: os eventos vivem só no processo e se perdem na reinicialização. Serve aos
/// testes e ao desenvolvimento, não pode ser apresentado como integração real. Guarda só os mais recentes, para não
/// crescer sem limite.
/// </summary>
public sealed class InMemoryEventPublisher : IEventPublisher
{
    public const int MaxRetained = 10_000;

    private readonly ConcurrentQueue<EventEnvelope> _eventos = new();

    public IReadOnlyList<EventEnvelope> Published => _eventos.ToArray();

    public Task PublishAsync(IReadOnlyCollection<EventEnvelope> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var evento in events)
        {
            _eventos.Enqueue(evento);
        }

        while (_eventos.Count > MaxRetained && _eventos.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }
}
