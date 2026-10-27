namespace IaBimeta.SharedKernel;

/// <summary>
/// Uma permissão efetiva do usuário. Com <see cref="OrganizationId"/> nulo, vale para todo o tenant; com valor, só
/// para aquela organização.
/// </summary>
public sealed record PermissionGrant(Permission Permission, Guid? OrganizationId);
