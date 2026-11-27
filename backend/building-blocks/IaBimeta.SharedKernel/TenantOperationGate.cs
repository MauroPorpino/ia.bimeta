namespace IaBimeta.SharedKernel;

/// <summary>
/// A situação OPERACIONAL de uma empresa para o processamento automático (Etapa 02, ADR 0043).
/// <see cref="IsOperational"/> é falso enquanto a empresa está suspensa (ou ainda em provisionamento): nada automático começa nem avança (IA, fluxos, envio de mensagens, mídia).
/// <see cref="OperationalSince"/> é o instante da última reativação (nulo se nunca foi suspensa): o que nasceu ANTES dele (mensagem recebida, turno, envio pendente) pertence ao período da suspensão ou anterior
/// e NÃO é processado automaticamente depois da reativação.
/// </summary>
public sealed record TenantOperationState(bool IsOperational, DateTimeOffset? OperationalSince)
{
    public static TenantOperationState Operational { get; } = new(true, null);
}

/// <summary>
/// Porta do processamento automático para saber se a empresa pode operar. Implementada pelo host (lê o registro do tenant). Os módulos a recebem como dependência OPCIONAL: sem ela (testes de módulo isolado)
/// a empresa é considerada operacional. Falha fechada: se o registro não for encontrado, a empresa NÃO é operacional.
/// </summary>
public interface ITenantOperationGate
{
    Task<TenantOperationState> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}
