using System.Text.RegularExpressions;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Domain;

public enum QueueItemStatus
{
    /// <summary>A conversa espera em uma fila, sem atendente responsável.</summary>
    Waiting = 1,

    /// <summary>Um atendente é o responsável pela conversa. Nunca há dois.</summary>
    Assigned = 2,

    /// <summary>O atendimento humano terminou: a conversa voltou para a IA ou a sessão foi encerrada.</summary>
    Closed = 3,
}

/// <summary>O que a operação pendente quer que aconteça com o item (e com o estado de atendimento no Conversations).</summary>
public enum PendingKind
{
    /// <summary>A conversa entra na fila: o Conversations passa a atendimento humano pendente (sem responsável).</summary>
    Enter = 1,

    /// <summary>Um atendente assume a conversa, ou ela passa de um atendente para outro.</summary>
    Assign = 2,

    /// <summary>O atendente devolve a conversa à fila.</summary>
    Release = 3,

    /// <summary>A conversa vai para outra fila (e, se preciso, para outra unidade), com ou sem um atendente de destino.</summary>
    TransferQueue = 4,

    /// <summary>O atendimento volta para a IA.</summary>
    ReturnToAi = 5,
}

/// <summary>
/// Intenção registrada ANTES de o Conversations ser chamado. É o que permite repetir a chamada, sem duplicar efeito, se algo
/// falhar no meio (BOUNDARIES.md §6, "Falha no meio"): o <see cref="TransitionId"/> e o <see cref="TransferId"/> são as chaves
/// de idempotência das duas chamadas. Enquanto existe, nenhuma outra operação muda o item.
/// </summary>
/// <param name="BusinessUnitId">A unidade de destino, só quando a operação inclui encaminhar a sessão para outra unidade.</param>
/// <param name="DepartmentId">O setor de destino, junto da unidade.</param>
/// <param name="ActorUserId">O usuário que pediu, ou <c>null</c> se foi um componente de sistema.</param>
/// <param name="ActorOrigin">
/// A origem do contexto de quem pediu (por exemplo, <c>session</c>), capturada quando a intenção foi gravada. Junto de
/// <paramref name="ActorUserId"/> e de <paramref name="StartedAt"/>, é a PROVENIÊNCIA da operação: quem concluir ou desfazer a intenção
/// depois (o job de reconciliação, em outro processo) audita com ela, e não com o contexto de quem retoma. Nula nas intenções anteriores
/// a este campo.
/// </param>
/// <param name="Basis">
/// O estado de atendimento no Conversations sobre o qual a operação foi AUTORIZADA (ver <see cref="HandlingBasis"/>), na entrada também. O
/// executor só altera o Conversations se esse estado ainda é o da sessão. Nula nas intenções anteriores a este campo: sem a base, a operação
/// não é executada.
/// </param>
public sealed record PendingOperation(
    PendingKind Kind,
    Guid OperationId,
    Guid TransitionId,
    Guid TransferId,
    Guid QueueId,
    Guid? UserId,
    Guid? BusinessUnitId,
    Guid? DepartmentId,
    Guid? ActorUserId,
    string? ActorOrigin,
    string Reason,
    DateTimeOffset StartedAt,
    HandlingBasis? Basis = null);

/// <summary>
/// O estado de atendimento da sessão (no Conversations, a autoridade sobre o responsável atual) sobre o qual uma operação foi autorizada: o
/// modo (atendimento humano, ou a IA), o período de atendimento humano e o responsável (nulo = a conversa esperava, sem atendente, ou estava
/// com a IA). Nasce com a intenção, na MESMA gravação, e é enviada ao Conversations como pré-condição de cada comando
/// (<c>HandlingPrecondition</c>), conferida dentro da trava da sessão: a autorização dada sobre este estado não vale para alterar outro,
/// mesmo que o executor releia a sessão e renove a versão esperada. A ENTRADA na fila parte da conversa com a IA (ou esperando, sem atendente)
/// e leva a base dela, para nunca retirar um atendente humano que apareceu depois de autorizada.
/// </summary>
/// <param name="HandlingPeriod">A identidade do período de atendimento humano (<c>SessionView.HandlingPeriod</c>) na autorização. Zero só na sessão em IA que nunca teve atendimento humano.</param>
/// <param name="ResponderUserId">O responsável da sessão na autorização; nulo se a conversa esperava na fila ou estava com a IA.</param>
/// <param name="InAiMode">A sessão estava com a IA (e não em atendimento humano) na autorização. Nunca tem responsável.</param>
/// <param name="PlacementEpoch">
/// A geração da colocação da sessão (<c>SessionView.PlacementEpoch</c>) na autorização, ou <c>null</c> se não é conhecida (intenção anterior ao campo). O executor a leva como
/// pré-condição, e o item a prende ao concluir: o item passa a valer só para a colocação (unidade e setor) que ele de fato tem, e a geração dela é a da autorização, mais
/// UMA se a própria operação muda a unidade ou o setor (a pré-condição atômica garante que foi ela, e não outra mudança, quem a avançou).
/// </param>
public sealed record HandlingBasis(long HandlingPeriod, Guid? ResponderUserId, bool InAiMode = false, long? PlacementEpoch = null);

/// <summary>
/// Uma conversa sob a responsabilidade do ContactCenter: a espera em uma fila, o atendente responsável e a operação em andamento.
/// Há no máximo UM item ativo (não encerrado) por sessão, garantido pelo banco, e no máximo um atendente responsável, porque o
/// responsável é uma coluna só. O estado de atendimento da conversa (IA ou humano, e o responsável) continua sendo do
/// Conversations: aqui fica a decisão do ContactCenter e a intenção de aplicá-la. A chave é <c>(tenant_id, id)</c>.
/// </summary>
public sealed class QueueItem : ITenantOwned
{
    public const int ReasonMaxLength = 100;
    public const int ErrorMaxLength = 120;

