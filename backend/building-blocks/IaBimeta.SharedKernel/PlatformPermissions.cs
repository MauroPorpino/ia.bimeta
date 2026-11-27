namespace IaBimeta.SharedKernel;

/// <summary>
/// As permissões da plataforma BImeta (ADR 0043). Só o tenant técnico da plataforma as tem: qualquer outro tenant as descarta no cálculo das permissões efetivas
/// e as recusa na concessão.
/// </summary>
public static class PlatformPermissions
{
    private static readonly HashSet<Permission> All = new()
    {
        Permission.PlatformTenantRead, Permission.PlatformTenantCreate, Permission.PlatformTenantUpdate, Permission.PlatformTenantQuotaManage,
        Permission.PlatformFirstAdminManage, Permission.PlatformTenantSuspend, Permission.PlatformOperatorManage, Permission.PlatformPermissionManage,
        Permission.PlatformAuditRead, Permission.PlatformOverviewRead,
    };

    public static bool IsPlatform(Permission permission) => All.Contains(permission);

    public static IReadOnlyCollection<Permission> Items => All;
}
