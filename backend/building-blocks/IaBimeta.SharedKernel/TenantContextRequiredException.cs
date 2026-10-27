namespace IaBimeta.SharedKernel;

/// <summary>Uma operação que exige tenant foi chamada sem contexto confiável. A falha é fechada: nada é executado.</summary>
public sealed class TenantContextRequiredException : InvalidOperationException
{
    public TenantContextRequiredException()
        : base("Esta operação exige um contexto de tenant confiável, e nenhum foi estabelecido.")
    {
    }
}
