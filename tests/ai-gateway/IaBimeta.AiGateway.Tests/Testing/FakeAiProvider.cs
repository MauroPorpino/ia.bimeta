using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.AiGateway.Testing;

/// <summary>
/// O dublê de provedor de IA dos testes (BOUNDARIES §12, ADR 0017, seção B): obedece à porta <see cref="IAiProvider"/> sem rede e sem credencial real, com
/// latência e falha INJETÁVEIS. Registra cada pedido recebido (para os testes inspecionarem o que o provedor veria) e a maior concorrência observada. O
/// <see cref="Behavior"/> decide o resultado de cada chamada; por padrão devolve uma resposta de texto. Nos testes do Agents e de integração, o mesmo dublê
/// é reutilizado (o contador de chamadas prova, por exemplo, que um handler nunca chama o modelo).
/// </summary>
public sealed class FakeAiProvider : IAiProvider
{
    private readonly object _gate = new();
    private readonly List<AiCompletionRequest> _requests = new();
    private int _inFlight;
    private int _maxInFlight;

    public FakeAiProvider(string providerId)
    {
        ProviderId = providerId;
        Behavior = (_, _) => Task.FromResult(Succeed("resposta de teste"));
    }

    public string ProviderId { get; }

    /// <summary>O que cada chamada devolve. Pode lançar, esperar ou ignorar o cancelamento: é o ponto de injeção de falha.</summary>
    public Func<AiCompletionRequest, CancellationToken, Task<AiCompletionResult>> Behavior { get; set; }

    /// <summary>A latência injetada: cada chamada espera isto (honrando o cancelamento) antes de aplicar o <see cref="Behavior"/>.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    public int MaxObservedConcurrency
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }
    }

    public IReadOnlyList<AiCompletionRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public async Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        try
        {
            if (Latency > TimeSpan.Zero)
            {
                await Task.Delay(Latency, cancellationToken);
            }

            return await Behavior(request, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }

    public AiCompletionResult Succeed(string text, AiUsage? usage = null, AiStopReason stopReason = AiStopReason.Completed, string? providerRequestId = "req-teste") =>
        AiCompletionResult.Success(new AiCompletion(text, stopReason), usage ?? new AiUsage(100, 0, 0, 20), Latency, providerRequestId);

    public AiCompletionResult Fail(AiErrorKind kind, TimeSpan? retryAfter = null, string? code = null, AiUsage? usage = null) =>
        AiCompletionResult.Failure(new AiError(kind, retryAfter, code), Latency, usage, "req-teste");

    /// <summary>A próxima e todas as chamadas seguintes devolvem este resultado.</summary>
    public FakeAiProvider Returns(AiCompletionResult result)
    {
        Behavior = (_, _) => Task.FromResult(result);
        return this;
    }

    /// <summary>As chamadas lançam a exceção (um adaptador com defeito, em vez de devolver a falha normalizada).</summary>
    public FakeAiProvider Throws(Exception exception)
    {
        Behavior = (_, _) => throw exception;
        return this;
    }

    /// <summary>As chamadas só terminam quando o portão é liberado (ou o cancelamento chega): serve aos testes de simultaneidade e de cancelamento.</summary>
    public FakeAiProvider BlocksUntil(TaskCompletionSource gate)
    {
        Behavior = async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return Succeed("resposta liberada");
        };
        return this;
    }

    /// <summary>As chamadas esperam sem fim, honrando o cancelamento (o provedor "pendurado", que só o tempo limite ou o chamador encerram).</summary>
    public FakeAiProvider Hangs()
    {
        Behavior = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Succeed("nunca chega");
        };
        return this;
    }
}
