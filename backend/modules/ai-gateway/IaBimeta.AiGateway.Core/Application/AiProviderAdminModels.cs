using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.AiGateway.Application;

/// <summary>A categoria de recusa de uma operação administrativa de IA (a API a traduz em 400, 404, 409 ou 429).</summary>
public enum AiAdminRefusalKind
{
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    TooManyRequests = 4,
}

/// <summary>
/// Uma recusa PREVISTA de uma operação administrativa de IA, com um <see cref="Code"/> estável (a interface o traduz em texto) e uma mensagem sem segredo. Nunca carrega credencial, endereço de servidor nem texto de provedor.
/// </summary>
public sealed class AiAdminRefusedException : Exception
{
    public AiAdminRefusedException(AiAdminRefusalKind kind, string code, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        Code = code;
        RetryAfter = retryAfter;
    }

    public AiAdminRefusalKind Kind { get; }

    public string Code { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>A credencial de um provedor como a interface a vê: NUNCA o valor. A impressão digital identifica a credencial sem revelá-la.</summary>
public sealed record AiCredentialView(bool Required, bool Present, string? Fingerprint, DateTimeOffset? UpdatedAt);

/// <summary>O resultado de um diagnóstico, com a data da verificação e se ele está DESATUALIZADO (credencial trocada ou removida depois, ou resultado antigo): um resultado desatualizado nunca confirma funcionamento.</summary>
public sealed record AiDiagnosticView(
    string State,
    string? Code,
    DateTimeOffset CheckedAt,
    bool Stale,
    string? StaleReason,
    long LatencyMs,
    bool? Reachable,
    bool? Authenticated,
    bool? ModelAvailable,
    bool? ConversationCapable,
    int? InputTokens,
    int? OutputTokens,
    string? UsageCertainty,
    string? ReplyPreview);

/// <summary>Um modelo AUTORIZADO do catálogo (nunca um identificador livre). Só o que o catálogo e o diagnóstico informam: nada de capacidade inventada.</summary>
public sealed record AiModelView(
    string ModelId,
    string Profile,
    int ContextWindowTokens,
    int MaxOutputTokens,
    bool IsDefault,
    AiDiagnosticView? Connectivity,
    AiDiagnosticView? Generation);

/// <summary>Um provedor para o tenant. <c>Implemented</c> (adaptador registrado) e <c>Authorized</c> (modelo ativo no catálogo) NÃO significam operacional: isso depende de credencial e de diagnósticos.</summary>
public sealed record AiProviderView(
    string ProviderId,
    bool Implemented,
    bool Authorized,
    string Supply,
    AiCredentialView Credential,
    bool IsDefault,
    bool ConnectivityCheckSupported,
    IReadOnlyList<AiModelView> Models);

/// <summary>A configuração de IA do tenant para a tela: o padrão atual, os provedores e os avisos.</summary>
public sealed record AiSettingsView(
    bool Configured,
    string? DefaultProviderId,
    string? DefaultModelId,
    string? Status,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AiProviderView> Providers);

public sealed record AiDefaultSelectionResult(IReadOnlyList<string> Warnings);

public sealed record AiCredentialRevocationResult(bool Revoked, bool WasDefaultProvider);

/// <summary>O resultado de um teste de conectividade. <c>Superseded</c>: a credencial foi trocada ou removida DURANTE o teste, e o resultado não foi guardado como validado.</summary>
public sealed record AiConnectivityTestView(string ProviderId, string ModelId, bool Supported, AiDiagnosticView? Result, bool Superseded);

/// <summary>O resultado de um teste de geração: o consumo é REAL e está no ledger. <c>Superseded</c> como no teste de conectividade.</summary>
public sealed record AiGenerationTestView(string ProviderId, string ModelId, bool Succeeded, AiDiagnosticView Result, Guid AiRequestId, bool Superseded);

internal enum AiDiagnosticKind
{
    Connectivity = 1,
    Generation = 2,
}

internal sealed record AiDiagnosticEntry(
    AiDiagnosticKind Kind,
    string ProviderId,
    string ModelId,
    DateTimeOffset At,
    string State,
    string? Code,
    string CredentialMarker,
    long LatencyMs,
    bool? Reachable,
    bool? Authenticated,
    bool? ModelAvailable,
    bool? ConversationCapable,
    int? InputTokens,
    int? OutputTokens,
    string? UsageCertainty,
    string? ReplyPreview);

/// <summary>
/// O último resultado de cada diagnóstico, por tenant, provedor, modelo e tipo, EM MEMÓRIA (sem migration). Limites assumidos e documentados: não sobrevive a reinício do Api nem é compartilhado entre instâncias; nesses casos o
/// diagnóstico volta a "não verificado", nunca a "verificado". Cada entrada guarda o marcador da credencial com que foi obtida: trocar ou remover a credencial a torna desatualizada.
/// </summary>
public sealed class AiDiagnosticStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Tenant, string Provider, string Model, int Kind), AiDiagnosticEntry> _entries = new();

