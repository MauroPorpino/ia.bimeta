namespace IaBimeta.Tenancy.Domain;

/// <summary>Dado informado viola uma regra do domínio de tenancy.</summary>
public sealed class TenancyValidationException : Exception
{
    public TenancyValidationException(string message)
        : base(message)
    {
    }
}
