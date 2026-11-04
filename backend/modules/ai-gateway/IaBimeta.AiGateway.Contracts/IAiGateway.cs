namespace IaBimeta.AiGateway.Contracts;

/// <summary>
/// O que o Agents chama (ADR 0017, seção B): gera uma resposta de IA para o tenant do contexto confiável. O AiGateway escolhe o provedor e o modelo
/// da configuração do tenant, aplica os limites, resolve a credencial na hora, chama o adaptador do provedor escolhido (e SÓ ele: não há fallback
/// entre provedores), normaliza o consumo e os erros e estima o custo. Só um contexto de sistema chama.
/// </summary>
public interface IAiGateway
{
    /// <summary>
    /// Gera a resposta. Falha do provedor, limite do tenant e configuração ausente voltam como <see cref="AiGenerationResult.Failure"/>; pedido
    /// inválido, contexto de tenant ausente, acesso negado e cancelamento continuam sendo exceções.
    /// </summary>
    Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken = default);
}