    /// <summary>Motivos são códigos curtos, sem espaços: nada de texto livre que possa carregar segredo (mesmo formato do Conversations).</summary>
    private static readonly Regex ReasonPattern = new("^[a-z0-9][a-z0-9_.-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Usado pela fábrica e pelo EF Core.
    private QueueItem()
    {
        TenantId = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    /// <summary>A organização da sessão, que é também a da fila (uma fila só recebe sessões da organização dela).</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>A unidade da sessão. Acompanha a sessão: muda só quando a operação encaminha a sessão para outra unidade.</summary>
    public Guid? BusinessUnitId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    public Guid QueueId { get; private set; }

    public QueueItemStatus Status { get; private set; }

    /// <summary>O atendente responsável. Só existe no estado <see cref="QueueItemStatus.Assigned"/>.</summary>
    public Guid? AssignedUserId { get; private set; }

    /// <summary>Token de concorrência: avança a cada mudança. Dois atendentes que assumem juntos disputam esta versão.</summary>
    public long Version { get; private set; }

    public DateTimeOffset EnteredAt { get; private set; }

    public DateTimeOffset? AssignedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>
    /// A sequência (<c>MessageSeq</c>) da última mensagem da conversa NO INSTANTE em que o atendimento humano terminou, dada pelo próprio
    /// Conversations (a transição que o encerrou, ou o encerramento da sessão): é a fronteira estável do que a fila deste item pôde ver. Não
    /// depende de relógio nem de quando o ContactCenter processou o fim. Nula enquanto o item está ativo e, no item encerrado, quando a
    /// fronteira não é conhecida: então o histórico do período fica fechado (nenhuma mensagem), nunca aberto.
    /// </summary>
    public long? ClosedThroughMessageSeq { get; private set; }

    /// <summary>
    /// A identidade do período de atendimento humano (<c>SessionView.HandlingPeriod</c>) a que o acesso deste item à conversa está preso:
    /// dada pelo Conversations quando a entrada na fila foi concluída. O item só autoriza a leitura enquanto a sessão estiver NESTE período
    /// (e, na mesma unidade e setor): humano → IA → humano é outro período, ainda que o modo seja de novo o humano, e fecha o acesso. Nula
    /// enquanto a entrada não terminou, e nos itens anteriores a este campo: período desconhecido fecha o acesso, nunca o abre.
    /// </summary>
    public long? HandlingPeriod { get; private set; }

    /// <summary>
    /// A identidade DURÁVEL da colocação (<c>SessionView.PlacementEpoch</c>) a que o acesso deste item à conversa está preso: a geração monotônica da
    /// unidade e do setor da sessão, que o Conversations avança, atomicamente, a cada mudança de colocação. O item só autoriza a leitura enquanto a sessão
    /// estiver NESTA geração: A → B → A é outra geração mesmo que a unidade, o setor e o período de atendimento sejam de novo os do item, e nada disso
    /// depende de o ContactCenter ter recebido o evento do encaminhamento. Vem da autorização da entrada ou da transferência do próprio ContactCenter
    /// (a geração lida mais uma, se a operação muda a colocação), nunca de uma leitura posterior. Nula nos itens anteriores ao campo e nas intenções sem a
    /// geração: desconhecida FECHA o acesso, nunca o abre.
    /// </summary>
    public long? PlacementEpoch { get; private set; }

    public string? CloseReason { get; private set; }

    /// <summary>
    /// A prioridade do atendimento (0 a <see cref="ServiceQueue.MaxPriority"/>; maior é mais urgente). Vem do roteamento (a regra ou o
    /// padrão da fila) e só um supervisor a muda. A fila é lida da mais urgente para a menos, e dentro da mesma prioridade, da mais antiga.
    /// </summary>
    public int Priority { get; private set; }

    /// <summary>A regra de roteamento que escolheu a fila, ou <c>null</c> se a fila foi pedida explicitamente.</summary>
    public Guid? RoutingRuleId { get; private set; }

    /// <summary>
    /// A maior <c>StateVersion</c> da sessão que o item já refletiu (por uma operação concluída ou por um evento do Conversations).
    /// Evento com versão igual ou menor é entrega repetida ou atrasada e não muda nada: a projeção só avança.
    /// </summary>
    public long ObservedStateVersion { get; private set; }

    /// <summary>
    /// A OBRIGAÇÃO de ressincronização: a maior <c>StateVersion</c> de um evento EXTERNO que o item ainda não aplicou (chegou com uma operação do item
    /// pendente, ou com outra obrigação aberta, e não pôde ser projetado ainda, ou não tinha como provar a identidade que o item representa). Nula
    /// quando nada ficou por projetar. É um fato DURÁVEL, independente de <see cref="ObservedStateVersion"/>: avançar a versão observada NÃO a liquida.
    /// Só uma liquidação explícita a zera (<see cref="MirrorSession"/>, <see cref="SettleUnverified"/>, <see cref="CompletePending"/> com cobertura
    /// comprovada, ou o encerramento do item). Enquanto aberta, a projeção normal de eventos posteriores ADIA (sobe a marca) em vez de espelhar, e o item
    /// precisa se alinhar à sessão quando não há operação pendente (<see cref="NeedsResync"/>). Nunca decresce enquanto existe.
    /// </summary>
    public long? ResyncStateVersion { get; private set; }

    /// <summary>
    /// O limite do que o atendimento deste item pode expor da conversa, depois que a sessão SAIU do escopo dele (foi encaminhada para outra
    /// unidade ou setor, por fora): a sequência da última mensagem naquele instante. Nulo enquanto a sessão não saiu (ou não se soube). Zero
    /// quando saiu sem fronteira conhecida: nenhuma mensagem. Só diminui: o que veio depois da saída é de quem a conversa passou a pertencer, e
    /// a volta da sessão ao escopo não o reabre. Só AUTORIZA conteúdo quando tem origem comprovada (<see cref="HistoryLimitIsValid"/>); sem ela o
    /// número é preservado como metadado.
    /// </summary>
    public long? HistoryLimitSeq { get; private set; }

    /// <summary>
    /// A geração da colocação (<see cref="PlacementEpoch"/>) DA QUAL a sessão saiu quando o <see cref="HistoryLimitSeq"/> foi gravado: a origem do limite,
    /// validada contra o vínculo do item. Só existe junto de um limite certificado, e é limpa com ele quando o item muda de geração por uma operação própria.
    /// </summary>
    public long? HistoryLimitOriginEpoch { get; private set; }

    /// <summary>
    /// O modelo das fronteiras de histórico do item (<see cref="QueueItemBoundaryModel"/>): 0 (legado, o padrão) = as fronteiras positivas que ele guarda não
    /// têm prova de origem e não autorizam conteúdo; 1 = TODAS as fronteiras positivas presentes foram certificadas pelo caminho que as gravou. Registro
    /// existente nunca é promovido automaticamente.
    /// </summary>
    public short BoundaryModel { get; private set; }

    /// <summary>O limite de histórico tem origem comprovada: certificado e da geração a que o item está preso (<see cref="ScopeProof.StoredLimit"/>)?</summary>
    public bool HistoryLimitIsValid => ScopeProof.StoredLimit(HistoryLimitSeq, HistoryLimitOriginEpoch, BoundaryModel, PlacementEpoch) == BoundaryOrigin.Valid;

    /// <summary>A fronteira do item ENCERRADO foi certificada pelo caminho que o encerrou (período e geração comprovados no fechamento, e o vínculo não muda depois)?</summary>
    public bool ClosedBoundaryIsCertified => IsClosed && ClosedThroughMessageSeq is not null && BoundaryModel == QueueItemBoundaryModel.Certified;

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// O token de cerca (<c>fencing token</c>) do executor que assumiu a operação pendente por último. É dado na própria linha do item, por um
    /// <c>UPDATE</c> que só vale se a operação ainda é a pendente (a emissão e a revalidação da intenção são UM passo, sob a trava da
    /// linha) e que avança a <see cref="Version"/>: quem assumiu antes tem a versão velha e não grava mais no item. O mesmo token vai para
    /// o Conversations, que recusa o menor. Quem perdeu a intenção (cancelada ou substituída) nunca recebe token.
    /// </summary>
    public long? ExecutorFenceToken { get; private set; }

    // A operação pendente, uma coluna por campo (todas nulas quando não há).
    public PendingKind? PendingKindValue { get; private set; }

    public Guid? PendingOperationId { get; private set; }

    public Guid? PendingTransitionId { get; private set; }

    public Guid? PendingTransferId { get; private set; }

    public Guid? PendingQueueId { get; private set; }

    public Guid? PendingUserId { get; private set; }

    public Guid? PendingBusinessUnitId { get; private set; }

    public Guid? PendingDepartmentId { get; private set; }

    public Guid? PendingActorUserId { get; private set; }

    /// <summary>A origem do contexto de quem pediu a operação pendente (ver <see cref="PendingOperation.ActorOrigin"/>).</summary>
    public string? PendingActorOrigin { get; private set; }

    /// <summary>O período de atendimento sobre o qual a operação pendente foi autorizada (ver <see cref="HandlingBasis"/>). Nulo = sem base.</summary>
    public long? PendingBasisPeriod { get; private set; }

    /// <summary>O responsável da sessão sobre o qual a operação pendente foi autorizada. Só existe com <see cref="PendingBasisPeriod"/>; nulo = esperava na fila, ou estava com a IA.</summary>
    public Guid? PendingBasisResponder { get; private set; }

    /// <summary>A sessão estava com a IA (e não em atendimento humano) quando a operação pendente foi autorizada (ver <see cref="HandlingBasis.InAiMode"/>). Só a entrada parte daí.</summary>
    public bool PendingBasisAiMode { get; private set; }

    /// <summary>A geração da colocação da sessão sobre a qual a operação pendente foi autorizada (ver <see cref="HandlingBasis.PlacementEpoch"/>). Nula = desconhecida.</summary>
    public long? PendingBasisPlacementEpoch { get; private set; }

    public string? PendingReason { get; private set; }

    public DateTimeOffset? PendingStartedAt { get; private set; }

    /// <summary>Quantas vezes a operação pendente foi tentada no Conversations sem concluir.</summary>
    public int PendingAttempts { get; private set; }

    /// <summary>O tipo da falha da última tentativa (só o nome do tipo, nunca a mensagem), para diagnóstico.</summary>
    public string? PendingLastError { get; private set; }

    /// <summary>
    /// Quando o job de reconciliação pode tentar a operação pendente de novo. Cada falha adia a próxima tentativa (recuo exponencial),
    /// para que a operação que não conclui não ocupe o lote do job para sempre e as outras pendências sejam atendidas. Nula enquanto
    /// não houve falha: a operação está elegível desde que começou.
    /// </summary>
    public DateTimeOffset? PendingNextAttemptAt { get; private set; }

    /// <summary>O primeiro recuo depois de uma falha, e o teto dele.</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>O recuo depois da falha de número <paramref name="attempts"/> (1, 2, 3...): dobra a cada falha até o teto.</summary>
    public static TimeSpan BackoffFor(int attempts) =>
        TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, BaseBackoff.Ticks << Math.Clamp(attempts - 1, 0, 20)));

