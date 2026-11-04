namespace IaBimeta.Conversations.Contracts;

public enum MessageDirection
{
    Inbound = 1,
    Outbound = 2,
}

public enum MessageAuthor
{
    Customer = 1,
    Ai = 2,
    Human = 3,
}

public enum MessageKind
{
    Text = 1,
    Audio = 2,
    Image = 3,
    Document = 4,
    Video = 5,
    Other = 6,
}

/// <summary>
/// Estado do conteúdo de uma mensagem do cliente (CONVERSATION-LIFECYCLE.md §3.1). Só <see cref="Ready"/> pode ser entrada
/// do modelo de IA: conteúdo pendente ou que falhou nunca chega ao prompt.
/// </summary>
public enum ContentState
{
    Ready = 1,
    Pending = 2,
    Failed = 3,
}

public enum SessionStatus
{
    Open = 1,
    Closed = 2,
}

/// <summary>
/// Como uma mensagem de entrada é associada a uma sessão (ADR 0012, seção B). Hoje só existe <see cref="Contact"/>: a sessão aberta é a do CONTATO na instância de
/// canal (chat: WhatsApp e redes semelhantes), no máximo uma por contato. O modo de associação por thread (e-mail) é de uma fatia posterior e NÃO existe aqui: um valor
/// que não seja <see cref="Contact"/> é recusado na ingestão, e nunca rebaixado para <see cref="Contact"/> (rebaixar trataria um e-mail como chat).
/// </summary>
public enum AssociationMode
{
    /// <summary>A sessão é a do contato na instância de canal; <c>ThreadKey</c> não existe.</summary>
    Contact = 1,
}

/// <summary>
/// Modo de atendimento: quem responde (BOUNDARIES.md §6). Só o Conversations o altera. É o vocabulário ÚNICO da plataforma:
/// Channels, Agents e ContactCenter usam este tipo, sem versão própria. A correspondência com os modos de produto é
/// <b>AI</b> = <see cref="AiAutonomous"/>, <b>Human</b> = <see cref="HumanAssisted"/> e <b>Hybrid</b> = <see cref="AiSupervised"/>
/// (a IA redige e um humano supervisiona), este ainda reservado. Cada configuração mora no módulo dono dela (agentes de IA no
/// Agents; filas, horários e atendentes no ContactCenter; número, idioma e unidades no Channels); o estado de cada sessão, e só
/// ele, mora aqui.
/// </summary>
public enum HandlingMode
{
    AiAutonomous = 1,

    /// <summary>Reservado: o fluxo de aprovação ainda não foi especificado, e a transição para este modo é recusada.</summary>
    AiSupervised = 2,

    /// <summary>Atendente humano. Sem responsável definido, o atendimento está pendente na fila do ContactCenter.</summary>
    HumanAssisted = 3,
}

public enum ReplyKind
{
    /// <summary>Resposta gerada pelo modelo.</summary>
    Generated = 1,

    /// <summary>Resposta determinística (template aprovado do tenant), sem modelo.</summary>
    Informative = 2,
}

/// <summary>
/// Estado de uma mensagem de saída. <see cref="Requested"/> é do Conversations; os demais refletem o envio e a entrega, que
/// são do Channels, e só avançam (nunca regridem).
/// </summary>
public enum DeliveryState
{
    Requested = 1,
    Queued = 2,
    Sending = 3,
    Sent = 4,
    Delivered = 5,
    Read = 6,
    Failed = 7,
    Expired = 8,
    Dead = 9,

    /// <summary>Resultado ambíguo do envio: nunca é reenviado automaticamente (EFX-3).</summary>
    SendUnknown = 10,
}

/// <summary>
/// De onde vem a evidência de um status de envio ou entrega (ADR 0011, seção F; ADR 0014, seção E). A origem diz o que o status prova: só a do provedor,
/// ligada à tentativa, prova envio, entrega ou falha definitiva; a declaração humana nunca prova falha (EFX-3).
/// </summary>
public enum EvidenceSource
{
    /// <summary>Status do provedor com o identificador da própria mensagem. É a origem padrão e preserva o comportamento anterior.</summary>
    ProviderStatus = 1,

    /// <summary>Consulta ao provedor pelo identificador da própria mensagem (por exemplo, a busca de uma mensagem enviada).</summary>
    ProviderLookup = 2,

    /// <summary>Rejeição definitiva do provedor ligada a esta tentativa.</summary>
    ProviderRejection = 3,

    /// <summary>
    /// Declaração humana ("conferi e a mensagem foi enviada"), com permissão, justificativa e auditoria (DP-08). Só pode CONFIRMAR o envio
    /// (<see cref="DeliveryState.Sent"/>): nunca produz falha definitiva nem entrega, e nunca provoca reenvio nem habilita duplicidade. O Conversations
    /// recusa as demais combinações com <c>DeliveryStatusOutcome.EvidenceNotAllowed</c>.
    /// </summary>
    Manual = 4,
}

public enum TurnEndReason
{
    Superseded = 1,
    Discarded = 2,
    Cancelled = 3,
    Dead = 4,
}

public enum SessionAttentionReason
{
    ChattyBurst = 1,
}

public enum HistoryOrder
{
    /// <summary>Do mais novo para o mais antigo (padrão): a página seguinte continua para trás.</summary>
    NewestFirst = 1,

    OldestFirst = 2,
}

/// <summary>
/// De onde veio a escolha do idioma efetivo de uma conversa. O idioma do tenant nunca é o do cliente por definição: quando
/// nada melhor se sabe, usa-se o do tenant e a origem diz que foi uma <b>suposição</b> (<see cref="TenantDefault"/>), que
/// qualquer informação melhor pode substituir.
/// </summary>
public enum LocaleSource
{
    /// <summary>Suposição: nada melhor era conhecido, então valeu o idioma padrão do tenant.</summary>
    TenantDefault = 1,

    /// <summary>O canal ou o provedor declarou o idioma do contato (por exemplo, o idioma do perfil).</summary>
    ChannelHint = 2,

    /// <summary>A preferência guardada do contato.</summary>
    ContactPreference = 3,

    /// <summary>O próprio cliente escolheu o idioma, relatado por um componente de sistema.</summary>
    CustomerChoice = 4,

    /// <summary>Um usuário autorizado (atendente) escolheu o idioma da conversa.</summary>
    OperatorChoice = 5,

    /// <summary>
    /// Suposição melhor que a do tenant, mas ainda suposição: nada se sabia do cliente e valeu o idioma padrão configurado para o
    /// NÚMERO (instância de canal) pelo qual ele falou. É do canal, não do cliente, e qualquer informação melhor a substitui.
    /// </summary>
    ChannelDefault = 6,
}

/// <summary>De onde veio o idioma registrado em uma mensagem.</summary>
public enum MessageLocaleOrigin
{
    /// <summary>O canal ou o provedor declarou o idioma da mensagem do cliente. Sem declaração, o idioma fica desconhecido (nulo): nunca se presume.</summary>
    ProviderDeclared = 1,

    /// <summary>Mensagem de saída escrita para o idioma efetivo da conversa no momento da resposta (intenção, não verificação do texto).</summary>
    ConversationLocale = 2,
}
