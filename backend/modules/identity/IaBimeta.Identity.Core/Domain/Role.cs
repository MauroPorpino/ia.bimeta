namespace IaBimeta.Identity.Domain;

/// <summary>Papéis iniciais. Cada papel dá um conjunto explícito de permissões (<see cref="Application.RolePermissions"/>).</summary>
public enum Role
{
    /// <summary>Usuário: lê a organização e a sua estrutura.</summary>
    User = 1,

    /// <summary>Gestor: além de ler, gere unidades e setores e vê os usuários da organização.</summary>
    Manager = 2,

    /// <summary>Administrador da organização: além do gestor, gere usuários e concessões da organização.</summary>
    OrganizationAdmin = 3,
}
