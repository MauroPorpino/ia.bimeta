using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>O efeito de um ajuste individual sobre as permissões que o perfil dá.</summary>
public enum OverrideEffect
{
    /// <summary>Acrescenta uma permissão que o perfil não dá.</summary>
    Grant = 1,

    /// <summary>Retira uma permissão que o perfil dá. Prevalece sobre qualquer concessão.</summary>
    Deny = 2,
}

/// <summary>
/// Um ajuste individual sobre o perfil de um usuário (Etapa 01): guarda só a DIFERENÇA em relação às permissões padrão do papel, no mesmo escopo da concessão (organização, ou nulo para o tenant). Mudar o perfil padrão
/// depois propaga para todos, e as restrições individuais continuam valendo. Nunca é um privilégio global: vale só no escopo em que foi criado. Existe no máximo um ajuste por usuário, escopo e permissão.
/// </summary>
public sealed class UserPermissionOverride : ITenantOwned
{
    private UserPermissionOverride(Guid id, TenantId tenantId, Guid userId, Guid? organizationId, Permission permission, OverrideEffect effect, DateTimeOffset createdAt, Guid? createdByUserId)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        OrganizationId = organizationId;
        Permission = permission;
        Effect = effect;
        CreatedAt = createdAt;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid UserId { get; }

    /// <summary>A organização do escopo, ou nulo para o escopo do tenant.</summary>
    public Guid? OrganizationId { get; }

    public Permission Permission { get; }

    public OverrideEffect Effect { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    public static UserPermissionOverride Create(TenantId tenantId, Guid userId, Guid? organizationId, Permission permission, OverrideEffect effect, DateTimeOffset now, Guid? createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (userId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new IdentityValidationException("O ajuste precisa de um usuário e, se houver escopo de organização, de uma organização válida.");
        }

        if (!Enum.IsDefined(effect) || !PermissionCatalog.IsDelegable(permission))
        {
            throw new IdentityValidationException("A permissão ou o efeito informado não pode ser personalizado.");
        }

        if (!PermissionCatalog.IsValidInScope(permission, organizationId))
        {
            throw new IdentityValidationException("Esta permissão só existe no escopo do tenant.");
        }

        return new UserPermissionOverride(Guid.CreateVersion7(), tenantId, userId, organizationId, permission, effect, now, createdByUserId);
    }

    public void Replace(OverrideEffect effect, DateTimeOffset now, Guid? by)
    {
        Effect = effect;
        CreatedAt = now;
        CreatedByUserId = by;
    }
}
