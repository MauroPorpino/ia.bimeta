using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Uma linha do consentimento de contato (ADR 0040, seção 4; ADR 0041, seção 4). A chave lógica é (tenant, ORGANIZAÇÃO, telefone E.164, finalidade): o consentimento é do telefone numa organização, não do contato, porque o
/// cliente que nunca escreveu também precisa de consentimento antes de um início ativo. SOMENTE ACRÉSCIMO: não há método que altere a linha e o banco recusa UPDATE e DELETE; mudar a situação insere outra linha, e o estado
/// efetivo é a ÚLTIMA linha da chave. É um registro de referência (<c>LegalRecord</c>): nenhum prazo, nenhum expurgo. Rastreável de ponta a ponta: sem evidência, finalidade, origem e data da coleta a criação é recusada.
/// </summary>
public sealed class ContactConsent : ITenantOwned
{
    public const int EvidenceMaxLength = 2000;

    /// <summary>A tolerância do relógio entre servidores para a data da coleta informada por um sistema externo: além disso, é data do futuro e é recusada.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private ContactConsent()
    {
        TenantId = null!;
        Phone = string.Empty;
        Evidence = string.Empty;
    }

    /// <summary>A ordem de gravação (identidade do banco): desempata linhas do mesmo instante, para o "último" ser sempre determinístico.</summary>
    public long Seq { get; private set; }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>O telefone em E.164 (<c>+5511999990000</c>): a única forma de identificar o titular; nunca vai à auditoria nem a log em claro.</summary>
    public string Phone { get; private set; }

    public ConsentPurpose Purpose { get; private set; }

    public ConsentStatus Status { get; private set; }

    public ConsentOrigin Origin { get; private set; }

    /// <summary>Texto ou referência que prova a coleta (de 1 a 2.000 caracteres). Para a palavra-chave do cliente: o identificador da mensagem e a palavra normalizada, nunca o texto livre.</summary>
    public string Evidence { get; private set; }

    /// <summary>Quando o consentimento (ou a revogação) foi COLETADO, segundo quem registra. Não pode estar no futuro.</summary>
    public DateTimeOffset CollectedAt { get; private set; }

    /// <summary>Quando a linha foi gravada aqui. Para uma linha <see cref="ConsentStatus.Revoked"/> é a "data da revogação" do ADR 0040.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public ActorKind ActorKind { get; private set; }

    /// <summary>A conexão (integração) ou o usuário que registrou; nulo só quando o autor é o sistema (<see cref="ConsentOrigin.CustomerKeyword"/>).</summary>
    public Guid? ActorId { get; private set; }

    public string? CustomerRef { get; private set; }

    public Guid? SessionId { get; private set; }

    public Guid? SourceMessageId { get; private set; }

    public static ContactConsent Create(
        TenantId tenantId,
        Guid organizationId,
        string? phone,
        ConsentPurpose purpose,
        ConsentStatus status,
        ConsentOrigin origin,
        string? evidence,
        DateTimeOffset collectedAt,
        ActorKind actorKind,
        Guid? actorId,
        string? customerRef,
        Guid? sessionId,
        Guid? sourceMessageId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (organizationId == Guid.Empty)
        {
            throw new ConversationsValidationException("O consentimento precisa de uma organização.");
        }

        if (!Enum.IsDefined(purpose) || !Enum.IsDefined(status) || !Enum.IsDefined(origin) || !Enum.IsDefined(actorKind))
        {
            throw new ConversationsValidationException("Finalidade, situação ou origem do consentimento inválida.");
        }

        // Rastreabilidade obrigatória: evidência (1 a 2.000), data da coleta que não esteja no futuro, origem e finalidade (já conferidas acima).
        var prova = (evidence ?? string.Empty).Trim();
        if (prova.Length == 0 || prova.Length > EvidenceMaxLength)
        {
            throw new ConversationsValidationException($"A evidência do consentimento é obrigatória e deve ter até {EvidenceMaxLength} caracteres.");
        }

        if (collectedAt == default || collectedAt > now + FutureTolerance)
        {
            throw new ConversationsValidationException("A data da coleta do consentimento é obrigatória e não pode estar no futuro.");
        }

        // A origem e o autor andam juntos: o sistema externo é uma conexão; a pessoa autorizada é um usuário; a palavra-chave do cliente é decisão do sistema e carrega a sessão e a mensagem que a provam.
        var parOk = origin switch
        {
            ConsentOrigin.IntegrationAsserted => actorKind == ActorKind.Integration && actorId is { } a1 && a1 != Guid.Empty,
            ConsentOrigin.AgentRecorded => actorKind == ActorKind.User && actorId is { } a2 && a2 != Guid.Empty,
            _ => actorKind == ActorKind.System && actorId is null && sessionId is { } s && s != Guid.Empty && sourceMessageId is { } m && m != Guid.Empty,
        };
        if (!parOk)
        {
            throw new ConversationsValidationException("A origem do consentimento não combina com o autor, a sessão e a mensagem informados.");
        }

        return new ContactConsent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            Phone = E164PhoneNumber.Normalize(phone),
            Purpose = purpose,
            Status = status,
            Origin = origin,
            Evidence = prova,
            CollectedAt = collectedAt,
            RecordedAt = now,
            ActorKind = actorKind,
            ActorId = actorId,
            CustomerRef = customerRef is null ? null : ContactExternalLink.ValidateCustomerRef(customerRef),
            SessionId = sessionId,
            SourceMessageId = sourceMessageId,
        };
    }

    /// <summary>
    /// Se repetir um registro é idempotente: mesma chave, mesma situação e mesma data da coleta que a última linha efetiva. Quem grava consulta a última linha e, se isto for verdadeiro, não insere (resposta igual, sem auditoria espúria).
    /// </summary>
    public bool IsSameAssertionAs(ConsentStatus status, DateTimeOffset collectedAt) => Status == status && CollectedAt == collectedAt;
}
