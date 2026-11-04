using System.Security.Cryptography;
using System.Text;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Domain;

/// <summary>A situação de uma credencial guardada.</summary>
public enum StoredAiCredentialStatus
{
    /// <summary>A credencial está em uso.</summary>
    Active = 1,

    /// <summary>A credencial foi revogada: o texto cifrado foi APAGADO, e ela nunca mais é decifrada.</summary>
    Revoked = 2,
}

/// <summary>
/// A credencial BYOK GUARDADA de um tenant para um provedor, SEMPRE cifrada (ADR 0017, seção E). Há no máximo uma ativa por (tenant, provedor). O valor em claro nunca é guardado nem
/// devolvido: só o texto cifrado (que só abre sob o tenant, a credencial e o provedor certos) e uma impressão digital não reversível, para o tenant conferir QUAL chave está em uso. A
/// revogação limpa o texto cifrado. A chave é <c>(tenant_id, id)</c> (TEN-3).
/// </summary>
public sealed class StoredAiCredential : ITenantOwned
{
    private StoredAiCredential()
    {
        TenantId = null!;
        ProviderId = null!;
        Fingerprint = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string ProviderId { get; private set; }

    /// <summary>A chave mestra que cifrou o texto atual (para a rotação); nulo na credencial revogada.</summary>
    public string? KeyId { get; private set; }

    /// <summary>O envelope cifrado (<c>v1:</c> e Base64); nulo na credencial revogada. Nunca o valor em claro.</summary>
    public string? Ciphertext { get; private set; }

    /// <summary>12 caracteres hexadecimais de um SHA-256 com sal: identifica a chave sem permitir recuperá-la. NÃO são os últimos caracteres da chave.</summary>
    public string Fingerprint { get; private set; }

    public StoredAiCredentialStatus Status { get; private set; }

    /// <summary>Cresce a cada alteração (token de concorrência).</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => Status == StoredAiCredentialStatus.Active;

    public static StoredAiCredential Create(TenantId tenantId, Guid id, string providerId, string keyId, string ciphertext, string fingerprint, DateTimeOffset now, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        Require(providerId, keyId, ciphertext, fingerprint);

        return new StoredAiCredential
        {
            Id = id,
            TenantId = tenantId,
            ProviderId = providerId,
            KeyId = keyId,
            Ciphertext = ciphertext,
            Fingerprint = fingerprint,
            Status = StoredAiCredentialStatus.Active,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = updatedBy,
        };
    }

    /// <summary>Troca o valor da credencial ativa (o mesmo identificador, e por isso o mesmo AAD) por um novo cifrado.</summary>
    public void Replace(string keyId, string ciphertext, string fingerprint, DateTimeOffset now, Guid? updatedBy)
    {
        if (!IsActive)
        {
            throw new AiGatewayValidationException("Uma credencial revogada não pode ser trocada: crie uma nova.");
        }

        Require(ProviderId, keyId, ciphertext, fingerprint);
        KeyId = keyId;
        Ciphertext = ciphertext;
        Fingerprint = fingerprint;
        Version++;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    /// <summary>Recifra com a chave ativa atual, sem mudar o valor (a rotação de chaves).</summary>
    public void Reencrypt(string keyId, string ciphertext, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new AiGatewayValidationException("Uma credencial revogada não é recifrada.");
        }

        Require(ProviderId, keyId, ciphertext, Fingerprint);
        KeyId = keyId;
        Ciphertext = ciphertext;
        Version++;
        UpdatedAt = now;
    }

    /// <summary>Revoga a credencial: APAGA o texto cifrado e a chave que o cifrou. Idempotente.</summary>
    public bool Revoke(DateTimeOffset now, Guid? updatedBy)
    {
        if (!IsActive)
        {
            return false;
        }

        Status = StoredAiCredentialStatus.Revoked;
        KeyId = null;
        Ciphertext = null;
        RevokedAt = now;
        Version++;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
        return true;
    }

    /// <summary>
    /// A impressão digital: 12 hexadecimais de <c>SHA-256("ia.bimeta/ai-fp/v1|tenant|credencial|segredo")</c>. O sal (tenant e credencial) impede que o mesmo segredo, em dois tenants,
    /// gere a mesma impressão, e o SHA-256 não é reversível. Serve só para conferir qual chave está em uso.
    /// </summary>
    public static string ComputeFingerprint(TenantId tenantId, Guid credentialId, string secret)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(secret);

        var bytes = Encoding.UTF8.GetBytes($"ia.bimeta/ai-fp/v1|{tenantId.Value:N}|{credentialId:N}|{secret}");
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Require(string providerId, string keyId, string ciphertext, string fingerprint)
    {
        if (!AiModelCatalog.IsValidProviderId(providerId) || string.IsNullOrWhiteSpace(keyId) || keyId.Length > 64 || string.IsNullOrWhiteSpace(ciphertext) || ciphertext.Length > 16_384
            || string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 12)
        {
            throw new AiGatewayValidationException("Os dados da credencial são inválidos.");
        }
    }
}
