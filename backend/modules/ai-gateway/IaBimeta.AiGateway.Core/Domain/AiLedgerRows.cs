using IaBimeta.AiGateway.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Domain;

/// <summary>A situação de uma chamada no ledger.</summary>
public enum AiRequestState
{
    /// <summary>RESERVADA: a vaga está aberta e retém tokens do orçamento do período.</summary>
    Reserved = 1,

    /// <summary>CONFIRMADA: o provedor informou o consumo, ou se provou que nada foi cobrado. Definitiva.</summary>
    Confirmed = 2,

    /// <summary>INDETERMINADA: pode ter sido cobrada e não se sabe quanto; cobra a reserva até uma reconciliação, que a leva a <see cref="Confirmed"/> uma única vez.</summary>
    Indeterminate = 3,
}

/// <summary>
/// A linha do ledger de UMA chamada ao modelo (tabela <c>ai_requests</c>): o estado, a reserva, o que está aplicado à janela e o consumo por tipo. NÃO guarda o prompt, a resposta nem a
/// credencial. Quem a grava é só o <c>PostgresAiUsageLedger</c>, por SQL atômico; esta entidade serve à leitura e ao modelo do EF. A chave é <c>(tenant_id, ai_request_id)</c> (TEN-3).
/// </summary>
public sealed class AiRequest : ITenantOwned
{
    private AiRequest()
    {
        TenantId = null!;
        ProviderId = null!;
        ModelId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid AiRequestId { get; private set; }

    public AiRequestState State { get; private set; }

    /// <summary>A correlação opaca do consumidor (por exemplo, o <c>TurnId</c>). Liga ao atendimento: é a parte "pessoal" do registro, a pseudonimizar na retenção.</summary>
    public string? CorrelationId { get; private set; }

    public string ProviderId { get; private set; }

    public string ModelId { get; private set; }

    /// <summary>Os tokens retidos do orçamento do período enquanto a vaga está aberta (entrada estimada com margem mais o teto de saída).</summary>
    public long ReservedTokens { get; private set; }

    /// <summary>A entrada estimada em tokens, guardada ao lado do consumo real para calibrar a heurística.</summary>
    public long EstimatedInputTokens { get; private set; }

    public DateTimeOffset WindowStart { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset LeaseExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int InputTokens { get; private set; }

    public int CachedInputTokens { get; private set; }

    public int CacheWriteTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public int? ReasoningTokens { get; private set; }

    /// <summary>O que está APLICADO à janela por esta chamada: 0 nada (reservada), <see cref="AiRequestState.Confirmed"/> ou <see cref="AiRequestState.Indeterminate"/>. Toda transição calcula o delta daqui.</summary>
    public short AppliedKind { get; private set; }

    /// <summary>Os tokens aplicados à janela (o consumo real se confirmada; a reserva se indeterminada).</summary>
    public long AppliedTokens { get; private set; }

    /// <summary>O excedente aplicado: o consumo real acima da reserva (só na confirmada).</summary>
    public long AppliedOverage { get; private set; }

    public decimal EstimatedCost { get; private set; }

    public string? CostCurrency { get; private set; }

    public int? LatencyMs { get; private set; }

    public AiRequestOutcome? Outcome { get; private set; }

    public AiStopReason? StopReason { get; private set; }

    public AiErrorKind? ErrorKind { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ProviderRequestId { get; private set; }

    public AiUsageCertainty? UsageCertainty { get; private set; }
}

/// <summary>
/// A janela de uso de um tenant (tabela <c>usage_windows</c>): os contadores de UM período. É um CACHE transacional, atualizado na mesma transação de cada mudança de estado da chamada; a
/// fonte de verdade é <c>ai_requests</c>, e os contadores podem ser reconstruídos por agregação. A janela ativa guarda o início, o fim e a duração com que abriu: mudar a duração do período do
/// tenant não a muda nem reinicia o consumo já feito. A chave é <c>(tenant_id, window_start)</c> (TEN-3).
/// </summary>
public sealed class AiUsageWindow : ITenantOwned
{
    private AiUsageWindow()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public DateTimeOffset WindowStart { get; private set; }

    public DateTimeOffset WindowEnd { get; private set; }

    public int PeriodSeconds { get; private set; }

    /// <summary>As chamadas admitidas na janela (contadas ao conceder a vaga).</summary>
    public int Calls { get; private set; }

    /// <summary>Os tokens CONFIRMADOS (consumo real).</summary>
    public long ConfirmedTokens { get; private set; }

    /// <summary>Os tokens INDETERMINADOS, cobrados pela reserva até a reconciliação.</summary>
    public long IndeterminateTokens { get; private set; }

    /// <summary>O excedente acumulado: o consumo real que passou da reserva.</summary>
    public long OverageTokens { get; private set; }
}
