using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Permissões explícitas de cada papel. Nada é herdado implicitamente: o que um papel pode está escrito aqui.
/// Com escopo de organização, as permissões valem só para aquela organização. As permissões que não têm
/// organização (criar organização e ler a auditoria do tenant) só existem na concessão de escopo do tenant.
/// </summary>
public static class RolePermissions
{
    private static readonly Permission[] UserPermissions =
    {
        Permission.OrganizationRead,
        Permission.StructureRead,
    };

    // O usuário comum ainda não lê atendimentos: o acesso do atendente vem por fila e equipe, no ContactCenter (etapa 4).
    private static readonly Permission[] ManagerPermissions =
    {
        Permission.OrganizationRead,
        Permission.StructureRead,
        Permission.StructureManage,
        Permission.UserRead,
        Permission.ConversationRead,
        Permission.ConversationManage,
        Permission.ChannelRead,
    };

    // Quem gere a organização gere os canais dela: o cadastro dos números e das unidades autorizadas muda quem pode atender.
    private static readonly Permission[] AdminPermissions =
    {
        Permission.OrganizationRead,
        Permission.StructureRead,
        Permission.StructureManage,
        Permission.UserRead,
        Permission.UserManage,
        Permission.ConversationRead,
        Permission.ConversationManage,
        Permission.ChannelRead,
        Permission.ChannelManage,
    };

    private static readonly Permission[] TenantAdminExtraPermissions =
    {
        Permission.OrganizationCreate,
        Permission.AuditRead,
        Permission.TenantSettingsManage,
    };

    /// <param name="organizationId">A organização da concessão, ou nulo para a concessão de escopo do tenant.</param>
    public static IReadOnlyList<PermissionGrant> Expand(Role role, Guid? organizationId)
    {
        var permissoes = role switch
        {
            Role.User => UserPermissions,
            Role.Manager => ManagerPermissions,
            Role.OrganizationAdmin => AdminPermissions,
            _ => Array.Empty<Permission>(),
        };

        var concedidas = permissoes.Select(p => new PermissionGrant(p, organizationId)).ToList();

        if (organizationId is null && role == Role.OrganizationAdmin)
        {
            concedidas.AddRange(TenantAdminExtraPermissions.Select(p => new PermissionGrant(p, null)));
        }

        return concedidas;
    }
}
