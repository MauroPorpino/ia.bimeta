namespace IaBimeta.Capabilities.Domain;

/// <summary>A entrada de administração é inválida. <see cref="Problems"/> são frases fixas com o NOME do campo e a regra, nunca valores digitados.</summary>
public sealed class CapabilitiesValidationException : Exception
{
    public CapabilitiesValidationException(IReadOnlyList<string> problems)
        : base("Dados inválidos: " + string.Join("; ", problems))
    {
        Problems = problems;
    }

    public CapabilitiesValidationException(string problem)
        : this([problem])
    {
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Outra operação mudou o recurso (ou usou a mesma chave) primeiro.</summary>
public sealed class CapabilitiesConflictException : Exception
{
    public CapabilitiesConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary><c>key-in-use</c>, <c>changed</c>, <c>not-draft</c>, <c>no-draft</c>, <c>limit-reached</c>, <c>already-confirmed</c>.</summary>
    public string Code { get; }
}

/// <summary>O recurso de infraestrutura necessário (a chave de cifra dos segredos) não está configurado. Nada foi gravado.</summary>
public sealed class CapabilitiesUnavailableException : Exception
{
    public CapabilitiesUnavailableException(string code)
        : base("O recurso necessário não está disponível neste ambiente.")
    {
        Code = code;
    }

    public string Code { get; }
}
