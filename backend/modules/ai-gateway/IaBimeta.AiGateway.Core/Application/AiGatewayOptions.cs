using IaBimeta.AiGateway.Domain;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// As opções de plataforma do AiGateway (configuração, sem redeploy). Os valores iniciais são REFERÊNCIA A CALIBRAR (ADR 0017, Pendências): nenhum
/// provedor publica latência oficial. Os limites por tenant ficam em <see cref="AiTenantLimits"/>, na configuração do tenant.
/// </summary>
public sealed record AiGatewayOptions
{
    /// <summary>O maior tempo limite de uma chamada: o pedido do consumidor é reduzido a ele.</summary>
    public TimeSpan MaxCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A maior espera sugerida devolvida numa falha retentável: uma espera maior que isso é reduzida a ele.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <exception cref="AiGatewayConfigurationException">Algum valor não é positivo.</exception>
    public void EnsureValid()
    {
        if (MaxCallTimeout <= TimeSpan.Zero || MaxRetryAfter <= TimeSpan.Zero)
        {
            throw new AiGatewayConfigurationException("O tempo limite máximo da chamada e a espera máxima sugerida precisam ser positivos.");
        }
    }
}
