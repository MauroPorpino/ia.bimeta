using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Messaging;

/// <summary>
/// Parâmetros do relay (BOUNDARIES.md §12.5): configuração, não constantes de código. Os valores padrão servem ao primeiro
/// processamento; o que for medido ajusta depois.
/// </summary>
public sealed class MessagingOptions
{
    /// <summary>Quantas tentativas um evento tem antes de ir para os mortos como "veneno".</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Quantos eventos o relay pega por ciclo.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Quanto tempo um lease vale. Passado esse tempo sem conclusão, outro relay pode pegar o evento (queda do processo).</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A espera depois da primeira falha; dobra a cada tentativa, com jitter.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>De quanto em quanto tempo o relay olha o outbox (polling; <c>LISTEN/NOTIFY</c> só depois de medir).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Quanto tempo os processados ficam guardados antes da purga.</summary>
    public TimeSpan ProcessedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        if (MaxAttempts < 1 || BatchSize < 1 || BatchSize > 1000 || LeaseDuration <= TimeSpan.Zero || BaseBackoff <= TimeSpan.Zero
            || MaxBackoff < BaseBackoff || PollInterval <= TimeSpan.Zero || ProcessedRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A configuração do Messaging é inválida (tentativas, lote, lease, espera, intervalo ou retenção).");
        }
    }
}

/// <summary>A espera entre tentativas: exponencial com jitter, limitada. Função pura sobre os parâmetros e o sorteio.</summary>
public static class Backoff
{
    /// <param name="attempt">O número da tentativa que acabou de falhar (1 para a primeira).</param>
    /// <param name="jitter">Um valor de 0 a 1 (sorteado pelo chamador, para o teste ser determinístico).</param>
    public static TimeSpan For(int attempt, MessagingOptions options, double jitter)
    {
        ArgumentNullException.ThrowIfNull(options);
        var passo = Math.Max(1, attempt);

        // 2^(passo-1) vezes a base, sem estourar: o expoente é limitado antes de multiplicar.
        var multiplicador = Math.Pow(2, Math.Min(passo - 1, 30));
        var bruto = TimeSpan.FromTicks((long)Math.Min(options.BaseBackoff.Ticks * multiplicador, options.MaxBackoff.Ticks));

        // O jitter espalha as tentativas entre 50% e 100% do valor: dois eventos que falharam juntos não voltam juntos.
        var fator = 0.5 + (0.5 * Math.Clamp(jitter, 0, 1));
        return TimeSpan.FromTicks((long)(bruto.Ticks * fator));
    }
}

/// <summary>O JSON do payload dos eventos: o mesmo para gravar no outbox e para reconstruir o contrato no handler.</summary>
public static class MessagingJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, payload.GetType(), Options);
    }

    public static object Deserialize(string json, Type payloadType) =>
        JsonSerializer.Deserialize(json, payloadType, Options) ?? throw new JsonException("O payload do evento é nulo.");
}

/// <summary>
/// Grava os eventos no outbox do módulo na transação em curso do DbContext dele (BOUNDARIES.md §12.9): o evento só existe se a
/// mudança de estado for confirmada junto. Não confirma nada sozinho: quem chama confirma com o <c>SaveChanges</c> da própria
/// unidade de trabalho.
/// </summary>
public interface IOutboxWriter
{
    /// <exception cref="InvalidOperationException">Algum evento é de outro tenant que o da operação: nunca se grava evento de um tenant no contexto de outro.</exception>
    void Stage(DbContext db, IReadOnlyCollection<EventEnvelope> events, TenantId expectedTenant, DateTimeOffset now);
}

public sealed class OutboxWriter : IOutboxWriter
{
    public void Stage(DbContext db, IReadOnlyCollection<EventEnvelope> events, TenantId expectedTenant, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(expectedTenant);

        if (events.Any(e => e.TenantId != expectedTenant))
        {
            throw new InvalidOperationException("Um evento de outro tenant não pode ser gravado no outbox desta operação.");
        }

        foreach (var evento in events)
        {
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Id = evento.EventId,
                TenantId = evento.TenantId.Value,
                OrderingKey = evento.OrderingKey,
                EventType = evento.Type,
                EventVersion = evento.Version,
                CorrelationId = evento.CorrelationId,
                CausationId = evento.CausationId,
                OccurredAt = evento.OccurredAt,
                Payload = MessagingJson.Serialize(evento.Payload),
                State = OutboxState.Pending,
                Attempts = 0,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }
    }
}

/// <summary>As métricas do Messaging (<c>System.Diagnostics.Metrics</c>, sem pacote novo; BOUNDARIES.md §12.10). Sem tenant como rótulo: cardinalidade fixa.</summary>
public static class MessagingMetrics
{
    public const string MeterName = "IaBimeta.Messaging";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Processed = Meter.CreateCounter<long>("outbox.processed", description: "Eventos entregues com sucesso.");

    public static readonly Counter<long> Retried = Meter.CreateCounter<long>("outbox.retried", description: "Eventos devolvidos para nova tentativa.");

    public static readonly Counter<long> Dead = Meter.CreateCounter<long>("outbox.dead", description: "Eventos que foram para os mortos.");

    public static readonly Counter<long> LeaseLost = Meter.CreateCounter<long>("outbox.lease_lost", description: "Conclusões recusadas por lease perdido.");

    public static readonly Histogram<double> ProcessingSeconds = Meter.CreateHistogram<double>("outbox.processing_seconds", "s", "Tempo de entrega de um evento aos handlers.");
}
