namespace IaBimeta.ContactCenter.Domain;

/// <summary>Dados inválidos de uma fila ou de um atendente de fila.</summary>
public sealed class ContactCenterValidationException : Exception
{
    public ContactCenterValidationException(string message)
        : base(message)
    {
    }
}
