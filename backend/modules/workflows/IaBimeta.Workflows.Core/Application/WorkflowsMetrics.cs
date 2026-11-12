using System.Diagnostics;
using System.Diagnostics.Metrics;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <summary>
/// As métricas do runtime (<c>Meter</c> <c>IaBimeta.Workflows</c>, ADR 0037 §7): só contagens e durações com etiquetas de baixa cardinalidade (situação, tipo de efeito, resultado). NUNCA conteúdo de cliente, variável
/// nem identificador de conversa. São por processo; a evidência durável é o histórico (instâncias, passos e efeitos) no banco, que as telas de métricas leem.
/// </summary>
public static class WorkflowsMetrics
{
    public const string MeterName = "IaBimeta.Workflows";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Started = Meter.CreateCounter<long>("workflows.instances.started", description: "Instâncias de fluxo criadas pelo roteador.");

    private static readonly Counter<long> Ended = Meter.CreateCounter<long>("workflows.instances.ended", description: "Instâncias de fluxo encerradas, por situação final.");

    private static readonly Counter<long> Effects = Meter.CreateCounter<long>("workflows.effects", description: "Efeitos realizados, por tipo e resultado.");

    private static readonly Histogram<double> InstanceSeconds = Meter.CreateHistogram<double>("workflows.instance.duration", unit: "s", description: "Duração de uma instância, do início ao encerramento.");

    public static void InstanceStarted() => Started.Add(1);

    public static void InstanceEnded(FlowStatus status, TimeSpan duration)
    {
        Ended.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
        InstanceSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("status", status.ToString()));
    }

    public static void EffectDone(EffectKind kind, EffectState state) =>
        Effects.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()), new KeyValuePair<string, object?>("result", state.ToString()));
}
