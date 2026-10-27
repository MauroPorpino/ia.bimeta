using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Domain;

/// <summary>
/// Unidade (matriz ou filial) de uma organização. A chave é <c>(tenant_id, id)</c> (TEN-2 e TEN-3) e a referência à
/// organização também carrega o tenant, então o banco recusa uma unidade ligada à organização de outro tenant.
/// </summary>
public sealed class BusinessUnit : ITenantOwned
{
    public const int NameMaxLength = 120;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private BusinessUnit(Guid id, TenantId tenantId, Guid organizationId, string name, BusinessUnitKind kind, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        OrganizationId = organizationId;
        Name = name;
        Kind = kind;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid OrganizationId { get; }

    public string Name { get; }

    public BusinessUnitKind Kind { get; }

    public DateTimeOffset CreatedAt { get; }

    public static BusinessUnit Create(TenantId tenantId, Guid organizationId, string name, BusinessUnitKind kind, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (organizationId == Guid.Empty)
        {
            throw new TenancyValidationException("A unidade precisa pertencer a uma organização.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new TenancyValidationException($"O nome da unidade deve ter de 1 a {NameMaxLength} caracteres.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new TenancyValidationException("O tipo da unidade deve ser matriz ou filial.");
        }

        return new BusinessUnit(Guid.CreateVersion7(), tenantId, organizationId, nomeValido, kind, now);
    }
}
