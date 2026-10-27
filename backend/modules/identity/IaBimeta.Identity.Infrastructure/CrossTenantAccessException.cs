namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// Tentativa de gravar, alterar ou excluir uma entidade de um tenant diferente do tenant do contexto da operação.
/// É bloqueada antes de chegar ao banco.
/// </summary>
public sealed class CrossTenantAccessException : InvalidOperationException
{
    public CrossTenantAccessException(string message)
        : base(message)
    {
    }
}
