namespace IaBimeta.Conversations.Contracts;

/// <summary>A finalidade do consentimento de contato (ADR 0040, seção 4; ADR 0041, seção 4). Utilidade: avisos e lembretes do atendimento. Marketing: campanhas e ofertas.</summary>
public enum ConsentPurpose
{
    Utility = 1,
    Marketing = 2,
}

/// <summary>A situação registrada numa linha de consentimento. Só se acrescenta: mudar a situação insere outra linha.</summary>
public enum ConsentStatus
{
    Granted = 1,
    Revoked = 2,
}

/// <summary>
/// De onde veio o registro. Nomes neutros: o sistema externo pode ser ERP, CRM, e-commerce ou outro (ADR 0041, premissa Standalone e Embedded). <see cref="IntegrationAsserted"/>: o servidor do sistema externo afirmou, por chamada
/// assinada. <see cref="AgentRecorded"/>: uma pessoa autorizada registrou. <see cref="CustomerKeyword"/>: o próprio cliente revogou por palavra-chave.
/// </summary>
public enum ConsentOrigin
{
    IntegrationAsserted = 1,
    AgentRecorded = 2,
    CustomerKeyword = 3,
}

/// <summary>Quem fez uma gravação: a conexão de integração (chamada assinada), um usuário do ia.bimeta, ou o sistema (decisão determinística da ingestão, sem autor humano).</summary>
public enum ActorKind
{
    Integration = 1,
    User = 2,
    System = 3,
}

/// <summary>De onde veio o vínculo entre o cliente do sistema externo e o contato: do servidor do sistema externo ou do atendente (só em sessão embutida).</summary>
public enum LinkOrigin
{
    Integration = 1,
    Agent = 2,
}

/// <summary>Uma linha do vínculo entre um cliente do sistema externo (<c>CustomerRef</c>, opaco) e um contato. Sem telefone: o vínculo localiza o contato, nunca concede visibilidade (ADR 0041, seção 2).</summary>
public sealed record ContactLinkView(
    Guid Id,
    Guid ContactId,
    Guid ConnectionId,
    Guid OrganizationId,
    string CustomerRef,
    LinkOrigin Origin,
    DateTimeOffset LinkedAt,
    DateTimeOffset? UnlinkedAt);

/// <summary>Uma linha do consentimento (imutável). O telefone aparece só para quem tem <c>ConsentManage</c>; a evidência é texto ou referência curta, nunca documento nem mensagem.</summary>
public sealed record ConsentRecordView(
    Guid Id,
    Guid OrganizationId,
    string Phone,
    ConsentPurpose Purpose,
    ConsentStatus Status,
    ConsentOrigin Origin,
    string Evidence,
    DateTimeOffset CollectedAt,
    DateTimeOffset RecordedAt,
    ActorKind ActorKind,
    Guid? ActorId,
    string? CustomerRef,
    Guid? SessionId,
    Guid? SourceMessageId);
