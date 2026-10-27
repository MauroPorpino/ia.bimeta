namespace IaBimeta.Identity.Application;

/// <summary>
/// Login recusado. A mensagem é a mesma para tenant inexistente, usuário inexistente e senha errada, para não
/// revelar o que existe.
/// </summary>
public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException()
        : base("Credenciais inválidas.")
    {
    }
}

public sealed class DuplicateUserEmailException : Exception
{
    public DuplicateUserEmailException()
        : base("Já existe um usuário com este e-mail neste tenant.")
    {
    }
}

/// <summary>
/// O recurso não existe no tenant ou o usuário não pode nem enxergá-lo. As duas situações são indistinguíveis de
/// propósito, para não revelar a existência do recurso (TEN-2).
/// </summary>
public sealed class IdentityResourceNotFoundException : Exception
{
    public IdentityResourceNotFoundException(string resource)
        : base($"{resource} não encontrado.")
    {
    }
}
