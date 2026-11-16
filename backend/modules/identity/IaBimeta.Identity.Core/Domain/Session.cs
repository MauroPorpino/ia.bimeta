using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Sessão de um usuário. Guarda só o hash (SHA-256) do token opaco, nunca o token: quem lê o banco não consegue
/// usar a sessão. Tem expiração e pode ser revogada. A sessão <see cref="SessionKind.Embedded"/> (ADR 0039) nasce da
/// troca de um ticket do servidor do ERP, é curta, nunca é estendida (renovar é trocar outro ticket) e leva a conexão,
/// o usuário externo e a origem do pai.
/// </summary>
public sealed class Session : ITenantOwned
{
    public const int TokenHashLength = 32;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private Session(
        Guid id, TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset? revokedAt,
        SessionKind kind, Guid? integrationId, string? externalUserId, string? parentOrigin)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
        Kind = kind;
        IntegrationId = integrationId;
        ExternalUserId = externalUserId;
        ParentOrigin = parentOrigin;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid UserId { get; }

    public byte[] TokenHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public SessionKind Kind { get; }

    /// <summary>A conexão de integração que abriu a sessão embutida.</summary>
    public Guid? IntegrationId { get; }

    /// <summary>O usuário no ERP, na sessão embutida.</summary>
    public string? ExternalUserId { get; }

    /// <summary>A origem do pai (o ERP) comprovada na troca do ticket.</summary>
    public string? ParentOrigin { get; }

    public static Session Create(TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        Validate(tenantId, tokenHash, lifetime);
        return new Session(Guid.CreateVersion7(), tenantId, userId, tokenHash, now, now + lifetime, null, SessionKind.Interactive, null, null, null);
    }

    public static Session CreateEmbedded(TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime, Guid integrationId, string externalUserId, string parentOrigin)
    {
        Validate(tenantId, tokenHash, lifetime);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentOrigin);
        if (integrationId == Guid.Empty)
        {
            throw new ArgumentException("A sessão embutida precisa da conexão.", nameof(integrationId));
        }

        return new Session(Guid.CreateVersion7(), tenantId, userId, tokenHash, now, now + lifetime, null, SessionKind.Embedded, integrationId, externalUserId, parentOrigin);
    }

    private static void Validate(TenantId tenantId, byte[] tokenHash, TimeSpan lifetime)
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