    public bool HasPending => PendingKindValue is not null;

    public bool IsClosed => Status == QueueItemStatus.Closed;

    /// <summary>A operação pendente, ou <c>null</c>.</summary>
    public PendingOperation? Pending => PendingKindValue is null
        ? null
        : new PendingOperation(
            PendingKindValue.Value, PendingOperationId!.Value, PendingTransitionId!.Value, PendingTransferId!.Value, PendingQueueId!.Value,
            PendingUserId, PendingBusinessUnitId, PendingDepartmentId, PendingActorUserId, PendingActorOrigin, PendingReason!, PendingStartedAt!.Value,
            PendingBasisPeriod is { } periodo ? new HandlingBasis(periodo, PendingBasisResponder, PendingBasisAiMode, PendingBasisPlacementEpoch) : null);

    public static string ValidateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonMaxLength || !ReasonPattern.IsMatch(reason))
        {
            throw new ContactCenterValidationException($"O motivo deve ter de 1 a {ReasonMaxLength} caracteres: letras minúsculas, números e '_.-'.");
        }

        return reason;
    }

    /// <summary>
    /// A conversa entra na fila. O item nasce em espera, com a entrada pendente: o Conversations ainda precisa passar a conversa
    /// para atendimento humano pendente, e só então a entrada se conclui (<see cref="CompletePending"/>).
    /// </summary>
    /// <param name="priority">A prioridade do atendimento (0 a <see cref="ServiceQueue.MaxPriority"/>), decidida pelo roteamento, nunca por quem pede.</param>
    /// <param name="routingRuleId">A regra de roteamento que escolheu a fila, para rastreio, ou <c>null</c> se a fila foi pedida explicitamente.</param>
    /// <param name="targetBusinessUnitId">A unidade para onde a sessão vai junto com a entrada (fila de unidade escolhida pelo roteamento), ou <c>null</c> se ela fica onde está.</param>
    /// <param name="basis">
    /// O estado de atendimento da sessão no Conversations sobre o qual a entrada foi autorizada (a IA, ou a espera sem atendente), guardado com a
    /// intenção. O executor o leva como pré-condição: a entrada não retira um atendente humano que surgiu depois. Sem ela, a entrada não é executada.
    /// </param>
    public static QueueItem Enter(
        TenantId tenantId, Guid sessionId, Guid organizationId, Guid? businessUnitId, Guid? departmentId, Guid queueId,
        Guid operationId, Guid? actorUserId, string reason, DateTimeOffset now,
        int priority = 0, Guid? routingRuleId = null, Guid? targetBusinessUnitId = null, Guid? targetDepartmentId = null, string? actorOrigin = null,
        HandlingBasis? basis = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (priority is < 0 or > ServiceQueue.MaxPriority)
        {
            throw new ContactCenterValidationException($"A prioridade deve ser de 0 a {ServiceQueue.MaxPriority}.");
        }

        if (routingRuleId == Guid.Empty || targetBusinessUnitId == Guid.Empty || targetDepartmentId == Guid.Empty || (targetDepartmentId is not null && targetBusinessUnitId is null))
        {
            throw new ContactCenterValidationException("A regra e a unidade de destino, quando informadas, não podem ser vazias, e o setor de destino só existe dentro de uma unidade.");
        }

        if (sessionId == Guid.Empty || organizationId == Guid.Empty || queueId == Guid.Empty || operationId == Guid.Empty)
        {
            throw new ContactCenterValidationException("A sessão, a organização, a fila e a operação são obrigatórias.");
        }

        if (businessUnitId == Guid.Empty || departmentId == Guid.Empty || (departmentId is not null && businessUnitId is null))
        {
            throw new ContactCenterValidationException("A unidade e o setor da sessão, quando informados, não podem ser vazios, e o setor só existe dentro de uma unidade.");
        }

        var item = new QueueItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SessionId = sessionId,
            OrganizationId = organizationId,
            BusinessUnitId = businessUnitId,
            DepartmentId = departmentId,
            QueueId = queueId,
            Status = QueueItemStatus.Waiting,
            Priority = priority,
            RoutingRuleId = routingRuleId,
            Version = 0, // o registro da entrada pendente avança para 1
            EnteredAt = now,
            UpdatedAt = now,
        };
        item.SetPending(PendingKind.Enter, operationId, queueId, null, targetBusinessUnitId, targetDepartmentId, actorUserId, actorOrigin, ValidateReason(reason), now, basis);
        return item;
    }

    /// <summary>
    /// Muda a prioridade do atendimento (quem supervisiona a fila decide). Vale mesmo com operação pendente: não depende do
    /// Conversations. Devolve o registro do histórico, ou <c>null</c> se a prioridade já era esta.
    /// </summary>
    public QueueItemHistory? ChangePriority(int priority, Guid operationId, Guid? actorUserId, string reason, DateTimeOffset now)
    {
        if (IsClosed)
        {
            throw new QueueItemStateException("A conversa não está mais em atendimento na fila.");
        }

        if (priority is < 0 or > ServiceQueue.MaxPriority)
        {
            throw new ContactCenterValidationException($"A prioridade deve ser de 0 a {ServiceQueue.MaxPriority}.");
        }

        RequireId(operationId);
        var motivo = ValidateReason(reason);
        if (Priority == priority)
        {
            return null;
        }

        var anterior = Priority;
        Priority = priority;
        Version++;
        UpdatedAt = now;
        return QueueItemHistory.Create(
            TenantId, Id, SessionId, operationId, HistoryKind.PriorityChanged, QueueId, QueueId, AssignedUserId, AssignedUserId, actorUserId, motivo, null, now, anterior, priority);
    }

    /// <summary>
    /// Um atendente assume a conversa (item em espera) ou a conversa passa de um atendente para outro (item atribuído).
    /// </summary>
    /// <param name="basis">
    /// O estado de atendimento no Conversations sobre o qual a operação foi autorizada (o responsável efetivo e o período), ou <c>null</c> se não
    /// havia sessão em atendimento a considerar (sessão encerrada). Vale para todas as operações abaixo.
    /// </param>
    public PendingOperation BeginAssign(
        Guid userId, Guid operationId, Guid? actorUserId, string reason, DateTimeOffset now, string? actorOrigin = null, HandlingBasis? basis = null)
    {
        RequireIdle();
        RequireId(userId, operationId);

        if (Status == QueueItemStatus.Assigned && AssignedUserId == userId)
        {
            throw new QueueItemStateException("A conversa já está com este atendente.");
        }

        SetPending(PendingKind.Assign, operationId, QueueId, userId, null, null, actorUserId, actorOrigin, ValidateReason(reason), now, basis);
        return Pending!;
    }

    /// <summary>O atendente devolve a conversa à fila, sem responsável.</summary>
    public PendingOperation BeginRelease(
        Guid operationId, Guid? actorUserId, string reason, DateTimeOffset now, string? actorOrigin = null, HandlingBasis? basis = null)
    {
        RequireIdle();
        RequireId(operationId);

        if (Status != QueueItemStatus.Assigned)
        {
            throw new QueueItemStateException("A conversa não está com um atendente.");
        }

        SetPending(PendingKind.Release, operationId, QueueId, null, null, null, actorUserId, actorOrigin, ValidateReason(reason), now, basis);
        return Pending!;
    }

    /// <summary>
    /// A conversa vai para outra fila, com ou sem atendente de destino. Se a fila de destino é de outra unidade, a operação
    /// inclui encaminhar a sessão para essa unidade (<paramref name="targetBusinessUnitId"/>).
    /// </summary>
    public PendingOperation BeginTransferToQueue(
        Guid targetQueueId, Guid? targetUserId, Guid? targetBusinessUnitId, Guid? targetDepartmentId, Guid operationId, Guid? actorUserId, string reason, DateTimeOffset now,
        string? actorOrigin = null, HandlingBasis? basis = null)
    {
        RequireIdle();
        RequireId(targetQueueId, operationId);

        if (targetUserId == Guid.Empty || targetBusinessUnitId == Guid.Empty || targetDepartmentId == Guid.Empty || (targetDepartmentId is not null && targetBusinessUnitId is null))
        {
            throw new ContactCenterValidationException("O atendente, a unidade e o setor de destino, quando informados, não podem ser vazios, e o setor só existe dentro de uma unidade.");
        }

        if (targetQueueId == QueueId)
        {
            throw new QueueItemStateException("A conversa já está nesta fila.");
        }

        SetPending(PendingKind.TransferQueue, operationId, targetQueueId, targetUserId, targetBusinessUnitId, targetDepartmentId, actorUserId, actorOrigin, ValidateReason(reason), now, basis);
        return Pending!;
    }

    /// <summary>O atendimento volta para a IA. Concluída, a operação encerra o item.</summary>
    public PendingOperation BeginReturnToAi(
        Guid operationId, Guid? actorUserId, string reason, DateTimeOffset now, string? actorOrigin = null, HandlingBasis? basis = null)
    {
        RequireIdle();
        RequireId(operationId);

        SetPending(PendingKind.ReturnToAi, operationId, QueueId, null, null, null, actorUserId, actorOrigin, ValidateReason(reason), now, basis);
        return Pending!;
    }

    /// <summary>
    /// O sufixo do motivo do histórico quando a transferência concluiu SEM o atendente de destino (ele saiu da fila de destino depois de a
    /// unidade já ter sido aplicada): o rastro diz que a conversa chegou à fila, mas sem responsável.
    /// </summary>
    public const string TargetUnavailableSuffix = ".target-unavailable";

    /// <summary>
    /// Aplica o resultado da operação pendente (o Conversations já fez a parte dele): atualiza o item, limpa a intenção e devolve o
    /// registro do histórico. A versão avança.
    /// </summary>
    /// <param name="messageSeq">Para a devolução à IA: a fronteira (sequência da última mensagem) dada pela transição no Conversations.</param>
    /// <param name="withoutTarget">
    /// Só na transferência: concluir SEM o atendente de destino. A conversa vai para a fila de destino, em espera, sem responsável (o que o
    /// executor deixou também no Conversations), e o histórico registra o motivo com <see cref="TargetUnavailableSuffix"/>.
    /// </param>
    /// <param name="handlingPeriod">
    /// Só na entrada: a identidade do período de atendimento humano que o Conversations deu (a da transição que a concluiu, ou a da sessão que
    /// já estava em atendimento humano). O item prende a este período o acesso dele à conversa. Nula = desconhecida, e o acesso fica fechado.
    /// </param>
    /// <param name="certifiedBoundary">
    /// Só na devolução à IA: <paramref name="messageSeq"/> é uma fronteira COMPROVADA (o efeito foi aplicado, com o recibo da própria operação, no período e na
    /// geração a que o item está preso). Sem isto o item é encerrado com a fronteira desconhecida (histórico fechado).
    /// </param>
    /// <param name="coversResyncThrough">
    /// A versão até a qual o efeito comprovado desta operação COBRE a obrigação de ressincronização aberta (ver <see cref="ResyncStateVersion"/>): só quem
    /// tem a prova correlacionada do efeito (o recibo da própria operação, aplicada com a base completa, com as identidades relevantes conferidas) a informa.
    /// Igualdade do estado corrente, recibo sem alteração, ou versão maior sem essa prova NÃO liquidam a obrigação.
    /// </param>
    public QueueItemHistory CompletePending(
        long? stateVersionAfter, DateTimeOffset now, long? messageSeq = null, bool withoutTarget = false, long? handlingPeriod = null, long? placementEpoch = null,
        bool certifiedBoundary = false, long? coversResyncThrough = null)
    {
        var pendente = Pending ?? throw new QueueItemStateException("Não há operação pendente.");
        if (withoutTarget)
        {
            if (pendente.Kind != PendingKind.TransferQueue)
            {
                throw new QueueItemStateException("Só a transferência conclui sem o atendente de destino.");
            }

            pendente = pendente with { UserId = null };
        }

        var deFila = QueueId;
        var deUsuario = AssignedUserId;
        HistoryKind tipo;

        switch (pendente.Kind)
        {
            case PendingKind.Enter:
                tipo = HistoryKind.Entered;
                HandlingPeriod = handlingPeriod; // o acesso do item à conversa fica preso ao período em que a entrada aconteceu
                PlacementEpoch = placementEpoch; // e à geração da colocação em que ela aconteceu (a da autorização, mais uma se a entrada levou a unidade)
                if (pendente.BusinessUnitId is { } unidadeDeEntrada)
                {
                    BusinessUnitId = unidadeDeEntrada;
                    DepartmentId = pendente.DepartmentId;
                    ClearHistoryLimit(); // outra geração: o limite da anterior não vale para o vínculo novo
                }

                break;

            case PendingKind.Assign:
                tipo = deUsuario is null ? HistoryKind.Assigned : HistoryKind.Reassigned;
                Status = QueueItemStatus.Assigned;
                AssignedUserId = pendente.UserId;
                AssignedAt = now;
                break;

            case PendingKind.Release:
                tipo = HistoryKind.Released;
                Status = QueueItemStatus.Waiting;
                AssignedUserId = null;
                AssignedAt = null;
                break;

            case PendingKind.TransferQueue:
                tipo = HistoryKind.TransferredQueue;
                QueueId = pendente.QueueId;
                if (pendente.BusinessUnitId is { } unidade)
                {
                    BusinessUnitId = unidade;
                    DepartmentId = pendente.DepartmentId;
                    PlacementEpoch = placementEpoch; // a colocação mudou: o item passa à geração dela (desconhecida, se a autorização não a trouxe)
                    ClearHistoryLimit(); // outra geração: o limite (e a origem) da anterior não valem para o vínculo novo, na mesma gravação
                }

                Status = pendente.UserId is null ? QueueItemStatus.Waiting : QueueItemStatus.Assigned;
                AssignedUserId = pendente.UserId;
                AssignedAt = pendente.UserId is null ? null : now;
                break;

            case PendingKind.ReturnToAi:
                tipo = HistoryKind.ReturnedToAi;
                Status = QueueItemStatus.Closed;
                AssignedUserId = null;
                AssignedAt = null;
                ClosedAt = now;
                ClosedThroughMessageSeq = BoundaryFor(messageSeq, certifiedBoundary);
                CloseReason = "returned-to-ai";
                ResyncStateVersion = null; // o item encerrado sai do escopo da ressincronização
                break;

            default:
                throw new InvalidOperationException("Tipo de operação pendente desconhecido.");
        }

        ClearPending();
        if (stateVersionAfter is { } vista && vista > ObservedStateVersion)
        {
            ObservedStateVersion = vista;
        }

        // Liquidação EXPLÍCITA da obrigação: só a cobertura comprovada do efeito a zera. Avançar a versão observada acima não a liquida.
        if (coversResyncThrough is { } cobre && ResyncStateVersion is { } marca && cobre >= marca)
        {
            ResyncStateVersion = null;
        }

        Version++;
        UpdatedAt = now;

        var motivo = withoutTarget ? SuffixedReason(pendente.Reason, TargetUnavailableSuffix) : pendente.Reason;
        return QueueItemHistory.Create(
            TenantId, Id, SessionId, pendente.OperationId, tipo, tipo == HistoryKind.Entered ? null : deFila, QueueId, deUsuario, AssignedUserId,
            pendente.ActorUserId, motivo, stateVersionAfter, now);
    }

    // O motivo continua dentro do limite e do formato dos motivos (o sufixo nunca é cortado: o que sobra do motivo original encolhe).
    private static string SuffixedReason(string reason, string suffix) =>
        (reason.Length + suffix.Length > ReasonMaxLength ? reason[..(ReasonMaxLength - suffix.Length)] : reason) + suffix;

    /// <summary>
    /// Reflete uma mudança de atendimento que NÃO foi feita por este módulo (alguém mudou o responsável direto no Conversations): o
    /// item passa a espelhar o responsável da sessão. Só vale para item ativo e sem operação pendente (a operação pendente
    /// converge sozinha, relendo a sessão). Devolve o registro do histórico (autor nulo, motivo <c>external-change</c>), ou
    /// <c>null</c> se o item já estava assim.
    /// </summary>
    public QueueItemHistory? ApplyExternalHandling(Guid? responderUserId, Guid operationId, long stateVersion, DateTimeOffset now)
    {
        RequireIdle();
        RequireId(operationId);

        var deUsuario = AssignedUserId;
        var igual = responderUserId == deUsuario && (responderUserId is null) == (Status == QueueItemStatus.Waiting);
        ObservedStateVersion = Math.Max(ObservedStateVersion, stateVersion);
        Version++;
        UpdatedAt = now;
        if (igual)
        {
            return null;
        }

        HistoryKind tipo;
        if (responderUserId is null)
        {
            tipo = HistoryKind.Released;
            Status = QueueItemStatus.Waiting;
            AssignedUserId = null;
            AssignedAt = null;
        }
        else
        {
            tipo = deUsuario is null ? HistoryKind.Assigned : HistoryKind.Reassigned;
            Status = QueueItemStatus.Assigned;
            AssignedUserId = responderUserId;
            AssignedAt = now;
        }

        return QueueItemHistory.Create(TenantId, Id, SessionId, operationId, tipo, QueueId, QueueId, deUsuario, AssignedUserId, null, ExternalChangeReason, stateVersion, now);
    }

    /// <summary>
    /// O atendimento voltou para a IA por uma mudança que NÃO foi feita por este módulo: o item é encerrado, com o rastro (autor
    /// nulo, motivo <c>external-change</c>). Devolve <c>null</c> se o item já estava encerrado.
    /// </summary>
    /// <param name="messageSeq">A fronteira dada pela transição no Conversations (a sequência da última mensagem naquele instante).</param>
    /// <param name="certifiedBoundary">
    /// <paramref name="messageSeq"/> é a fronteira do atendimento que ESTE item representa, comprovada (o período e a geração do evento são os do item). Sem a
    /// prova, o item é encerrado com a fronteira desconhecida: o histórico fica fechado, nunca aberto.
    /// </param>
    public QueueItemHistory? CloseBecauseReturnedToAiExternally(Guid operationId, long stateVersion, DateTimeOffset now, long? messageSeq = null, bool certifiedBoundary = false)
    {
        if (IsClosed)
        {
            return null;
        }

        var deUsuario = AssignedUserId;
        ClearPending();
        Status = QueueItemStatus.Closed;
        AssignedUserId = null;
        AssignedAt = null;
        ClosedAt = now;
        ClosedThroughMessageSeq = BoundaryFor(messageSeq, certifiedBoundary);
        CloseReason = "returned-to-ai";
        ResyncStateVersion = null; // o item encerrado sai do escopo da ressincronização
        ObservedStateVersion = Math.Max(ObservedStateVersion, stateVersion);
        Version++;
        UpdatedAt = now;

        return QueueItemHistory.Create(
            TenantId, Id, SessionId, operationId, HistoryKind.ReturnedToAi, QueueId, QueueId, deUsuario, null, null, ExternalChangeReason, stateVersion, now);
    }

    public const string ExternalChangeReason = "external-change";

    /// <summary>
    /// Desiste da operação pendente porque o Conversations (ou a regra do Channels) a recusou de forma definitiva. O item volta
    /// ao que era, e nada é registrado no histórico, que só guarda o que aconteceu. Uma entrada recusada encerra o item, que
    /// nunca chegou a existir para a conversa.
    /// </summary>
    public void CancelPending(string closeReasonIfEntry, DateTimeOffset now)
    {
        var pendente = Pending ?? throw new QueueItemStateException("Não há operação pendente.");

        ClearPending();
        if (pendente.Kind == PendingKind.Enter)
        {
            Status = QueueItemStatus.Closed;
            ClosedAt = now;
            CloseReason = ValidateReason(closeReasonIfEntry);
            ResyncStateVersion = null; // o item encerrado sai do escopo da ressincronização
        }

        Version++;
        UpdatedAt = now;
    }

    /// <summary>O sufixo do motivo do histórico da transferência que o Conversations só confirmou em parte: a unidade foi aplicada, e o atendimento mudou antes do responsável.</summary>
    public const string ResponderChangedSuffix = ".responder-changed";

    /// <summary>O sufixo do motivo do histórico da transferência cuja unidade o Conversations aplicou, e cuja sessão terminou antes de a operação concluir.</summary>
    public const string SessionEndedSuffix = ".session-ended";

    /// <summary>
    /// O item acompanha a parte da transferência que o Conversations JÁ confirmou (a unidade e o setor de destino): passa para a fila, a
    /// unidade e o setor de destino, que são o lugar onde a conversa está. A intenção continua pendente (quem chama a conclui ou a encerra no
    /// mesmo passo). Devolve o registro do histórico da transferência.
    /// </summary>
    /// <param name="placementEpoch">A geração da colocação de destino (a da autorização mais uma); nula se a autorização não a trouxe: o acesso fica fechado.</param>
    public QueueItemHistory AdoptConfirmedTransferDestination(DateTimeOffset now, string reasonSuffix, long? placementEpoch = null)
    {
        var pendente = Pending ?? throw new QueueItemStateException("Não há operação pendente.");
        if (pendente.Kind != PendingKind.TransferQueue || pendente.BusinessUnitId is not { } unidade)
        {
            throw new QueueItemStateException("Só a transferência que inclui a unidade tem destino já confirmado.");
        }

        var deFila = QueueId;
        QueueId = pendente.QueueId;
        BusinessUnitId = unidade;
        DepartmentId = pendente.DepartmentId;
        PlacementEpoch = placementEpoch;
        ClearHistoryLimit(); // outra geração: o limite (e a origem) da anterior não valem para o vínculo novo, na mesma gravação
        Version++;
        UpdatedAt = now;

        return QueueItemHistory.Create(
            TenantId, Id, SessionId, pendente.OperationId, HistoryKind.TransferredQueue, deFila, QueueId, AssignedUserId, AssignedUserId,
            pendente.ActorUserId, SuffixedReason(pendente.Reason, reasonSuffix), null, now);
    }

    /// <summary>
    /// Conclui uma transferência de unidade que o Conversations confirmou SÓ EM PARTE: a unidade (e o setor) de destino já foram aplicados na
    /// sessão, e a segunda parte (quem responde) foi recusada porque o atendimento mudou depois de a operação ser autorizada. A parte confirmada
    /// não se desfaz (o ContactCenter não reverte, por conta própria, o que o Conversations já gravou): o item passa para a fila, a unidade e o
    /// setor de destino, que são o lugar onde a conversa JÁ está, e a fila de origem perde o acesso. Quem responde, porém, não é o da
    /// autorização: o item espelha o atendimento que o Conversations tem agora (o responsável atual é preservado), ou é encerrado se esse
    /// atendimento acabou. Nada fica pela metade: a fila, a unidade e o setor do item continuam coerentes, e a intenção é limpa.
    /// </summary>
    /// <param name="attendance">
    /// A prova de que o atendimento que o item representa continua (<see cref="ScopeProof.AttendanceOfSession"/>): <c>Same</c> espelha o responsável de agora,
    /// <c>Other</c> encerra o item (o atendimento acabou), <c>Unknown</c> não espelha nem encerra por inferência.
    /// </param>
    /// <param name="responderUserId">O responsável efetivo da sessão agora (nulo = espera).</param>
    /// <param name="stateVersion">A versão do estado da sessão lida junto do responsável: a projeção só avança a partir dela.</param>
    /// <param name="mirrorOperationId">O identificador (determinístico) do registro de histórico da mudança de atendimento que o item passa a espelhar.</param>
    /// <returns>Os registros do histórico, na ordem: a transferência concluída em parte e, se o atendimento é outro, o espelho dele.</returns>
    /// <param name="placementEpoch">A geração da colocação de destino (a da autorização mais uma), ou <c>null</c> se a autorização não a trouxe.</param>
    public IReadOnlyList<QueueItemHistory> CompletePartialTransfer(
        ProofVerdict attendance, Guid? responderUserId, long stateVersion, Guid mirrorOperationId, DateTimeOffset now, long? placementEpoch = null)
    {
        var pendente = Pending ?? throw new QueueItemStateException("Não há operação pendente.");
        if (pendente.Kind != PendingKind.TransferQueue || pendente.BusinessUnitId is not { } unidade)
        {
            throw new QueueItemStateException("Só a transferência que inclui a unidade conclui em parte.");
        }

        RequireId(mirrorOperationId);

        // A parte confirmada: o lugar onde a conversa está. O responsável do item não muda ainda (o espelho vem abaixo).
        var registros = new List<QueueItemHistory> { AdoptConfirmedTransferDestination(now, ResponderChangedSuffix, placementEpoch) };
        ClearPending();

        // Adotar o destino confirmado NÃO cobre a obrigação de ressincronização. Só o espelho abaixo a liquida, e só com a prova de identidade (Same ou Other).
        var espelho = MirrorSession(attendance, responderUserId, stateVersion, mirrorOperationId, now);
        if (espelho is not null)
        {
            registros.Add(espelho);
        }

        return registros;
    }

    /// <summary>
    /// O item passa a refletir o atendimento como o Conversations o tem AGORA, e SÓ com a prova de identidade (<paramref name="attendance"/>, ver
    /// <see cref="ScopeProof.AttendanceOfSession"/>): <c>Same</c> espelha o responsável; <c>Other</c> (em IA, ou em outro período humano) o atendimento que este
    /// item representava acabou, e o item é encerrado com a fronteira desconhecida (o período fica fechado); <c>Unknown</c> não faz NADA (não espelha nem
    /// encerra por inferência) e NÃO liquida a obrigação: quem a liquida como não verificada é <see cref="SettleUnverified"/>, de forma explícita. Uma visão
    /// MAIS ANTIGA que a já observada nunca sobrescreve a mais nova (devolve <c>null</c>, sem mudar nada). Espelhado com a prova, e a visão na versão da
    /// marca ou mais nova, a obrigação (<see cref="ResyncStateVersion"/>) está liquidada.
    /// </summary>
    /// <returns>O registro do histórico, ou <c>null</c> se o item já estava assim, a prova é desconhecida ou a visão era mais antiga que a observada.</returns>
    public QueueItemHistory? MirrorSession(ProofVerdict attendance, Guid? responderUserId, long stateVersion, Guid mirrorOperationId, DateTimeOffset now)
    {
        RequireIdle();
        RequireId(mirrorOperationId);
        if (stateVersion < ObservedStateVersion || attendance == ProofVerdict.Unknown)
        {
            return null;
        }

        var registro = attendance == ProofVerdict.Same
            ? ApplyExternalHandling(responderUserId, mirrorOperationId, stateVersion, now)
            : CloseBecauseReturnedToAiExternally(mirrorOperationId, stateVersion, now);
        if (ResyncStateVersion is { } marca && stateVersion >= marca)
        {
            ResyncStateVersion = null;
        }

        return registro;
    }

    /// <summary>
    /// Liquida a obrigação de ressincronização de um item cuja identidade NÃO se consegue comprovar (legado permanentemente sem prova: o período do item é
    /// desconhecido). Não espelha, não encerra e não preenche identidade alguma por inferência: o conteúdo e os novos comandos continuam negados, e quem chama
    /// registra o resultado como NÃO VERIFICADO (sem declarar convergência). NÃO serve para falha transitória, tempo esgotado ou operação ainda recuperável por
    /// seu identificador: esses casos continuam pendentes, e a obrigação segue aberta. A versão da sessão lida precisa alcançar a marca.
    /// </summary>
    /// <returns><c>true</c> se a obrigação foi liquidada.</returns>
    public bool SettleUnverified(long sessionStateVersion, DateTimeOffset now)
    {
        if (IsClosed || HasPending || ResyncStateVersion is not { } marca || sessionStateVersion < marca)
        {
            return false;
        }

        ResyncStateVersion = null;
        ObservedStateVersion = Math.Max(ObservedStateVersion, sessionStateVersion);
        Version++;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Um evento externo MAIS NOVO que o observado chegou com uma operação do item pendente: ele não pode ser projetado agora (a operação
    /// converge relendo a sessão, e o estado dela ainda vale), mas também não pode ser perdido. Guarda a versão, na mesma gravação do inbox, e a
    /// gravação avança a <see cref="Version"/>: se a operação pendente é concluída ao mesmo tempo, quem perde a corrida é detectado (o executor
    /// reexecuta com a sessão atual, ou o evento é reentregue e projetado). Devolve <c>false</c>, sem mudar nada, se a marca já cobria a versão.
    /// </summary>
    public bool MarkResync(long stateVersion, DateTimeOffset now)
    {
        if (IsClosed || stateVersion <= ObservedStateVersion || (ResyncStateVersion is { } atual && atual >= stateVersion))
        {
            return false;
        }

        ResyncStateVersion = stateVersion;
        Version++;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// A obrigação de ressincronização está ABERTA (<see cref="ResyncStateVersion"/> não nula) num item ativo, haja ou não operação pendente. Enquanto
    /// aberta, o evento posterior não é projetado normalmente: ADIA (sobe a marca).
    /// </summary>
    public bool ResyncOpen => !IsClosed && ResyncStateVersion is not null;

    /// <summary>
    /// O item tem obrigação aberta E nenhuma operação pendente: quem reconcilia lê a sessão e o alinha (<see cref="MirrorSession"/>), ou liquida como não
    /// verificado (<see cref="SettleUnverified"/>). Independe de <see cref="ObservedStateVersion"/>: avançar a versão observada não encerra este trabalho.
    /// </summary>
    public bool NeedsResync => ResyncOpen && !HasPending;

    /// <summary>
    /// A sessão SAIU da geração da colocação a que este item está preso (foi encaminhada, por fora, para outra unidade ou setor): daí em diante o item não
    /// expõe da conversa mais que <paramref name="messageSeq"/>, a sequência da última mensagem naquele instante. SÓ grava com a origem comprovada: a geração da
    /// qual a sessão saiu (<paramref name="fromPlacementEpoch"/>, do evento) é a que o item prendeu (<see cref="ScopeProof.CandidateLimit"/>); de outra geração
    /// ou sem origem não se grava (o acesso já fica fechado pela divergência de geração). Só diminui: encaminhamentos seguintes, e a volta ao escopo, não o
    /// ampliam. Devolve <c>false</c> se nada mudou. Item encerrado nunca muda a fronteira.
    /// </summary>
    public bool LeaveScope(long? messageSeq, long? fromPlacementEpoch, DateTimeOffset now)
    {
        if (IsClosed || ScopeProof.CandidateLimit(fromPlacementEpoch, PlacementEpoch) != BoundaryOrigin.Valid)
        {
            return false;
        }

        var limite = Math.Max(messageSeq ?? 0, 0);
        if (HistoryLimitSeq is { } atual && atual <= limite)
        {
            return false;
        }

        HistoryLimitSeq = limite;
        HistoryLimitOriginEpoch = fromPlacementEpoch;
        PromoteBoundaryModelIfClean();
        Version++;
        UpdatedAt = now;
        return true;
    }

    // O vínculo do item mudou de geração por uma operação própria: o limite e a origem da geração anterior não valem para o novo vínculo.
    private void ClearHistoryLimit()
    {
        HistoryLimitSeq = null;
        HistoryLimitOriginEpoch = null;
    }

    // O modelo certificado (1) quer dizer que TODAS as fronteiras positivas presentes são certificadas. Depois de gravar uma fronteira certificada, o item só é
    // promovido de 0 para 1 se não sobrou fronteira positiva SEM prova (a do encerramento ou a do limite): certificar uma não promove a outra.
    // Um item ATIVO nunca tem fronteira de encerramento, e o fechamento é o único que a grava (uma vez): então a única outra fronteira positiva que pode estar
    // sem prova, na hora de promover, é o limite de saída.
    private void PromoteBoundaryModelIfClean()
    {
        if (BoundaryModel == QueueItemBoundaryModel.Certified)
        {
            return;
        }

        var limiteSemProva = HistoryLimitSeq is > 0 && !(HistoryLimitOriginEpoch is { } origem && PlacementEpoch is { } geracao && origem == geracao);
        if (!limiteSemProva)
        {
            BoundaryModel = QueueItemBoundaryModel.Certified;
        }
    }

    /// <summary>
    /// A fronteira do histórico de um item encerrado: a sequência dada por quem encerrou, SÓ se <paramref name="certified"/> (o caminho que encerrou
    /// comprovou o período e a geração do item no fechamento), limitada pelo que a sessão já tinha deixado de expor (<see cref="HistoryLimitSeq"/>, e só se
    /// o limite tem origem comprovada). Fronteira desconhecida, não certificada, ou limite zero ou sem prova, fecha o histórico (<c>null</c>), nunca o abre.
    /// Gravar uma fronteira positiva certificada promove o item ao modelo certificado se nenhuma outra fronteira positiva ficou sem prova.
    /// </summary>
    private long? BoundaryFor(long? messageSeq, bool certified)
    {
        if (!certified)
        {
            return null;
        }

        long? fronteira;
        if (HistoryLimitSeq is not { } limite)
        {
            fronteira = messageSeq;
        }
        else
        {
            fronteira = limite > 0 && HistoryLimitIsValid && messageSeq is { } fim ? Math.Min(fim, limite) : null;
        }

        if (fronteira is > 0)
        {
            PromoteBoundaryModelIfClean();
        }

        return fronteira;
    }

    /// <summary>
    /// Encerra o item porque a sessão terminou (ou deixou de existir). Descarta uma operação pendente, que perdeu o sentido.
    /// Devolve o registro do histórico, ou <c>null</c> se o item já estava encerrado.
    /// </summary>
    /// <param name="messageSeq">A sequência da última mensagem da sessão ao encerrar (nenhuma chega depois), dada pelo Conversations.</param>
    /// <param name="certifiedBoundary">
    /// <paramref name="messageSeq"/> é a fronteira do atendimento que ESTE item representa, comprovada contra a sessão (<c>HistoryBoundary</c>: o período e a
    /// geração do item no fechamento). Sem a prova, o item é encerrado com a fronteira desconhecida (histórico fechado).
    /// </param>
    public QueueItemHistory? CloseBecauseSessionEnded(Guid operationId, string reason, DateTimeOffset now, long? messageSeq = null, bool certifiedBoundary = false)
    {
        if (IsClosed)
        {
            return null;
        }

        var deUsuario = AssignedUserId;
        ClearPending();
        Status = QueueItemStatus.Closed;
        AssignedUserId = null;
        AssignedAt = null;
        ClosedAt = now;
        ClosedThroughMessageSeq = BoundaryFor(messageSeq, certifiedBoundary);
        CloseReason = ValidateReason(reason);
        ResyncStateVersion = null; // o item encerrado sai do escopo da ressincronização
        Version++;
        UpdatedAt = now;

        return QueueItemHistory.Create(TenantId, Id, SessionId, operationId, HistoryKind.SessionClosed, QueueId, QueueId, deUsuario, null, null, reason, null, now);
    }

    /// <summary>Anota que a tentativa de concluir a operação pendente no Conversations falhou, sem decidir nada sobre o item.</summary>
    public void RecordAttemptFailure(string errorType, DateTimeOffset now)
    {
        if (!HasPending)
        {
            return;
        }

        PendingAttempts++;
        PendingLastError = errorType.Length > ErrorMaxLength ? errorType[..ErrorMaxLength] : errorType;
        PendingNextAttemptAt = now + BackoffFor(PendingAttempts);
        UpdatedAt = now;
    }

    private void RequireIdle()
    {
        if (IsClosed)
        {
            throw new QueueItemStateException("A conversa não está mais em atendimento na fila.");
        }

        if (HasPending)
        {
            throw new QueueItemStateException("Há outra operação em andamento nesta conversa.");
        }
    }

    private static void RequireId(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
        {
            throw new ContactCenterValidationException("Os identificadores da operação não podem ser vazios.");
        }
    }

    private void SetPending(
        PendingKind kind, Guid operationId, Guid queueId, Guid? userId, Guid? businessUnitId, Guid? departmentId, Guid? actorUserId, string? actorOrigin, string reason, DateTimeOffset now,
        HandlingBasis? basis = null)
    {
        if (actorOrigin is not null && (actorOrigin.Length == 0 || actorOrigin.Length > PendingAudit.ActorOriginMaxLength))
        {
            throw new ContactCenterValidationException($"A origem de quem pediu deve ter de 1 a {PendingAudit.ActorOriginMaxLength} caracteres.");
        }

        // Em atendimento humano o período começa em 1; só a sessão em IA que nunca teve atendimento humano está no período 0, e ela não tem responsável.
        if (basis is not null
            && (basis.HandlingPeriod < (basis.InAiMode ? 0 : 1) || basis.ResponderUserId == Guid.Empty || (basis.InAiMode && basis.ResponderUserId is not null) || basis.PlacementEpoch is < 1))
        {
            throw new ContactCenterValidationException("A base de autorização da operação precisa de um período de atendimento e, se houver, de um responsável válido (a IA não tem responsável).");
        }

        PendingBasisPeriod = basis?.HandlingPeriod;
        PendingBasisResponder = basis?.ResponderUserId;
        PendingBasisAiMode = basis?.InAiMode ?? false;
        PendingBasisPlacementEpoch = basis?.PlacementEpoch;
        PendingKindValue = kind;
        PendingOperationId = operationId;
        PendingTransitionId = operationId;
        PendingTransferId = Guid.CreateVersion7();
        PendingQueueId = queueId;
        PendingUserId = userId;
        PendingBusinessUnitId = businessUnitId;
        PendingDepartmentId = departmentId;
        PendingActorUserId = actorUserId;
        PendingActorOrigin = actorOrigin;
        PendingReason = reason;
        PendingStartedAt = now;
        PendingAttempts = 0;
        PendingLastError = null;
        PendingNextAttemptAt = null;
        Version++;
        UpdatedAt = now;
    }

    private void ClearPending()
    {
        PendingKindValue = null;
        PendingOperationId = null;
        PendingTransitionId = null;
        PendingTransferId = null;
        PendingQueueId = null;
        PendingUserId = null;
        PendingBusinessUnitId = null;
        PendingDepartmentId = null;
        PendingActorUserId = null;
        PendingActorOrigin = null;
        PendingBasisPeriod = null;
        PendingBasisResponder = null;
        PendingBasisAiMode = false;
        PendingBasisPlacementEpoch = null;
        PendingReason = null;
        PendingStartedAt = null;
        PendingAttempts = 0;
        PendingLastError = null;
        PendingNextAttemptAt = null;
    }
}

/// <summary>A operação não cabe no estado atual do item (por exemplo, assumir uma conversa encerrada). Quem chama relê o item.</summary>
public sealed class QueueItemStateException : Exception
{
    public QueueItemStateException(string message)
        : base(message)
    {
    }
}
