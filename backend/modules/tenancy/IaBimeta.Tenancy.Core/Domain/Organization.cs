using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Domain;

/// <summary>
/// Organização (empresa jurídica) de um tenant. A chave é <c>(tenant_id, id)</c> (TEN-2 e TEN-3): um recurso só é
/// encontrado dentro do tenant dono.
/// </summary>
public sealed class Organization : ITenantOwned
{
    public const int NameMaxLength = 120;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private Organization(Guid id, TenantId tenantId, string name, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public string Name { get; }

    public DateTimeOffset CreatedAt { get; }

    public static Organization Create(TenantId tenantId, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new TenancyValidationException($"O nome da organização deve ter de 1 a {NameMaxLength} caracteres.");
        }

        return new Organization(Guid.CreateVersion7(), tenantId, nomeValido, now);
    }
}
