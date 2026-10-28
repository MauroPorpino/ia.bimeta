namespace IaBimeta.Channels.Domain;

/// <summary>Dados ou configuração inválidos. A mensagem nunca contém o valor de um segredo.</summary>
public sealed class ChannelsValidationException : Exception
{
    public ChannelsValidationException(string message)
        : base(message)
    {
    }
}
