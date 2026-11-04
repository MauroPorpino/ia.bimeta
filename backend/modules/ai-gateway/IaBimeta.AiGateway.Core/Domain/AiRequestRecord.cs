using IaBimeta.AiGateway.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Domain;

/// <summary>Como a chamada terminou, para o registro de consumo.</summary>
public enum AiRequestOutcome
{
    /// <summary>O provedor devolveu uma resposta (o motivo de parada, inclusive recusa ou truncamento, fica em <see cref="AiRequestRecord.StopReason"/>).</summary>
    Succeeded = 1,

    /// <summary>A chamada falhou (erro do provedor, tempo esgotado ou resultado inválido do adaptador): o tipo está em <see cref="AiRequestRecord.ErrorKind"/>.</summary>
    Failed = 2,

    /// <summary>O chamador desistiu no meio da chamada.</summary>
    Cancelled = 3,
}

/// <summary>
/// O registro de consumo de UMA chamada ao modelo (<c>AiRequest</c>, ADR 0017, seção I.1): tenant, identificador da chamada, correlação opaca,
/// provedor, modelo, tokens por tipo, custo estimado, latência, resultado, tipo de erro e identificador da requisição no provedor. NÃO guarda o prompt
/// nem a resposta (nem a credencial): o dado de saúde continua só no Conversations. Só as chamadas que chegaram a ser tentadas são registradas.
/// </summary>
public sealed record AiRequestRecord(
    TenantId TenantId,
    Guid AiRequestId,
    string CorrelationId,
    string ProviderId,
    string ModelId,
    AiUsage Usage,
    AiCostEstimate EstimatedCost,
    TimeSpan Latency,
    AiRequestOutcome Outcome,
    AiStopReason? StopReason,
    AiErrorKind? ErrorKind,
    string? ErrorCode,
    string? ProviderRequestId,
    DateTimeOffset OccurredAt);
