using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

public enum QueueStatus
{
    /// <summary>A fila recebe atendimentos.</summary>
    Active = 1,

    /// <summary>A fila não recebe atendimentos novos (os atendentes continuam cadastrados).</summary>
    Paused = 2,
}

/// <summary>
/// Uma fila de atendimento de uma organização jurídica, opcionalmente restrita a uma unidade (matriz ou filial) e a um setor
/// dela. A organização é quem decide quem pode ler e gerir a fila (TEN-2). A unidade e o setor são identificadores opacos do
/// Tenancy, validados pelo caso de uso, sem chave estrangeira entre esquemas (DAD-2). O nome é único por organização, sem
/// diferenciar maiúsculas de minúsculas. A chave é <c>(tenant_id, id)</c>.
/// </summary>
public sealed class ServiceQueue : ITenantOwned
{
    public const int NameMaxLength = 120;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private ServiceQueue(
        Guid id,
        TenantId tenantId,
        Guid organizationId,
        Guid? businessUnitId,
        Guid? departmentId,
        string name,
        string nameKey,
        QueueStatus status,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        OrganizationId = organizationId;
        BusinessUnitId = businessUnitId;
        DepartmentId = departmentId;
        Name = name;
        NameKey = nameKey;
        Status = status;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid OrganizationId { get; }

    /// <summary>A unidade a que a fila se restringe, ou <c>null</c> para uma fila da organização inteira.</summary>
    public Guid? BusinessUnitId { get; }

    /// <summary>O setor (da unidade) a que a fila se restringe. Só existe com unidade.</summary>
    public Guid? DepartmentId { get; }

    public string Name { get; }

    /// <summary>O nome em minúsculas, para a unicidade sem diferenciar maiúsculas de minúsculas.</summary>
    public string NameKey { get; }

    public QueueStatus Status { get; private set; }

    /// <summary>Token de concorrência: avança a cada mudança de situação.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ServiceQueue Create(TenantId tenantId, Guid organizationId, Guid? businessUnitId, Guid? departmentId, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (organizationId == Guid.Empty)
        {
            throw new ContactCenterValidationException("A fila precisa pertencer a uma organização.");
        }

        if (businessUnitId == Guid.Empty || departmentId == Guid.Empty)
        {
            throw new ContactCenterValidationException("A unidade e o setor da fila, quando informados, não podem ser vazios.");
        }

        if (departmentId is not null && businessUnitId is null)
        {
            throw new ContactCenterValidationException("O setor da fila só existe dentro de uma unidade.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new ContactCenterValidationException($"O nome da fila deve ter de 1 a {NameMaxLength} caracteres.");
        }

        return new ServiceQueue(
            Guid.CreateVersion7(), tenantId, organizationId, businessUnitId, departmentId, nomeValido, nomeValido.ToLowerInvariant(),
            QueueStatus.Active, 1, now, now);
    }

    /// <summary>Pausa a fila. Devolve <c>false</c>, sem mudar nada, se ela já estava pausada.</summary>
    public bool Pause(DateTimeOffset now) => Change(QueueStatus.Paused, now);

    /// <summary>Retoma a fila. Devolve <c>false</c>, sem mudar nada, se ela já estava ativa.</summary>
    public bool Resume(DateTimeOffset now) => Change(QueueStatus.Active, now);

    private bool Change(QueueStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return false;
        }

        Status = status;
        Version++;
        UpdatedAt = now;
        return true;
    }
}
