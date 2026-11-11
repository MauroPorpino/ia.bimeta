using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>Os números BRUTOS de uma janela, lidos do banco do módulo para o tenant do contexto (sem texto de cliente).</summary>
public sealed record SemanticMetricsRaw(
    IReadOnlyDictionary<SemanticOutcome, int> DecisionsByOutcome, int FaqHits, int SemanticHits, int ModelReplies, int Handoffs, IReadOnlyList<int> LatenciesMilliseconds);

public interface ISemanticMetricsStore
{
    Task<SemanticMetricsRaw> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>
/// As métricas da camada semântica de UM tenant (ADR 0034). O que é CONTADO é exato (decisões, acertos do FAQ e da busca, respostas do modelo, transferências, latência); o que é ESTIMADO (tokens e custo evitados)
/// NÃO sai daqui: depende do ledger do AiGateway e fica no relatório operacional do host, com o rótulo de estimativa e sem descontar o custo da infraestrutura local. As "chamadas comerciais evitadas" são a soma
/// exata dos turnos respondidos sem o modelo (FAQ determinístico e busca semântica).
/// </summary>
public sealed record SemanticMetricsView(
    int WindowDays,
    int Decisions,
    int Answered,
    int WouldAnswer,
    int NoCandidates,
    int BelowThreshold,
    int Ambiguous,
    int NotAutoReply,
    int GuardRejected,
    int Stale,
    int Unavailable,
    int FaqHits,
    int SemanticHits,
    int ModelReplies,
    int Handoffs,
    int CommercialCallsAvoided,
    double SemanticShareOfAiReplies,
    double? LatencyAverageMilliseconds,
    int? LatencyP95Milliseconds);

public sealed class SemanticMetricsService
{
    public const int MinDays = 1;

    public const int MaxDays = 90;

    private readonly IRequestContextAccessor _context;
    private readonly ISemanticMetricsStore _store;
    private readonly TimeProvider _clock;

    public SemanticMetricsService(IRequestContextAccessor context, ISemanticMetricsStore store, TimeProvider clock)
    {
        _context = context;
        _store = store;
        _clock = clock;
    }

    public async Task<SemanticMetricsView> GetAsync(int days, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.TenantSettingsManage);
        if (days is < MinDays or > MaxDays)
        {
            throw new AgentsValidationException($"A janela das métricas vai de {MinDays} a {MaxDays} dias.");
        }

        var cru = await _store.ReadAsync(_clock.GetUtcNow().AddDays(-days), cancellationToken);
        return Summarize(days, cru);
    }

    public static SemanticMetricsView Summarize(int days, SemanticMetricsRaw raw)
    {
        int N(SemanticOutcome o) => raw.DecisionsByOutcome.TryGetValue(o, out var n) ? n : 0;
        var ordenadas = raw.LatenciesMilliseconds.OrderBy(x => x).ToList();
        var respondidasPelaIa = raw.FaqHits + raw.SemanticHits + raw.ModelReplies;
        return new SemanticMetricsView(
            days,
            raw.DecisionsByOutcome.Values.Sum(),
            N(SemanticOutcome.Answered), N(SemanticOutcome.WouldAnswer), N(SemanticOutcome.NoCandidates), N(SemanticOutcome.BelowThreshold), N(SemanticOutcome.Ambiguous),
            N(SemanticOutcome.NotAutoReply), N(SemanticOutcome.GuardRejected), N(SemanticOutcome.Stale), N(SemanticOutcome.Unavailable),
            raw.FaqHits, raw.SemanticHits, raw.ModelReplies, raw.Handoffs,
            raw.FaqHits + raw.SemanticHits,
            respondidasPelaIa == 0 ? 0 : Math.Round(100.0 * raw.SemanticHits / respondidasPelaIa, 1),
            ordenadas.Count == 0 ? null : Math.Round(ordenadas.Average(), 1),
            ordenadas.Count == 0 ? null : ordenadas[Math.Min(ordenadas.Count - 1, (int)Math.Ceiling(0.95 * ordenadas.Count) - 1)]);
    }
}
