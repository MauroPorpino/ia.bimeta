using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// Lê a configuração de IA de um tenant (provedor, modelo, origem da credencial e limites). A implementação (Infrastructure, fatia C1.4) filtra por
/// tenant; o AiGateway ainda confere que o que voltou é do tenant do contexto e recusa o que não for (defesa em profundidade, TEN-1).
/// </summary>
public interface ITenantAiConfigurationReader
{
    /// <summary>A configuração do tenant, ou nulo se ele ainda não configurou a IA.</summary>
    Task<AiTenantConfiguration?> GetAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolve a credencial do tenant para o provedor, NA HORA do uso (ADR 0017, seção E.2). A implementação (Infrastructure, fatia C1.4) decifra a
/// credencial guardada sob o tenant e o provedor, e falha fechada (nulo) sem chave. O AiGateway a mantém em memória só durante a chamada e nunca a põe em
/// log, em erro nem em resultado.
/// </summary>
public interface IAiCredentialResolver
{
    /// <summary>A credencial do tenant para o provedor, ou nulo se não há uma utilizável.</summary>
    Task<AiCredential?> ResolveAsync(TenantId tenantId, string providerId, CancellationToken cancellationToken);
}

/// <summary>Qual limite do tenant impediu a chamada.</summary>
public enum AiLimitRefusal
{
    /// <summary>Já há o máximo de chamadas simultâneas do tenant.</summary>
    ConcurrentCalls = 1,

    /// <summary>O tenant já fez o máximo de chamadas do período.</summary>
    CallsPerPeriod = 2,

    /// <summary>O tenant já consumiu o máximo de tokens do período.</summary>
    TokensPerPeriod = 3,
}

/// <summary>O pedido de uma vaga para fazer uma chamada: o tenant, a chamada, os limites vigentes e o instante.</summary>
public sealed record AiLeaseRequest(TenantId TenantId, Guid AiRequestId, AiTenantLimits Limits, DateTimeOffset Now);

/// <summary>A vaga concedida para uma chamada. Mantém um lugar entre as chamadas simultâneas do tenant até ser devolvida em <see cref="IAiUsageLedger.CompleteAsync"/>.</summary>
public sealed record AiLease(TenantId TenantId, Guid AiRequestId, DateTimeOffset StartedAt);

/// <summary>A vaga concedida (<see cref="Lease"/>) ou o limite que a recusou (<see cref="Refusal"/>), nunca os dois.</summary>
public sealed record AiLeaseResult(AiLease? Lease, AiLimitRefusal? Refusal);

/// <summary>
/// A contabilidade de uso do tenant (ADR 0017, seção I): a única porta de estado dos limites. A implementação (Infrastructure, fatia C1.4) conta no
/// banco, para valer entre instâncias (BOUNDARIES LIM-1); esta fatia só define o contrato, e os testes usam um dublê em memória.
/// </summary>
public interface IAiUsageLedger
{
    /// <summary>
    /// Pede, de forma ATÔMICA, a vaga para uma chamada: concede só se o tenant tem chamada simultânea livre, chamadas e tokens do período dentro dos
    /// limites, e então ocupa a vaga e conta a chamada. Duas chamadas concorrentes nunca ultrapassam o limite de simultaneidade do tenant.
    /// </summary>
    Task<AiLeaseResult> TryBeginAsync(AiLeaseRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Devolve a vaga e registra o consumo da chamada (tokens somam ao período). É chamada SEMPRE que a vaga foi concedida, também quando a chamada
    /// falha ou é cancelada.
    /// </summary>
    Task CompleteAsync(AiLease lease, AiRequestRecord record, CancellationToken cancellationToken);
}
