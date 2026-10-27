namespace IaBimeta.SharedKernel;

/// <summary>A operação exige um usuário autenticado e o contexto não tem nenhum.</summary>
public sealed class AuthenticationRequiredException : InvalidOperationException
{
    public AuthenticationRequiredException()
        : base("Esta operação exige um usuário autenticado.")
    {
    }
}
