using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Domain;

/// <summary>
/// Setor de uma unidade. A chave é <c>(tenant_id, id)</c> e a referência à unidade carrega o tenant, então o banco
/// recusa um setor ligado à unidade de outro tenant. A organização do setor é a da unidade (TEN-2: o escopo vem do
/// recurso carregado).
/// </summary>
public sealed class Department : ITenantOwned
{
    public const int NameMaxLength = 120;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private Department(Guid id, TenantId tenantId, Guid businessUnitId, string name, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        BusinessUnitId = businessUnitId;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid BusinessUnitId { get; }

    public string Name { get; }

    public DateTimeOffset CreatedAt { get; }

    public static Department Create(TenantId tenantId, Guid businessUnitId, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (businessUnitId == Guid.Empty)
        {
            throw new TenancyValidationException("O setor precisa pertencer a uma unidade.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new TenancyValidationException($"O nome do setor deve ter de 1 a {NameMaxLength} caracteres.");
        }

        return new Department(Guid.CreateVersion7(), tenantId, businessUnitId, nomeValido, now);
    }
}
