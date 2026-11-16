using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Ticket de uso único emitido pelo servidor do ERP (ADR 0039). Só o hash do token é guardado. Vale poucos segundos e é consumido de forma atômica na primeira tentativa de troca, certa ou errada. O ticket de abertura
/// (<see cref="TicketKind.Launch"/>) fica preso ao <em>challenge</em> do iframe (o verifier só existe na memória dele) e à origem do pai declarada pelo ERP; o de contexto (<see cref="TicketKind.Context"/>) fica preso ao usuário e à conexão da
/// sessão embutida que o resgata.
/// </summary>
public sealed class LaunchTicket : ITenantOwned
{
    public const int TokenHashLength = 32;
    public const int ChallengeLength = 43;
    public const int CustomerRefMaxLength = 128;

    private LaunchTicket(
        Guid id, TenantId tenantId, Guid connectionId, TicketKind kind, byte[] tokenHash, string? challenge, string? parentOrigin, string externalUserId, Guid userId,
        string? customerRef, DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset? usedAt)
    {
        Id = id;
        TenantId = tenantId;
        ConnectionId = connectionId;
        Kind = kind;
        TokenHash = tokenHash;
        Challenge = challenge;
        ParentOrigin = parentOrigin;
        ExternalUserId = externalUserId;
        UserId = userId;
        CustomerRef = customerRef;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        UsedAt = usedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid ConnectionId { get; }

    public TicketKind Kind { get; }

    public byte[] TokenHash { get; }

    /// <summary>S256 do verifier (base64url, 43 caracteres). Só no ticket de abertura.</summary>
    public string? Challenge { get; }

    /// <summary>A origem do pai (o ERP) que o servidor do ERP declarou. Só no ticket de abertura.</summary>
    public string? ParentOrigin { get; }

    public string ExternalUserId { get; }

    public Guid UserId { get; }

    /// <summary>O cliente do ERP em foco, quando o ERP o informou.</summary>
    public string? CustomerRef { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? UsedAt { get; private set; }

    public static LaunchTicket CreateLaunch(
        TenantId tenantId, Guid connectionId, byte[] tokenHash, string challenge, string parentOrigin, string externalUserId, Guid userId, string? customerRef, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ValidateHash(tokenHash);
        if (challenge is not { Length: ChallengeLength })
        {
            throw new IdentityValidationException("O challenge deve ser o SHA-256 do verifier em base64url (43 caracteres).");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(parentOrigin);
        return new LaunchTicket(Guid.CreateVersion7(), tenantId, connectionId, TicketKind.Launch, tokenHash, challenge, parentOrigin, externalUserId, userId, NormalizeCustomerRef(customerRef), now, now + lifetime, null);
    }

    public static LaunchTicket CreateContext(
        TenantId tenantId, Guid connectionId, byte[] tokenHash, string externalUserId, Guid userId, string customerRef, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ValidateHash(tokenHash);
        var cliente = NormalizeCustomerRef(customerRef) ?? throw new IdentityValidationException("O ticket de contexto exige o cliente do ERP.");
        return new LaunchTicket(Guid.CreateVersion7(), tenantId, connectionId, TicketKind.Context, tokenHash, null, null, externalUserId, userId, cliente, now, now + lifetime, null);
    }

    /// <summary>O identificador do cliente do ERP: opaco, 1 a 128 caracteres, sem controle. <c>null</c> se vazio.</summary>
    public static string? NormalizeCustomerRef(string? texto)
    {
        var valor = (texto ?? string.Empty).Trim();
        if (valor.Length == 0)
        {
            return null;
        }

        return valor.Length > CustomerRefMaxLength || valor.Any(char.IsControl)
            ? throw new IdentityValidationException($"O identificador do cliente deve ter até {CustomerRefMaxLength} caracteres, sem controle.")
            : valor;
    }

    private static void ValidateHash(byte[] hash)
    {
        if (hash is not { Length: TokenHashLength })
        {
            throw new ArgumentException($"O hash do ticket deve ter {TokenHashLength} bytes.", nameof(hash));
        }
    }
}
