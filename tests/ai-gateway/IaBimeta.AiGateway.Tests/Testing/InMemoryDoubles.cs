using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Testing;

/// <summary>Configuração de IA por tenant, em memória (a persistência é da fatia C1.4). Registra quem leu, para provar o isolamento.</summary>
public sealed class FakeTenantAiConfigurationReader : ITenantAiConfigurationReader
{
    private readonly object _gate = new();
    private readonly Dictionary<TenantId, AiTenantConfiguration> _configurations = new();
    private readonly List<TenantId> _reads = new();

    /// <summary>Os tenants para os quais a configuração foi pedida, em ordem.</summary>
    public IReadOnlyList<TenantId> Reads
    {
        get
        {
            lock (_gate)
            {
                return _reads.ToList();
            }
        }
    }

    /// <summary>Quando definida, é devolvida a QUALQUER tenant: simula um leitor com defeito que vaza a configuração de outro tenant.</summary>
    public AiTenantConfiguration? LeakedConfiguration { get; set; }

    public FakeTenantAiConfigurationReader Set(AiTenantConfiguration configuration)
    {
        lock (_gate)
        {
            _configurations[configuration.TenantId] = configuration;
        }

        return this;
    }

    public Task<AiTenantConfiguration?> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _reads.Add(tenantId);
            return Task.FromResult(LeakedConfiguration ?? _configurations.GetValueOrDefault(tenantId));
        }
    }
}

/// <summary>Credenciais SINTÉTICAS por tenant e provedor, em memória. Nunca há chave real: os valores são canários de teste.</summary>
public sealed class FakeAiCredentialResolver : IAiCredentialResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<(TenantId, string), AiCredential> _credentials = new();
    private readonly List<(TenantId TenantId, string ProviderId)> _resolutions = new();

    /// <summary>As credenciais pedidas (tenant e provedor), em ordem.</summary>
    public IReadOnlyList<(TenantId TenantId, string ProviderId)> Resolutions
    {
        get
        {
            lock (_gate)
            {
                return _resolutions.ToList();
            }
        }
    }

    public FakeAiCredentialResolver Set(TenantId tenantId, string providerId, string secret)
    {
        lock (_gate)
        {
            _credentials[(tenantId, providerId)] = new AiCredential(secret);
        }

        return this;
    }

    public Task<AiCredential?> ResolveAsync(TenantId tenantId, string providerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _resolutions.Add((tenantId, providerId));
            return Task.FromResult(_credentials.GetValueOrDefault((tenantId, providerId)));
        }
    }
}

/// <summary>
/// A contabilidade de uso em memória: respeita o contrato de <see cref="IAiUsageLedger"/> (concessão ATÔMICA da vaga, simultaneidade, chamadas e tokens do
/// período, devolução da vaga em toda conclusão). Registra a maior simultaneidade observada por tenant e todos os registros de consumo.
/// </summary>
public sealed class InMemoryAiUsageLedger : IAiUsageLedger
{
    private sealed class TenantState
    {
        public int InFlight { get; set; }

        public int MaxInFlightObserved { get; set; }

        public Dictionary<long, (int Calls, long Tokens)> Windows { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<TenantId, TenantState> _tenants = new();
    private readonly Dictionary<(TenantId, Guid), long> _openLeases = new();
    private readonly List<AiRequestRecord> _records = new();

    public IReadOnlyList<AiRequestRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToList();
            }
        }
    }

    public int InFlight(TenantId tenantId)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) ? estado.InFlight : 0;
        }
    }

    public int MaxInFlightObserved(TenantId tenantId)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) ? estado.MaxInFlightObserved : 0;
        }
    }

    /// <summary>As chamadas e os tokens contados na janela do instante informado.</summary>
    public (int Calls, long Tokens) UsageInWindowOf(TenantId tenantId, DateTimeOffset now, TimeSpan period)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) && estado.Windows.TryGetValue(now.UtcTicks / period.Ticks, out var janela) ? janela : (0, 0);
        }
    }

    public Task<AiLeaseResult> TryBeginAsync(AiLeaseRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_tenants.TryGetValue(request.TenantId, out var estado))
            {
                estado = new TenantState();
                _tenants[request.TenantId] = estado;
            }

            var chave = request.Now.UtcTicks / request.Limits.Period.Ticks;
            estado.Windows.TryGetValue(chave, out var janela);

            if (estado.InFlight >= request.Limits.MaxConcurrentCalls)
            {
                return Task.FromResult(new AiLeaseResult(null, AiLimitRefusal.ConcurrentCalls));
            }

            if (janela.Calls >= request.Limits.MaxCallsPerPeriod)
            {
                return Task.FromResult(new AiLeaseResult(null, AiLimitRefusal.CallsPerPeriod));
            }

            if (janela.Tokens >= request.Limits.MaxTokensPerPeriod)
            {
                return Task.FromResult(new AiLeaseResult(null, AiLimitRefusal.TokensPerPeriod));
            }

            estado.InFlight++;
            estado.MaxInFlightObserved = Math.Max(estado.MaxInFlightObserved, estado.InFlight);
            estado.Windows[chave] = (janela.Calls + 1, janela.Tokens);
            _openLeases[(request.TenantId, request.AiRequestId)] = chave;

            return Task.FromResult(new AiLeaseResult(new AiLease(request.TenantId, request.AiRequestId, request.Now), null));
        }
    }

    public Task CompleteAsync(AiLease lease, AiRequestRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_openLeases.Remove((lease.TenantId, lease.AiRequestId), out var chave))
            {
                throw new InvalidOperationException("A vaga já foi devolvida ou nunca foi concedida.");
            }

            var estado = _tenants[lease.TenantId];
            estado.InFlight--;

            // Os tokens somam na janela em que a chamada COMEÇOU, a mesma em que ela foi contada.
            var janela = estado.Windows[chave];
            estado.Windows[chave] = (janela.Calls, janela.Tokens + record.Usage.InputTokens + record.Usage.OutputTokens);
            _records.Add(record);
        }

        return Task.CompletedTask;
    }
}
