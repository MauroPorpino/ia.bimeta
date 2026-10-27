namespace IaBimeta.SharedKernel;

/// <summary>O usuário autenticado não tem a permissão exigida sobre o recurso (nega por padrão, TEN-2).</summary>
public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException(Permission permission)
        : base($"Acesso negado: a permissão '{permission}' é necessária para esta operação.")
    {
        Permission = permission;
    }

    public Permission Permission { get; }
}
