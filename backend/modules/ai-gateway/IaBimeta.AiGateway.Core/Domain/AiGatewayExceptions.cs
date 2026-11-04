namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// A configuração do AiGateway (catálogo de modelos, provedores registrados, opções) é inválida. É erro de implantação, não de requisição: quem monta o
/// módulo falha na partida em vez de operar com uma configuração que aceitaria modelo ou preço sem sentido.
/// </summary>
public sealed class AiGatewayConfigurationException : Exception
{
    public AiGatewayConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>O pedido de geração é inválido (forma, limites ou esquema da saída estruturada). Nada foi chamado nem contabilizado.</summary>
public sealed class AiGatewayValidationException : Exception
{
    public AiGatewayValidationException(string message)
        : base(message)
    {
    }
}
