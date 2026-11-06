using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Token de redefinição de senha: de USO ÚNICO, com expiração curta, emitido por um administrador (não há serviço de e-mail). Guarda só o hash (SHA-256) do token, nunca o token: quem lê o banco não consegue
/// redefinir a senha de ninguém. O token em claro existe só na resposta da emissão. Emitir um novo token revoga os anteriores ainda não usados do mesmo usuário.
/// </summary>
public sealed class PasswordResetToken : ITenantOwned
{
    public const int TokenHashLength = 32;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private PasswordResetToken(Guid id, TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, Guid? issuedByUserId, DateTimeOffset? usedAt, DateTimeOffset? revokedAt)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        IssuedByUserId = issuedByUserId;
        UsedAt = usedAt;
        RevokedAt = revokedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    /// <summary>O usuário cuja senha o token redefine.</summary>
    public Guid UserId { get; }

    public byte[] TokenHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Quem emitiu (o administrador), ou <c>null</c> quando a emissão veio do comando do operador (contexto de sistema).</summary>
    public Guid? IssuedByUserId { get; }

    public DateTimeOffset? UsedAt { get; }

    public DateTimeOffset? RevokedAt { get; }

    public static PasswordResetToken Create(TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime, Guid? issuedByUserId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException($"O hash do token deve ter {TokenHashLength} bytes.", nameof(tokenHash));
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "O token precisa de uma duração positiva.");
        }

        return new PasswordResetToken(Guid.CreateVersion7(), tenantId, userId, tokenHash, now, now + lifetime, issuedByUserId, null, null);
    }
}
