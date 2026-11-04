using IaBimeta.AiGateway.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// Os limites básicos de IA de um tenant (ADR 0017, seção I.2). Os valores são CONFIGURAÇÃO (alteráveis sem redeploy) e referência a calibrar: nenhum
/// valor é embutido no código. Todos precisam ser positivos.
/// </summary>
/// <param name="MaxConcurrentCalls">As chamadas ao modelo em andamento ao mesmo tempo, no tenant.</param>
/// <param name="MaxCallsPerPeriod">As chamadas ao modelo em cada período.</param>
/// <param name="MaxTokensPerPeriod">
/// Os tokens (entrada mais saída) em cada período. É um teto DURO COM RESERVA: uma chamada só é admitida se o já consumido, o indeterminado, as reservas abertas e a reserva
/// dela (entrada estimada com margem mais o teto de saída) cabem no máximo. O consumo real pode passar da reserva só pelo erro da estimativa de entrada, e o excedente é registrado.
/// </param>
/// <param name="Period">A duração do período (janela fixa, alinhada ao relógio UTC). Uma mudança vale só para a próxima janela: a janela ativa mantém a duração com que abriu.</param>
/// <param name="MaxInputMessages">As mensagens de entrada de uma chamada (o contexto do turno).</param>
/// <param name="MaxOutputTokens">O teto de tokens de saída de uma resposta.</param>
/// <param name="MaxTokensPerCall">O teto de tokens (entrada estimada mais saída) de UMA chamada, quando o tenant define um. Opcional.</param>
public sealed record AiTenantLimits(
    int MaxConcurrentCalls,
    int MaxCallsPerPeriod,
    long MaxTokensPerPeriod,
    TimeSpan Period,
    int MaxInputMessages,
    int MaxOutputTokens,
    long? MaxTokensPerCall = null)
{
    /// <summary>Verdadeiro quando todos os valores são positivos (o teto por chamada, quando existe, também).</summary>
    public bool IsValid =>
        MaxConcurrentCalls > 0
        && MaxCallsPerPeriod > 0
        && MaxTokensPerPeriod > 0
        && Period > TimeSpan.Zero
        && MaxInputMessages > 0
        && MaxOutputTokens > 0
        && (MaxTokensPerCall is null || MaxTokensPerCall > 0);
}

/// <summary>A situação da IA de um tenant: habilitada, desabilitada por ele ou bloqueada pela plataforma.</summary>
public enum AiTenantStatus
{
    /// <summary>A IA do tenant pode ser usada.</summary>
    Enabled = 1,

    /// <summary>O próprio tenant desabilitou a IA.</summary>
    Disabled = 2,

    /// <summary>A plataforma bloqueou a IA do tenant (abuso, cobrança, investigação). O tenant não a reabilita sozinho.</summary>
    Blocked = 3,
}

/// <summary>
/// A configuração de IA de um tenant: o provedor, o modelo, a origem da credencial, os limites e a situação. É escolha do tenant (ADR 0017, seção D): o produto
/// não presume modelo. O <see cref="TenantId"/> acompanha a configuração para que uma leitura que devolva a de OUTRO tenant seja reconhecida e recusada.
/// </summary>
public sealed record AiTenantConfiguration(
    TenantId TenantId,
    string ProviderId,
    string ModelId,
    AiCredentialSource CredentialSource,
    AiTenantLimits Limits,
    AiTenantStatus Status = AiTenantStatus.Enabled);
