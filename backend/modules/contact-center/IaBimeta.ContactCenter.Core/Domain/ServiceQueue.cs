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

    // Políticas de roteamento e de horário (valores padrão: atende sempre, aceita e espera, prioridade normal, sem limite por atendente).

    /// <summary>O fuso horário (IANA) do horário de atendimento, ou <c>null</c> se a fila não tem horário e atende sempre.</summary>
    public string? TimeZoneId { get; private set; }

    public OutOfHoursBehavior OutOfHours { get; private set; } = OutOfHoursBehavior.Accept;

    public NoAgentBehavior NoAgent { get; private set; } = NoAgentBehavior.Wait;

    /// <summary>A fila de contingência (da mesma organização) para o fora do horário e a falta de atendente, quando configurados para isso.</summary>
    public Guid? FallbackQueueId { get; private set; }

    /// <summary>A prioridade das conversas que entram por aqui sem regra de roteamento que diga outra (0 a <see cref="MaxPriority"/>).</summary>
    public int DefaultPriority { get; private set; }

    /// <summary>Quantas conversas cada atendente assume ao mesmo tempo nesta fila, ou <c>null</c> para sem limite.</summary>
    public int? MaxItemsPerAgent { get; private set; }

    public const int MaxPriority = 9;
    public const int MaxItemsPerAgentLimit = 1000;

    /// <summary>
    /// Define o que fazer fora do horário e sem atendente disponível, a prioridade padrão e o limite por atendente. Devolve
    /// <c>false</c>, sem mudar nada, se já estava assim. A existência e a organização da fila de contingência são validadas pelo caso
    /// de uso.
    /// </summary>
    public bool Configure(
        Guid? fallbackQueueId, OutOfHoursBehavior outOfHours, NoAgentBehavior noAgent, int defaultPriority, int? maxItemsPerAgent, DateTimeOffset now)
    {
        if (!Enum.IsDefined(outOfHours) || !Enum.IsDefined(noAgent))
        {
            throw new ContactCenterValidationException("O comportamento fora do horário ou sem atendente é inválido.");
        }

        if (fallbackQueueId == Guid.Empty || fallbackQueueId == Id)
        {
            throw new ContactCenterValidationException("A fila de contingência não pode ser a própria fila nem vazia.");
        }

        if ((outOfHours == OutOfHoursBehavior.Fallback || noAgent == NoAgentBehavior.Fallback) && fallbackQueueId is null)
        {
            throw new ContactCenterValidationException("O comportamento de contingência exige uma fila de contingência.");
        }

        if (defaultPriority is < 0 or > MaxPriority)
        {
            throw new ContactCenterValidationException($"A prioridade deve ser de 0 a {MaxPriority}.");
        }

        if (maxItemsPerAgent is < 1 or > MaxItemsPerAgentLimit)
        {
            throw new ContactCenterValidationException($"O limite por atendente deve ser de 1 a {MaxItemsPerAgentLimit}, ou vazio para sem limite.");
        }

        if (OutOfHours == outOfHours && NoAgent == noAgent && FallbackQueueId == fallbackQueueId && DefaultPriority == defaultPriority && MaxItemsPerAgent == maxItemsPerAgent)
        {
            return false;
        }

        OutOfHours = outOfHours;
        NoAgent = noAgent;
        FallbackQueueId = fallbackQueueId;
        DefaultPriority = defaultPriority;
        MaxItemsPerAgent = maxItemsPerAgent;
        Version++;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Define (ou, com <c>null</c>, remove) o fuso horário do horário de atendimento. A versão sempre avança: as faixas e os dias
    /// fechados são trocados junto, na mesma gravação, e quem editou o horário ao mesmo tempo recebe o conflito.
    /// </summary>
    public void ReplaceSchedule(string? timeZoneId, DateTimeOffset now)
    {
        TimeZoneId = timeZoneId is null ? null : QueueSchedule.ValidateTimeZone(timeZoneId);
        Version++;
        UpdatedAt = now;
    }

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

    /// <summary>
    /// A fila pode atender uma conversa desta organização, unidade e setor? Uma fila da organização inteira atende qualquer
    /// conversa dela; uma fila de unidade só as da unidade; uma fila de setor só as do setor. Quem está na fila não ganha
    /// acesso a nada fora disto.
    /// </summary>
    public bool Covers(Guid organizationId, Guid? businessUnitId, Guid? departmentId) =>
        OrganizationId == organizationId
        && (BusinessUnitId is null || BusinessUnitId == businessUnitId)
        && (DepartmentId is null || DepartmentId == departmentId);

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
