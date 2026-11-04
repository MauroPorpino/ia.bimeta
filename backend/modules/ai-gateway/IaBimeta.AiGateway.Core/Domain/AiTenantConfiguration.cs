using IaBimeta.AiGateway.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// Os limites básicos de IA de um tenant (ADR 0017, seção I.2). Os valores são CONFIGURAÇÃO (alteráveis sem redeploy) e referência a calibrar: nenhum
/// valor é embutido no código. Todos precisam ser positivos.
/// </summary>
/// <param name="MaxConcurrentCalls">As chamadas ao modelo em andamento ao mesmo tempo, no tenant.</param>
/// <param name="MaxCallsPerPeriod">As chamadas ao modelo em cada período.</param>
/// <param name="MaxTokensPerPeriod">Os tokens (entrada mais saída) em cada período. Só é verificado ao começar a chamada: a última chamada do período pode ultrapassá-lo.</param>
/// <param name="Period">A duração do período (janela fixa, alinhada ao relógio UTC).</param>
/// <param name="MaxInputMessages">As mensagens de entrada de uma chamada (o contexto do turno).</param>
/// <param name="MaxOutputTokens">O teto de tokens de saída de uma resposta.</param>
public sealed record AiTenantLimits(
    int MaxConcurrentCalls,
    int MaxCallsPerPeriod,
    long MaxTokensPerPeriod,
    TimeSpan Period,
    int MaxInputMessages,
    int MaxOutputTokens)
{
    /// <summary>Verdadeiro quando todos os valores são positivos.</summary>
    public bool IsValid =>
        MaxConcurrentCalls > 0
        && MaxCallsPerPeriod > 0
        && MaxTokensPerPeriod > 0
        && Period > TimeSpan.Zero
        && MaxInputMessages > 0
        && MaxOutputTokens > 0;
}

/// <summary>
/// A configuração de IA de um tenant: o provedor, o modelo, a origem da credencial e os limites. É escolha do tenant (ADR 0017, seção D): o produto não
/// presume modelo. Esta fatia não a persiste (a persistência e a credencial são da Infrastructure, fatia C1.4); o <see cref="TenantId"/> acompanha a
/// configuração para que uma leitura que devolva a de OUTRO tenant seja reconhecida e recusada.
/// </summary>
public sealed record AiTenantConfiguration(
    TenantId TenantId,
    string ProviderId,
    string ModelId,
    AiCredentialSource CredentialSource,
    AiTenantLimits Limits);
