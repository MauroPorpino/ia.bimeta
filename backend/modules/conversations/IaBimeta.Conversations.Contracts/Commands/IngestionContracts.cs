using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Contracts.Commands;

/// <summary>
/// Entrada do Channels no Conversations: mensagem do cliente, estado do conteúdo e status de entrega. Só um contexto de
/// sistema (o worker de ingestão do Channels) pode chamar; o tenant vem do <c>RequestContext</c>, nunca do comando.
/// </summary>
public interface IConversationIngestion
{
    /// <summary>
    /// Registra a mensagem do cliente de forma idempotente por (canal, <c>ProviderEventId</c>): o mesmo evento repetido
    /// devolve a mensagem já gravada, sem novo <c>InboundSeq</c> e sem novo evento.
    /// </summary>
    Task<IngestInboundMessageResult> IngestInboundMessageAsync(IngestInboundMessageCommand command, CancellationToken cancellationToken = default);

    Task<ContentChangeResult> SetMessageContentAsync(SetMessageContentCommand command, CancellationToken cancellationToken = default);

    Task<ContentChangeResult> FailMessageContentAsync(FailMessageContentCommand command, CancellationToken cancellationToken = default);

    /// <summary>Status de envio e entrega vindo do Channels, monotônico: um status mais antigo que o atual é ignorado.</summary>
    Task<DeliveryStatusResult> RecordDeliveryStatusAsync(RecordDeliveryStatusCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda a tradução de uma mensagem à parte, sem tocar no original. Quem traduz (um serviço de tradução, em etapa
    /// futura) é de sistema; não há tradução aqui. Cada mensagem tem no máximo uma tradução por idioma de destino, e uma
    /// segunda, diferente, nunca sobrescreve a primeira.
    /// </summary>
    Task<RecordTranslationResult> RecordMessageTranslationAsync(RecordMessageTranslationCommand command, CancellationToken cancellationToken = default);
}

/// <param name="ChannelInstanceId">
/// A instância de canal (um número de WhatsApp do tenant), resolvida pelo Channels pelo Phone Number ID depois de validar a
/// autenticidade do evento. Não há tenant no comando: o tenant é o do contexto confiável. O contato é identificado por
/// <c>(tenant, instância de canal, referência no provedor)</c>, nunca só pelo telefone: a mesma pessoa em dois números do
/// tenant tem dois contatos, duas sessões e duas conversas, separados.
/// </param>
/// <param name="ContactExternalRef">O contato no provedor (por exemplo, o número). Identifica o contato dentro da instância de canal.</param>
/// <param name="OrganizationId">A organização responsável, resolvida pelo Channels a partir da instância de canal e validada aqui.</param>
/// <param name="BusinessUnitId">
/// A unidade responsável, se o canal a define (um canal corporativo pode atender várias: o Channels escolhe entre as
/// autorizadas). Precisa pertencer à organização e, ao criar a sessão, estar entre as unidades autorizadas do número: a lista é
/// do Channels e é consultada na hora, com falha fechada. Uma unidade não autorizada recusa a ingestão
/// (<c>ChannelUnitNotAuthorizedException</c>) sem gravar nada, e nenhuma outra unidade é escolhida no lugar.
/// </param>
/// <param name="DepartmentId">O setor responsável, se houver. Precisa pertencer à unidade (se a unidade não vier, vale a do setor). Só vale ao criar a sessão: uma sessão já aberta, inclusive uma já transferida, não é reencaminhada por uma mensagem nova.</param>
/// <param name="ProviderEventId">O id da mensagem no provedor: a chave de idempotência.</param>
/// <param name="ProviderTimestamp">O momento da mensagem no provedor (ordena a exibição; o relógio do servidor é outro).</param>
/// <param name="Text">O texto, guardado exatamente como veio (sem cortar espaços nem normalizar). Obrigatório para <see cref="MessageKind.Text"/>; nos demais tipos o conteúdo nasce pendente.</param>
/// <param name="ContactLocaleHint">O idioma do contato, se o canal ou o provedor o declara (por exemplo, o do perfil). Só vale para escolher o idioma de uma conversa nova, quando o contato não tem preferência guardada; não é guardado como preferência.</param>
/// <param name="MessageLocale">O idioma desta mensagem, se o provedor o declara. Sem declaração, o idioma da mensagem fica desconhecido: nunca se presume o idioma do tenant nem o da conversa.</param>
/// <param name="ChannelDefaultLocale">
/// O idioma padrão configurado para o número (do Channels). Só vale para escolher o idioma de uma conversa nova quando o
/// contato não tem preferência e o provedor não declarou idioma; entra antes do padrão do tenant, como suposição
/// (<see cref="LocaleSource.ChannelDefault"/>).
/// </param>
/// <param name="InitialHandlingMode">
/// Quem responde quando a sessão NASCE: <see cref="HandlingMode.AiAutonomous"/> (AI, o padrão) ou
/// <see cref="HandlingMode.HumanAssisted"/> (Human: sem responsável, fica pendente na fila do ContactCenter). Hybrid
/// (<see cref="HandlingMode.AiSupervised"/>) é reservado e recusado. Só vale para a sessão nova: uma sessão já aberta mantém o
/// modo dela, e só <c>TransitionHandlingMode</c> o muda. Quem decide o modo (a configuração do número, do agente ou da fila) é
/// de outro módulo; aqui ele só é aplicado de forma atômica na criação, sem uma janela em que a IA responderia por engano.
/// </param>
/// <param name="ChannelData">
/// Metadados opacos e versionados do canal (por exemplo, o tipo e o identificador de uma mídia no provedor). Validado na forma antes de qualquer acesso ao
/// armazenamento e guardado com a mensagem (versão e pares, sem interpretação); um bloco inválido recusa a ingestão sem gravar nada. Numa repetição do mesmo
/// evento do provedor vale o bloco da primeira gravação. Omitido, não muda nada: os chamadores anteriores seguem válidos.
/// </param>
/// <param name="AssociationMode">
/// Como a mensagem se associa a uma sessão (ADR 0012, seção B). Hoje só existe <see cref="AssociationMode.Contact"/>, o padrão: os chamadores anteriores seguem válidos
/// e o comportamento não muda. Um valor que não seja <see cref="AssociationMode.Contact"/> (inclusive um número fora do enum) é RECUSADO antes de qualquer acesso ao
/// armazenamento: nunca é rebaixado para <see cref="AssociationMode.Contact"/>.
/// </param>
public sealed record IngestInboundMessageCommand(
    ChannelInstanceId ChannelInstanceId,
    string ContactExternalRef,
    string? ContactDisplayName,
    Guid OrganizationId,
    Guid? BusinessUnitId,
    string ProviderEventId,
    DateTimeOffset ProviderTimestamp,
    MessageKind Kind,
    string? Text,
    Locale? ContactLocaleHint = null,
    Locale? MessageLocale = null,
    Guid? DepartmentId = null,
    Locale? ChannelDefaultLocale = null,
    HandlingMode InitialHandlingMode = HandlingMode.AiAutonomous,
    ChannelData? ChannelData = null,
    AssociationMode AssociationMode = AssociationMode.Contact);

public enum IngestOutcome
{
    /// <summary>A mensagem foi gravada agora: ganhou <c>InboundSeq</c> e o evento foi publicado.</summary>
    Registered = 1,

    /// <summary>O mesmo evento do provedor já tinha sido gravado: nada novo foi gravado nem publicado.</summary>
    Duplicate = 2,
}

public sealed record IngestInboundMessageResult(
    IngestOutcome Outcome,
    Guid SessionId,
    Guid ConversationId,
    Guid MessageId,
    long InboundSeq,
    long MessageSeq,
    bool LateArrival,
    ContentState ContentState);

/// <summary>Quem processa a mídia informa o conteúdo pronto (texto extraído ou transcrição).</summary>
public sealed record SetMessageContentCommand(Guid MessageId, string Text);

/// <summary>Quem processa a mídia informa que o conteúdo falhou, ou que o prazo da política se esgotou.</summary>
public sealed record FailMessageContentCommand(Guid MessageId, string Reason);

public enum ContentChangeOutcome
{
    Changed = 1,

    /// <summary>A mensagem já estava no estado pedido: nada mudou (idempotente).</summary>
    Unchanged = 2,

    MessageNotFound = 3,

    /// <summary>A mensagem não é do cliente ou o conteúdo já tem outro estado final, e não aceita a mudança.</summary>
    NotAllowed = 4,
}

public sealed record ContentChangeResult(ContentChangeOutcome Outcome, ContentState? ContentState);

/// <param name="Evidence">
/// De onde vem a evidência deste status. O padrão é a do provedor, e o comportamento de quem não a informa não muda. A declaração humana
/// (<see cref="EvidenceSource.Manual"/>) só pode confirmar o envio (<see cref="DeliveryState.Sent"/>): qualquer outro estado com ela é recusado
/// (<see cref="DeliveryStatusOutcome.EvidenceNotAllowed"/>), e nunca produz falha definitiva.
/// </param>
public sealed record RecordDeliveryStatusCommand(Guid MessageId, DeliveryState State, string? FailureReason, EvidenceSource Evidence = EvidenceSource.ProviderStatus);

public enum DeliveryStatusOutcome
{
    Applied = 1,

    /// <summary>Status igual ou anterior ao atual: ignorado, porque o status nunca regride.</summary>
    Ignored = 2,

    MessageNotFound = 3,

    /// <summary>A mensagem não é de saída.</summary>
    NotAnOutboundMessage = 4,

    /// <summary>
    /// A origem da evidência não pode produzir este estado: a declaração humana (<see cref="EvidenceSource.Manual"/>) só confirma o envio (<c>Sent</c>) e
    /// nunca falha, expira, entrega nem lê. Nada foi lido nem gravado.
    /// </summary>
    EvidenceNotAllowed = 5,
}

public sealed record DeliveryStatusResult(DeliveryStatusOutcome Outcome, DeliveryState? CurrentState);

/// <param name="TargetLocale">O idioma da tradução.</param>
/// <param name="Text">O texto traduzido, guardado como veio.</param>
/// <param name="Provenance">Código curto de quem produziu a tradução (por exemplo, <c>human</c>): sem espaços e sem texto livre.</param>
public sealed record RecordMessageTranslationCommand(Guid MessageId, Locale TargetLocale, string Text, string Provenance);

public enum RecordTranslationOutcome
{
    /// <summary>A tradução foi gravada à parte; o original não foi tocado.</summary>
    Recorded = 1,

    /// <summary>Já havia exatamente esta tradução para este idioma: nada mudou.</summary>
    AlreadyRecorded = 2,

    /// <summary>Já há outra tradução, diferente, para este idioma. Ela NÃO é sobrescrita.</summary>
    Conflict = 3,

    MessageNotFound = 4,

    /// <summary>O conteúdo da mensagem ainda não está pronto (pendente ou com falha): não há texto para traduzir.</summary>
    ContentNotReady = 5,
}

/// <param name="TranslationId">A tradução gravada, ou a que já existia (<see cref="RecordTranslationOutcome.AlreadyRecorded"/> e <see cref="RecordTranslationOutcome.Conflict"/>).</param>
public sealed record RecordTranslationResult(RecordTranslationOutcome Outcome, Guid? TranslationId);
