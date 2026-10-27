using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Concessão de um papel a um usuário. Com <see cref="OrganizationId"/> definido, vale só para aquela organização;
/// nulo, vale para todo o tenant (escopo do tenant, criado apenas pelo bootstrap do administrador). Há uma concessão
/// por usuário e escopo: conceder de novo troca o papel.
/// </summary>
public sealed class UserOrganizationGrant : ITenantOwned
{
    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private UserOrganizationGrant(Guid id, TenantId tenantId, Guid userId, Guid? organizationId, Role role, DateTimeOffset grantedAt, Guid? grantedByUserId)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        OrganizationId = organizationId;
        Role = role;
        GrantedAt = grantedAt;
        GrantedByUserId = grantedByUserId;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid UserId { get; }

    public Guid? OrganizationId { get; }

    public Role Role { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>Quem concedeu. Nulo quando foi o bootstrap (contexto de sistema).</summary>
    public Guid? GrantedByUserId { get; private set; }

    public static UserOrganizationGrant Create(TenantId tenantId, Guid userId, Guid? organizationId, Role role, DateTimeOffset now, Guid? grantedByUserId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ValidateRole(role);

        if (userId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new IdentityValidationException("A concessão precisa de um usuário e, se houver escopo de organização, de uma organização válida.");
        }

        return new UserOrganizationGrant(Guid.CreateVersion7(), tenantId, userId, organizationId, role, now, grantedByUserId);
    }

    public void Reassign(Role role, DateTimeOffset now, Guid? grantedByUserId)
    {
        ValidateRole(role);

        Role = role;
        GrantedAt = now;
        GrantedByUserId = grantedByUserId;
    }

    public static void ValidateRole(Role role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new IdentityValidationException("O papel informado não existe.");
        }
    }
}
