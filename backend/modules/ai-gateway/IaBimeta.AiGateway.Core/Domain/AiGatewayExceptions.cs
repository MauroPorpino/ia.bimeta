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

/// <summary>
/// A contabilidade de uso (o ledger) não está disponível: o banco não respondeu, a conexão caiu ou o tempo esgotou. É falha de infraestrutura, nunca decisão de limite. Antes da
/// chamada ao provedor a falha é FECHADA (o provedor não é chamado); depois dela, a resposta obtida não se perde.
/// </summary>
public sealed class AiLedgerUnavailableException : Exception
{
    public AiLedgerUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A chave mestra das credenciais BYOK não está disponível (nenhuma ativa, a que cifrou não está configurada ou tem formato inválido). É falha da PLATAFORMA, não do
/// tenant: falha fechada, sem nunca repetir o conteúdo de uma chave na mensagem.
/// </summary>
public sealed class AiSecretsUnavailableException : Exception
{
    public AiSecretsUnavailableException(string message)
        : base(message)
    {
    }
}
