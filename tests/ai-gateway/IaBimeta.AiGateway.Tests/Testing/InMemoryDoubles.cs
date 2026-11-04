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
/// A contabilidade de uso em memória: respeita o contrato de <see cref="IAiUsageLedger"/> como a implementação do PostgreSQL (concessão ATÔMICA da vaga com RESERVA de
/// tokens, simultaneidade por contagem de vagas abertas, expiração no <c>TryBegin</c>, conclusão IDEMPOTENTE e reconciliação do indeterminado). Registra a maior
/// simultaneidade observada por tenant e todos os registros de consumo. A janela é fixa, alinhada ao relógio UTC (a regra de mudança de duração é testada no PostgreSQL).
/// </summary>
public sealed class InMemoryAiUsageLedger : IAiUsageLedger
{
    private enum State
    {
        Open = 1,
        Confirmed = 2,
        Indeterminate = 3,
    }

    private sealed class Entry
    {
        public State State { get; set; } = State.Open;

        public long Reserved { get; init; }

        public long Window { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public long Charged { get; set; }
    }

    private sealed class Window
    {
        public int Calls { get; set; }

        public long Confirmed { get; set; }

        public long Indeterminate { get; set; }

        public long Overage { get; set; }
    }

    private sealed class TenantState
    {
        public int MaxInFlightObserved { get; set; }

        public Dictionary<Guid, Entry> Requests { get; } = new();

        public Dictionary<long, Window> Windows { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<TenantId, TenantState> _tenants = new();
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

    /// <summary>Quando verdadeiro, <see cref="TryBeginAsync"/> e <see cref="CompleteAsync"/> falham como um banco fora do ar (<see cref="AiLedgerUnavailableException"/>).</summary>
    public bool Unavailable { get; set; }

    public int InFlight(TenantId tenantId)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) ? estado.Requests.Values.Count(r => r.State == State.Open) : 0;
        }
    }

    public int MaxInFlightObserved(TenantId tenantId)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) ? estado.MaxInFlightObserved : 0;
        }
    }

    /// <summary>As chamadas e os tokens contados (confirmados mais indeterminados) na janela do instante informado.</summary>
    public (int Calls, long Tokens) UsageInWindowOf(TenantId tenantId, DateTimeOffset now, TimeSpan period)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) && estado.Windows.TryGetValue(now.UtcTicks / period.Ticks, out var janela)
                ? (janela.Calls, janela.Confirmed + janela.Indeterminate)
                : (0, 0);
        }
    }

    /// <summary>Os tokens reservados pelas vagas abertas do tenant.</summary>
    public long OpenReservedTokens(TenantId tenantId)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) ? estado.Requests.Values.Where(r => r.State == State.Open).Sum(r => r.Reserved) : 0;
        }
    }

    /// <summary>Os tokens indeterminados (cobrados pela reserva) na janela do instante informado.</summary>
    public long IndeterminateTokensInWindowOf(TenantId tenantId, DateTimeOffset now, TimeSpan period)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) && estado.Windows.TryGetValue(now.UtcTicks / period.Ticks, out var janela) ? janela.Indeterminate : 0;
        }
    }

    /// <summary>O excedente (consumo real acima da reserva) registrado na janela do instante informado.</summary>
    public long OverageTokensInWindowOf(TenantId tenantId, DateTimeOffset now, TimeSpan period)
    {
        lock (_gate)
        {
            return _tenants.TryGetValue(tenantId, out var estado) && estado.Windows.TryGetValue(now.UtcTicks / period.Ticks, out var janela) ? janela.Overage : 0;
        }
    }

    public Task<AiLeaseResult> TryBeginAsync(AiLeaseRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();

            if (!_tenants.TryGetValue(request.TenantId, out var estado))
            {
                estado = new TenantState();
                _tenants[request.TenantId] = estado;
            }

            // Vagas expiradas deste tenant viram consumo indeterminado (cobram a reserva) ANTES da decisão.
            foreach (var aberta in estado.Requests.Values.Where(r => r.State == State.Open && r.ExpiresAt < request.Now))
            {
                aberta.State = State.Indeterminate;
                aberta.Charged = aberta.Reserved;
                estado.Windows[aberta.Window].Indeterminate += aberta.Reserved;
            }

            var chave = request.Now.UtcTicks / request.Limits.Period.Ticks;
            if (!estado.Windows.TryGetValue(chave, out var janela))
            {
                janela = new Window();
                estado.Windows[chave] = janela;
            }

            var abertas = estado.Requests.Values.Count(r => r.State == State.Open);
            if (abertas >= request.Limits.MaxConcurrentCalls)
            {
                return Task.FromResult(new AiLeaseResult(null, AiLimitRefusal.ConcurrentCalls));
            }

            if (janela.Calls >= request.Limits.MaxCallsPerPeriod)
            {
                return Task.FromResult(new AiLeaseResult(null, AiLimitRefusal.CallsPerPeriod));
            }

            var reservadoNaJanela = estado.Requests.Values.Where(r => r.State == State.Open && r.Window == chave).Sum(r => r.Reserved);
            if (janela.Confirmed + janela.Indeterminate + reservadoNaJanela + request.ReservedTokens > request.Limits.MaxTokensPerPeriod)
            {
                return Task.FromResult(new AiLeaseResult(null, AiLimitRefusal.TokensPerPeriod));
            }

            var expira = request.Now + request.LeaseDuration;
            estado.Requests.Add(request.AiRequestId, new Entry { Reserved = request.ReservedTokens, Window = chave, ExpiresAt = expira });
            janela.Calls++;
            estado.MaxInFlightObserved = Math.Max(estado.MaxInFlightObserved, abertas + 1);

            return Task.FromResult(new AiLeaseResult(new AiLease(request.TenantId, request.AiRequestId, request.Now, request.ReservedTokens, expira), null));
        }
    }

    public Task CompleteAsync(AiLease lease, AiRequestRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();

            if (!_tenants.TryGetValue(lease.TenantId, out var estado) || !estado.Requests.TryGetValue(lease.AiRequestId, out var entrada))
            {
                throw new InvalidOperationException("A vaga nunca foi concedida.");
            }

            // Idempotente: uma chamada já CONFIRMADA nunca é contada de novo.
            if (entrada.State == State.Confirmed)
            {
                return Task.CompletedTask;
            }

            var janela = estado.Windows[entrada.Window];
            if (record.UsageCertainty == AiUsageCertainty.Unknown)
            {
                // Indeterminado: cobra a reserva. Se já era indeterminado (a vaga expirou), nada muda.
                if (entrada.State == State.Open)
                {
                    entrada.State = State.Indeterminate;
                    entrada.Charged = entrada.Reserved;
                    janela.Indeterminate += entrada.Reserved;
                    _records.Add(record);
                }

                return Task.CompletedTask;
            }

            // Confirmado: o consumo informado (ou zero, com evidência de que nada foi cobrado). Se estava indeterminado, o indeterminado sai e o real entra (reconciliação).
            var real = record.UsageCertainty == AiUsageCertainty.NoneIncurred ? 0 : (long)record.Usage.InputTokens + record.Usage.OutputTokens;
            if (entrada.State == State.Indeterminate)
            {
                janela.Indeterminate -= entrada.Charged;
            }

            janela.Confirmed += real;
            janela.Overage += Math.Max(0, real - entrada.Reserved);
            entrada.State = State.Confirmed;
            entrada.Charged = real;
            _records.Add(record);
        }

        return Task.CompletedTask;
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new AiLedgerUnavailableException("A contabilidade de uso está fora do ar (dublê).");
        }
    }
}
