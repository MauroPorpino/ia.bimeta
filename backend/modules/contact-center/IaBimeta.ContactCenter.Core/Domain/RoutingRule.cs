using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

/// <summary>
/// Uma regra de roteamento: para as conversas de um canal (número), unidade e setor, a fila que recebe o atendimento humano e a
/// prioridade dele. Um critério vazio vale para qualquer valor (uma regra sem nenhum critério é a regra padrão da organização). A
/// organização é a da fila de destino e decide quem lê e quem gere a regra (TEN-2). O canal, a unidade e o setor são identificadores
/// opacos (DAD-2): a unidade só é aplicada à conversa se o Conversations a aceitar, o que inclui a autorização do número (ADR 0008).
/// A chave é <c>(tenant_id, id)</c>.
/// </summary>
public sealed class RoutingRule : ITenantOwned
{
    public const int NameMaxLength = 120;

    private RoutingRule()
    {
        TenantId = null!;
        Name = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    /// <summary>O número a que a regra se aplica, ou <c>null</c> para qualquer número.</summary>
    public Guid? ChannelInstanceId { get; private set; }

    public Guid? BusinessUnitId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    /// <summary>A fila de destino, da mesma organização da regra.</summary>
    public Guid QueueId { get; private set; }

    /// <summary>A prioridade das conversas roteadas por esta regra (0 a <see cref="ServiceQueue.MaxPriority"/>).</summary>
    public int Priority { get; private set; }

    /// <summary>A ordem de avaliação: a menor vem primeiro. No empate, vale a regra mais específica e, depois, a mais antiga.</summary>
    public int Order { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>Token de concorrência: avança a cada mudança.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Quantos critérios a regra define: quanto mais, mais específica.</summary>
    public int Specificity => (ChannelInstanceId is null ? 0 : 1) + (BusinessUnitId is null ? 0 : 1) + (DepartmentId is null ? 0 : 1);

    public static RoutingRule Create(
        TenantId tenantId, Guid organizationId, string name, Guid? channelInstanceId, Guid? businessUnitId, Guid? departmentId,
        Guid queueId, int priority, int order, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (organizationId == Guid.Empty || queueId == Guid.Empty)
        {
            throw new ContactCenterValidationException("A regra precisa de organização e de fila de destino.");
        }

        if (channelInstanceId == Guid.Empty || businessUnitId == Guid.Empty || departmentId == Guid.Empty)
        {
            throw new ContactCenterValidationException("Os critérios da regra, quando informados, não podem ser vazios.");
        }

        if (departmentId is not null && businessUnitId is null)
        {
            throw new ContactCenterValidationException("O setor da regra só existe dentro de uma unidade.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new ContactCenterValidationException($"O nome da regra deve ter de 1 a {NameMaxLength} caracteres.");
        }

        if (priority is < 0 or > ServiceQueue.MaxPriority)
        {
            throw new ContactCenterValidationException($"A prioridade deve ser de 0 a {ServiceQueue.MaxPriority}.");
        }

        return new RoutingRule
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            Name = nomeValido,
            ChannelInstanceId = channelInstanceId,
            BusinessUnitId = businessUnitId,
            DepartmentId = departmentId,
            QueueId = queueId,
            Priority = priority,
            Order = order,
            Enabled = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>A regra vale para esta conversa? Cada critério informado precisa bater; os vazios aceitam qualquer valor.</summary>
    public bool Matches(Guid organizationId, Guid channelInstanceId, Guid? businessUnitId, Guid? departmentId) =>
        Enabled
        && OrganizationId == organizationId
        && (ChannelInstanceId is null || ChannelInstanceId == channelInstanceId)
        && (BusinessUnitId is null || BusinessUnitId == businessUnitId)
        && (DepartmentId is null || DepartmentId == departmentId);

    /// <summary>Liga ou desliga a regra. Devolve <c>false</c>, sem mudar nada, se já estava assim.</summary>
    public bool SetEnabled(bool enabled, DateTimeOffset now)
    {
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        Version++;
        UpdatedAt = now;
        return true;
    }
}

public enum AgentStatus
{
    /// <summary>Recebe atendimentos: conta como atendente disponível das filas dele.</summary>
    Available = 1,

    /// <summary>Em pausa: continua nas filas, mas não conta como disponível.</summary>
    Paused = 2,

    /// <summary>Fora do atendimento. É também o estado de quem nunca informou presença.</summary>
    Offline = 3,
}

/// <summary>
/// A presença de um atendente: se ele recebe atendimentos agora. Vale para todas as filas em que ele está e é informada por ele
/// mesmo. A chave é <c>(tenant_id, user_id)</c>.
/// </summary>
public sealed class AgentPresence : ITenantOwned
{
    private AgentPresence()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public AgentStatus Status { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AgentPresence Create(TenantId tenantId, Guid userId, AgentStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        Validate(userId, status);
        return new AgentPresence { TenantId = tenantId, UserId = userId, Status = status, Version = 1, UpdatedAt = now };
    }

    /// <summary>Muda a presença. Devolve <c>false</c>, sem mudar nada, se já estava assim.</summary>
    public bool Change(AgentStatus status, DateTimeOffset now)
    {
        Validate(UserId, status);
        if (Status == status)
        {
            return false;
        }

        Status = status;
        Version++;
        UpdatedAt = now;
        return true;
    }

    private static void Validate(Guid userId, AgentStatus status)
    {
        if (userId == Guid.Empty || !Enum.IsDefined(status))
        {
            throw new ContactCenterValidationException("O atendente e a situação de presença são obrigatórios e válidos.");
        }
    }
}
