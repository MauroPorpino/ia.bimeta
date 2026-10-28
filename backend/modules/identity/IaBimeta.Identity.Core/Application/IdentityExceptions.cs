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

/// <summary>
/// Muitas senhas erradas para este e-mail: o login está bloqueado por um tempo, e a senha nem é verificada. A resposta
/// é a mesma exista o e-mail ou não, para não revelar o que existe.
/// </summary>
public sealed class LoginThrottledException : Exception
{
    public LoginThrottledException(TimeSpan retryAfter)
        : base("Muitas tentativas de login. Tente novamente mais tarde.")
    {
        RetryAfter = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    public TimeSpan RetryAfter { get; }
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