    internal void Put(Guid tenant, AiDiagnosticEntry entry) => _entries[(tenant, entry.ProviderId, entry.ModelId, (int)entry.Kind)] = entry;

    internal AiDiagnosticEntry? Get(Guid tenant, string provider, string model, AiDiagnosticKind kind) =>
        _entries.TryGetValue((tenant, provider, model, (int)kind), out var entry) ? entry : null;

    /// <summary>Descarta os resultados do provedor (credencial trocada ou removida).</summary>
    internal void ClearProvider(Guid tenant, string provider)
    {
        foreach (var chave in _entries.Keys.Where(k => k.Tenant == tenant && k.Provider == provider).ToList())
        {
            _entries.TryRemove(chave, out _);
        }
    }
}

/// <summary>
/// A proteção contra abuso dos diagnósticos (por tenant): janela deslizante por tipo e UM teste por vez por provedor. A recusa devolve quando tentar de novo. Em memória: protege o processo, sem arquitetura distribuída.
/// </summary>
public sealed class AiDiagnosticThrottle
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Tenant, int Kind), Queue<DateTimeOffset>> _windows = new();
    private readonly HashSet<(Guid Tenant, string Provider)> _inFlight = new();

    public AiDiagnosticThrottle(TimeProvider clock, int connectivityMax = 10, TimeSpan? connectivityWindow = null, int generationMax = 5, TimeSpan? generationWindow = null)
    {
        _clock = clock;
        ConnectivityMax = connectivityMax;
        ConnectivityWindow = connectivityWindow ?? TimeSpan.FromMinutes(5);
        GenerationMax = generationMax;
        GenerationWindow = generationWindow ?? TimeSpan.FromMinutes(10);
    }

    public int ConnectivityMax { get; }

    public TimeSpan ConnectivityWindow { get; }

    public int GenerationMax { get; }

    public TimeSpan GenerationWindow { get; }

    internal IDisposable Enter(Guid tenant, AiDiagnosticKind kind, string provider)
    {
        var agora = _clock.GetUtcNow();
        var max = kind == AiDiagnosticKind.Generation ? GenerationMax : ConnectivityMax;
        var janela = kind == AiDiagnosticKind.Generation ? GenerationWindow : ConnectivityWindow;
        lock (_gate)
        {
            if (_inFlight.Contains((tenant, provider)))
            {
                throw new AiAdminRefusedException(AiAdminRefusalKind.Conflict, "test-in-progress", "Já existe um teste deste provedor em andamento. Aguarde a conclusão.");
            }

            if (!_windows.TryGetValue((tenant, (int)kind), out var fila))
            {
                fila = new Queue<DateTimeOffset>();
                _windows[(tenant, (int)kind)] = fila;
            }

            while (fila.Count > 0 && agora - fila.Peek() >= janela)
            {
                fila.Dequeue();
            }

            if (fila.Count >= max)
            {
                var espera = janela - (agora - fila.Peek());
                throw new AiAdminRefusedException(AiAdminRefusalKind.TooManyRequests, "rate-limited", "Muitos testes em pouco tempo. Aguarde um pouco e tente de novo.", espera);
            }

            fila.Enqueue(agora);
            _inFlight.Add((tenant, provider));
        }

        return new Lease(this, tenant, provider);
    }

    private sealed class Lease(AiDiagnosticThrottle owner, Guid tenant, string provider) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._inFlight.Remove((tenant, provider));
            }
        }
    }
}
