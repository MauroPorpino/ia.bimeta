namespace IaBimeta.ContactCenter.Domain;

/// <summary>Dados inválidos de uma fila ou de um atendente de fila.</summary>
public sealed class ContactCenterValidationException : Exception
{
    public ContactCenterValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Com o código estável do campo recusado (o cliente da API corrige o dado pelo código, não pelo texto).</summary>
    public ContactCenterValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string? Code { get; }
}
