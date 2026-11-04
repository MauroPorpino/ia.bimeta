using IaBimeta.AiGateway.Domain;

namespace IaBimeta.AiGateway.Application;

/// <summary>
/// As opções de plataforma do AiGateway (configuração, sem redeploy). Os valores iniciais são REFERÊNCIA A CALIBRAR (ADR 0017, Pendências): nenhum
/// provedor publica latência oficial, e a estimativa de tokens é uma HEURÍSTICA, não uma medida. Os limites por tenant ficam em <see cref="AiTenantLimits"/>.
/// </summary>
public sealed record AiGatewayOptions
{
    /// <summary>O maior tempo limite de uma chamada: o pedido do consumidor é reduzido a ele.</summary>
    public TimeSpan MaxCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A maior espera sugerida devolvida numa falha retentável: uma espera maior que isso é reduzida a ele.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Quantos caracteres valem um token na estimativa da ENTRADA. Quanto MENOR, mais conservadora a estimativa (mais tokens). Um token vale cerca de 4 caracteres em inglês e menos em
    /// português e em outras escritas: 3 é conservador. Calibre comparando <c>estimated_input_tokens</c> com o consumo real registrado.
    /// </summary>
    public double CharactersPerToken { get; init; } = 3.0;

    /// <summary>A margem de segurança multiplicada sobre a estimativa de entrada (1,3 = 30% a mais). Cobre o erro da heurística, não o garante.</summary>
    public double InputSafetyMargin { get; init; } = 1.3;

    /// <summary>Os tokens que cada mensagem acrescenta pelo envelope do provedor (papel, delimitadores), somados à estimativa de entrada.</summary>
    public int PerMessageOverheadTokens { get; init; } = 8;

    /// <summary>Os tokens fixos da instrução de sistema e do envelope da requisição, somados à estimativa de entrada.</summary>
    public int RequestOverheadTokens { get; init; } = 16;

    /// <summary>
    /// O teto de caracteres da entrada de UMA chamada (instrução de sistema mais todas as mensagens). O provedor não limita a entrada, então este teto, a janela de contexto do modelo e o
    /// teto de tokens por chamada do tenant são o que a limita. Nulo: só a janela de contexto e o teto do tenant valem.
    /// </summary>
    public int? MaxInputCharacters { get; init; }

    /// <summary>A margem somada ao tempo limite efetivo para a validade da vaga: passado <c>timeout + margem</c> sem conclusão, a vaga expira e o consumo vira indeterminado.</summary>
    public TimeSpan LeaseMargin { get; init; } = TimeSpan.FromSeconds(30);

    /// <exception cref="AiGatewayConfigurationException">Algum valor não faz sentido.</exception>
    public void EnsureValid()
    {
        if (MaxCallTimeout <= TimeSpan.Zero || MaxRetryAfter <= TimeSpan.Zero)
        {
            throw new AiGatewayConfigurationException("O tempo limite máximo da chamada e a espera máxima sugerida precisam ser positivos.");
        }

        if (!(CharactersPerToken > 0) || !double.IsFinite(CharactersPerToken))
        {
            throw new AiGatewayConfigurationException("Os caracteres por token da estimativa precisam ser positivos.");
        }

        if (!(InputSafetyMargin >= 1) || !double.IsFinite(InputSafetyMargin))
        {
            throw new AiGatewayConfigurationException("A margem de segurança da estimativa de entrada não pode ser menor que 1 (a estimativa nunca subestima por configuração).");
        }

        if (PerMessageOverheadTokens < 0 || RequestOverheadTokens < 0 || MaxInputCharacters is <= 0 || LeaseMargin < TimeSpan.Zero)
        {
            throw new AiGatewayConfigurationException("Os acréscimos da estimativa, o teto de entrada e a margem da vaga não podem ser negativos (o teto de entrada, quando existe, é positivo).");
        }
    }
}
