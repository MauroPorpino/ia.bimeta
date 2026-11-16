using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// O vínculo entre um usuário do ERP e um usuário interno do ia.bimeta, dentro de UMA conexão (ADR 0039, seção 6). Só dois caminhos o criam: o administrador (<see cref="IdentitySource.Mapped"/>, para o usuário
/// indicado, nunca administrativo) ou a primeira entrada de um usuário novo da conexão <see cref="ProvisioningMode.JitAttendant"/> (<see cref="IdentitySource.Jit"/>, com usuário federado recém-criado). Coincidência de
/// e-mail nunca cria nem escolhe vínculo.
/// </summary>
public sealed class ExternalIdentity : ITenantOwned
{
    public const int ExternalUserIdMaxLength = 128;

    private ExternalIdentity(Guid id, TenantId tenantId, Guid connectionId, string externalUserId, Guid userId, IdentitySource source, ExternalIdentityStatus status, DateTimeOffset createdAt, Guid? createdByUserId)
    {
        Id = id;
        TenantId = tenantId;
        ConnectionId = connectionId;
        ExternalUserId = externalUserId;
        UserId = userId;
        Source = source;
        Status = status;
        CreatedAt = createdAt;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid ConnectionId { get; }

    /// <summary>O identificador do usuário no ERP: opaco, diferencia maiúsculas de minúsculas.</summary>
    public string ExternalUserId { get; }

    public Guid UserId { get; }

    public IdentitySource Source { get; }

    public ExternalIdentityStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public Guid? CreatedByUserId { get; }

    /// <summary>Marca de revogação (logout do ERP ou emergência): sessão criada ATÉ este instante, e ticket emitido até ele, não valem mais. É o que fecha a corrida entre a troca de um ticket e a revogação.</summary>
    public DateTimeOffset? SessionsRevokedAt { get; private set; }

    public bool IsActive => Status == ExternalIdentityStatus.Active;

    /// <summary>Marca a revogação (logout do ERP): só avança no tempo.</summary>
    public void MarkSessionsRevoked(DateTimeOffset now) => SessionsRevokedAt = SessionsRevokedAt is { } atual && atual > now ? atual : now;

    public static ExternalIdentity Create(TenantId tenantId, Guid connectionId, string? externalUserId, Guid userId, IdentitySource source, DateTimeOffset now, Guid? createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (userId == Guid.Empty)
        {
            throw new IdentityValidationException("O vínculo precisa de um usuário.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new IdentityValidationException("Origem do vínculo inválida.");
        }

        return new ExternalIdentity(Guid.CreateVersion7(), tenantId, connectionId, NormalizeExternalUserId(externalUserId), userId, source, ExternalIdentityStatus.Active, now, createdByUserId);
    }

    public static string NormalizeExternalUserId(string? texto)
    {
        var valor = (texto ?? string.Empty).Trim();
        return valor.Length is 0 or > ExternalUserIdMaxLength || valor.Any(char.IsControl)
            ? throw new IdentityValidationException($"O identificador do usuário externo deve ter de 1 a {ExternalUserIdMaxLength} caracteres, sem controle.")
            : valor;
    }

    /// <summary>Devolve <c>false</c> se já estava nesta situação.</summary>
    public bool SetStatus(ExternalIdentityStatus status)
    {
        if (Status == status)
        {
            return false;
        }

        Status = status;
        return true;
    }
}
