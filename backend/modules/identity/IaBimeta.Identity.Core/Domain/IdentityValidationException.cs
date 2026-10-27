namespace IaBimeta.Identity.Domain;

/// <summary>Dado informado viola uma regra do domínio de identidade. A mensagem nunca contém a senha informada.</summary>
public sealed class IdentityValidationException : Exception
{
    public IdentityValidationException(string message)
        : base(message)
    {
    }
}
