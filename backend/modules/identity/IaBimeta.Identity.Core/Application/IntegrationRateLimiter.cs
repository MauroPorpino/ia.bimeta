using System.Collections.Concurrent;

namespace IaBimeta.Identity.Application;

/// <summary>A cota estourou. O host responde 429 com <c>Retry-After</c>.</summary>
public sealed class EmbeddedRateLimitedException : Exception
{
    public EmbeddedRateLimitedException(TimeSpan retryAfter)
        : base("Limite de requisições excedido.")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

/// <summary>
/// Limite de taxa das chamadas assinadas por IDENTIDADE AUTENTICADA OU CONHECIDA (E1-R). O limite anterior à autenticação (por origem de rede) é do host e não depende de nada que o chamador escolha; este vem depois e tem
/// a cardinalidade limitada pelo que existe no banco: a partição é o identificador de uma chave que o banco CONHECE (antes da assinatura) ou o da conexão (depois dela), nunca um valor arbitrário de cabeçalho. Quem
/// inventa identificadores de chave não ganha cota nova: a chave inexistente é recusada antes de qualquer cota e só gasta o limite da origem de rede.
/// Janela fixa em memória (por instância): é um freio contra inundação, não a contabilidade de uso.
/// </summary>
public sealed class IntegrationRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _window;
    private readonly int _permitsPerKey;
    private readonly int _permitsPerConnection;
    private long _lastSweepTicks;

    public IntegrationRateLimiter(TimeProvider clock, IdentityOptions options)
    {
        _clock = clock;
        _window = options.IntegrationRateWindow;
        _permitsPerKey = options.IntegrationPermitsPerKey;
        _permitsPerConnection = options.IntegrationPermitsPerConnection;
    }

    /// <summary>Recusa se a chave CONHECIDA já esgotou o orçamento de FALHAS da janela (só as falhas contam aqui: quem acerta a assinatura não gasta este orçamento, e as chamadas boas são limitadas por conexão).</summary>
    public void ThrowIfKeyThrottled(Guid keyId) => Check("k:" + keyId.ToString("N"), _permitsPerKey, consume: false);

    /// <summary>Registra uma falha de autenticação contra a chave conhecida.</summary>
    public void RecordKeyFailure(Guid keyId) => Check("k:" + keyId.ToString("N"), int.MaxValue, consume: true);

    /// <summary>Conta uma chamada autenticada da conexão (todas as chaves dela somam).</summary>
    public void CheckConnection(Guid connectionId) => Check("c:" + connectionId.ToString("N"), _permitsPerConnection, consume: true);

    private void Check(string partition, int permits, bool consume)
    {
        var agora = _clock.GetUtcNow().UtcTicks;
        Sweep(agora);

        var janela = _windows.GetOrAdd(partition, _ => new Window(agora));
        lock (janela)
        {
            if (agora - janela.StartedTicks >= _window.Ticks)
            {
                janela.StartedTicks = agora;
                janela.Count = 0;
            }

            if (janela.Count >= permits)
            {
                throw new EmbeddedRateLimitedException(TimeSpan.FromTicks(Math.Max(0, janela.StartedTicks + _window.Ticks - agora)));
            }

            if (consume)
            {
                janela.Count++;
            }
        }
    }

    // Esquece as janelas vencidas de tempos em tempos: a memória acompanha as chaves ativas, não o histórico.
    private void Sweep(long agora)
    {
        var ultima = Interlocked.Read(ref _lastSweepTicks);
        if (agora - ultima < _window.Ticks * 4 || Interlocked.CompareExchange(ref _lastSweepTicks, agora, ultima) != ultima)
        {
            return;
        }

        foreach (var par in _windows)
        {
            if (agora - par.Value.StartedTicks >= _window.Ticks * 2)
            {
                _windows.TryRemove(par.Key, out _);
            }
        }
    }

    private sealed class Window
    {
        public Window(long startedTicks) => StartedTicks = startedTicks;

        public long StartedTicks;

        public int Count;
    }
}
