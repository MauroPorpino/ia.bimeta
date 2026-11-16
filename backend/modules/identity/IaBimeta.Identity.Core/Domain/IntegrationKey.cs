using System.Buffers.Text;
using System.Security.Cryptography;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Uma chave de assinatura (HMAC-SHA256) da conexão: o identificador público (<c>ibk_…</c>) vai no cabeçalho de cada chamada do servidor do ERP, e o segredo (<c>ibsk_…</c>) só existe cifrado em repouso. No máximo duas chaves
/// ativas por conexão (rotação sem parada). Uma chave revogada ou vencida nunca autentica.
/// </summary>
public sealed class IntegrationKey : ITenantOwned
{
    public const string PublicPrefix = "ibk_";
    public const string SecretPrefix = "ibsk_";
    public const int MaxActivePerConnection = 2;

    private IntegrationKey(
        Guid id, TenantId tenantId, Guid connectionId, string secretKeyId, string secretCiphertext, IntegrationKeyStatus status, DateTimeOffset createdAt,
        DateTimeOffset? expiresAt, DateTimeOffset? revokedAt, Guid? createdByUserId)
    {
        Id = id;
        TenantId = tenantId;
        ConnectionId = connectionId;
        SecretKeyId = secretKeyId;
        SecretCiphertext = secretCiphertext;
        Status = status;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid ConnectionId { get; }

    /// <summary>A chave mestra (de <c>Identity:Secrets</c>) que cifrou o segredo.</summary>
    public string SecretKeyId { get; }

    public string SecretCiphertext { get; }

    public IntegrationKeyStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Fim programado (rotação): depois dele a chave deixa de autenticar. <c>null</c> = sem fim programado.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? CreatedByUserId { get; }

    public string PublicId => PublicPrefix + Id.ToString("N");

    public static IntegrationKey Create(TenantId tenantId, Guid connectionId, Guid keyId, string secretKeyId, string secretCiphertext, DateTimeOffset now, Guid? createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretCiphertext);
        return new IntegrationKey(keyId, tenantId, connectionId, secretKeyId, secretCiphertext, IntegrationKeyStatus.Active, now, null, null, createdByUserId);
    }

    /// <summary>Gera o par (identificador, segredo em claro). O segredo aparece uma única vez, na criação.</summary>
    public static (Guid Id, string Secret) GenerateIdentifiers() =>
        (Guid.CreateVersion7(), SecretPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

    public static bool TryParsePublicId(string? texto, out Guid id)
    {
        id = Guid.Empty;
        return texto is not null && texto.Length == PublicPrefix.Length + 32 && texto.StartsWith(PublicPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(texto.AsSpan(PublicPrefix.Length), "N", out id) && id != Guid.Empty;
    }

    public bool IsUsableAt(DateTimeOffset now) => Status == IntegrationKeyStatus.Active && RevokedAt is null && (ExpiresAt is null || now < ExpiresAt);

    /// <summary>Devolve <c>false</c> se já estava revogada.</summary>
    public bool Revoke(DateTimeOffset now)
    {
        if (Status == IntegrationKeyStatus.Revoked)
        {
            return false;
        }

        Status = IntegrationKeyStatus.Revoked;
        RevokedAt = now;
        return true;
    }

    /// <summary>Programa o fim da chave (rotação). Nunca prolonga um fim já programado.</summary>
    public void ExpireAt(DateTimeOffset at)
    {
        if (ExpiresAt is null || at < ExpiresAt)
        {
            ExpiresAt = at;
        }
    }
}
