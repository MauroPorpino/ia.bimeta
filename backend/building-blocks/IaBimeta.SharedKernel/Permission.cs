namespace IaBimeta.SharedKernel;

/// <summary>
/// Permissões explícitas da plataforma. O Identity decide quais concessões dão quais permissões; os demais módulos
/// só perguntam se o contexto da operação tem a permissão sobre o recurso (<see cref="AccessPolicy"/>).
/// </summary>
public enum Permission
{
    /// <summary>Ler uma organização. Sem ela, a organização responde como se não existisse.</summary>
    OrganizationRead = 1,

    /// <summary>Criar organizações no tenant. Só existe com concessão de escopo do tenant.</summary>
    OrganizationCreate = 2,

    /// <summary>Ler unidades e setores de uma organização.</summary>
    StructureRead = 3,

    /// <summary>Criar e alterar unidades e setores de uma organização.</summary>
    StructureManage = 4,

    /// <summary>Ler os usuários com acesso a uma organização.</summary>
    UserRead = 5,

    /// <summary>Criar usuários e conceder ou revogar papéis em uma organização.</summary>
    UserManage = 6,

    /// <summary>Consultar a trilha de auditoria do tenant. Só existe com concessão de escopo do tenant.</summary>
    AuditRead = 7,
}
