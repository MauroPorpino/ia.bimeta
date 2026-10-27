using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Sessão de um usuário. Guarda só o hash (SHA-256) do token opaco, nunca o token: quem lê o banco não consegue
/// usar a sessão. Tem expiração e pode ser revogada.
/// </summary>
public sealed class Session : ITenantOwned
{
    public const int TokenHashLength = 32;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private Session(Guid id, TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset? revokedAt)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid UserId { get; }

    public byte[] TokenHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static Session Create(TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(tokenHash);

        if (tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException($"O hash do token deve ter {TokenHashLength} bytes.", nameof(tokenHash));
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A sessão precisa de uma duração positiva.");
        }

        return new Session(Guid.CreateVersion7(), tenantId, userId, tokenHash, now, now + lifetime, null);
    }

    /// <summary>A sessão vale enquanto não foi revogada e não expirou.</summary>
    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>Revoga a sessão. Devolve <c>false</c> se já estava revogada.</summary>
    public bool Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        return true;
    }
}
