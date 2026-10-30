using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// Persistência do módulo. Toda consulta só enxerga o tenant do contexto (TEN-2 e TEN-3) e falha fechada sem contexto. As
/// entidades devolvidas por <c>Find</c> sem o sufixo <c>ReadOnly</c> são rastreadas: o caso de uso as altera e chama
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IQueueRepository
{
    /// <exception cref="QueueNameInUseException">Já existe uma fila com este nome na organização.</exception>
    Task AddQueueAsync(ServiceQueue queue, CancellationToken cancellationToken);

    /// <summary>A fila rastreada para alteração.</summary>
    Task<ServiceQueue?> FindQueueAsync(Guid id, CancellationToken cancellationToken);

    Task<ServiceQueue?> FindQueueReadOnlyAsync(Guid id, CancellationToken cancellationToken);

    /// <param name="organizationIds">As organizações visíveis, ou <c>null</c> para todas as do tenant.</param>
    Task<IReadOnlyList<ServiceQueue>> ListQueuesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken);

    /// <summary>As filas em que o usuário é atendente, de qualquer organização do tenant.</summary>
    Task<IReadOnlyList<ServiceQueue>> ListQueuesOfMemberAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>O atendente da fila rastreado para alteração.</summary>
    Task<QueueMembership?> FindMembershipAsync(Guid queueId, Guid userId, CancellationToken cancellationToken);

    Task<QueueMembership?> FindMembershipReadOnlyAsync(Guid queueId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Todas as participações do usuário em filas (para saber o que ele enxerga).</summary>
    Task<IReadOnlyList<QueueMembership>> ListMembershipsOfUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <exception cref="ContactCenterConflictException">Outra operação colocou o mesmo atendente na fila primeiro.</exception>
    Task AddMembershipAsync(QueueMembership membership, CancellationToken cancellationToken);

    Task RemoveMembershipAsync(QueueMembership membership, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueueMembership>> ListMembershipsAsync(Guid queueId, CancellationToken cancellationToken);

    /// <summary>Grava as alterações das entidades rastreadas.</summary>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou o mesmo recurso antes.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// O que cada usuário enxerga dos itens da fila. Nulo em <see cref="ReadableOrganizationIds"/> significa sem restrição
/// (contexto de sistema ou concessão de tenant). Quem está em uma fila enxerga os itens dela; quem lê as filas de uma
/// organização, os de todas as filas dela. Nada além disso: estar em uma fila não abre as outras.
/// </summary>
public sealed record QueueItemScope(
    IReadOnlyCollection<Guid>? ReadableOrganizationIds,
    IReadOnlyCollection<Guid> MemberQueueIds,
    Guid? UserId);

public sealed record QueueItemFilter(Guid? QueueId, QueueItemStatus? Status, bool AssignedToMe, int Take);

public interface IQueueItemRepository
{
    /// <summary>O item rastreado para alteração.</summary>
    Task<QueueItem?> FindItemAsync(Guid id, CancellationToken cancellationToken);

    Task<QueueItem?> FindItemReadOnlyAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>O item ainda ativo (não encerrado) da sessão, rastreado.</summary>
    Task<QueueItem?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Relê o item do banco (descartando o que o objeto guardava) e devolve o objeto atualizado. Quem conclui uma operação relê antes de
    /// cada escrita no Conversations: o objeto que veio de antes pode estar defasado (outra tentativa concluiu ou desfez a operação).
    /// </summary>
    Task<QueueItem> ReloadItemAsync(QueueItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Tranca o item para quem vai concluir a operação pendente dele no Conversations (trava consultiva do banco, que sobrevive à
    /// transação). Só uma tentativa de cada vez escreve no Conversations por conta de um item, e quem espera relê o item ao entrar.
    /// Devolve <c>null</c> se não conseguiu a trava em um prazo curto (outra tentativa está concluindo a operação): quem chama deixa a
    /// operação pendente, que a outra tentativa (ou o job) conclui.
    /// </summary>
    Task<IAsyncDisposable?> TryLockItemAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// O token de cerca (<c>fencing token</c>) de quem vai escrever no Conversations por conta da operação <paramref name="operationId"/>
    /// do item. A emissão e a revalidação da intenção são UM passo atômico, sob a trava da linha do item: o token só é dado se a operação
    /// AINDA é a pendente do item (nunca a quem a perdeu, por cancelamento ou substituição) e é o próximo da sequência do módulo (monotônico).
    /// A emissão avança a versão do item, então quem pegou um token antes não grava mais no item. A trava do banco por item vale para uma
    /// conexão, que pode se perder sem que o executor saiba; o token, validado por quem altera a sessão e pela versão do item, não depende disso.
    /// Devolve <c>null</c> se a operação já não é a pendente do item (concluída, desfeita ou substituída): quem chama não escreve nada.
    /// </summary>
    Task<long?> AcquireFenceTokenAsync(Guid itemId, Guid operationId, CancellationToken cancellationToken);

    /// <summary>O recibo da operação com este identificador (a chave de idempotência do pedido), rastreado para alteração, ou <c>null</c>.</summary>
    Task<QueueOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken);

    Task<QueueOperation?> FindOperationReadOnlyAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Grava o recibo junto do que for gravado em seguida (a mesma transação do <see cref="SaveChangesAsync"/>).</summary>
    void AddOperation(QueueOperation operation);

    void RemoveOperation(QueueOperation operation);

    /// <summary>Os itens em espera da fila, sem operação pendente, do mais urgente para o menos e, na mesma prioridade, do mais antigo.</summary>
    Task<IReadOnlyList<QueueItem>> ListWaitingAsync(Guid queueId, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Quantas conversas ocupam o atendente nesta fila: as atribuídas a ele, ativas, MAIS as que têm uma atribuição ou transferência
    /// para ele ainda pendente (a intenção já gravada, que o Conversations ainda não confirmou).
    /// </summary>
    Task<int> CountOccupiedAsync(Guid queueId, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Grava o que foi rastreado se o atendente ainda tem vaga na fila (menos de <paramref name="maximum"/> conversas ocupadas). A
    /// conferência e a gravação acontecem na MESMA transação, sob uma trava do banco por fila e atendente: dois pedidos simultâneos
    /// do mesmo atendente não passam juntos do limite. Devolve <c>false</c>, sem gravar nada (o rastreamento é descartado), se ele não tem vaga.
    /// <para>
    /// Sob a mesma trava, ANTES de contar, confere se o recibo de <paramref name="operationId"/> já existe: o mesmo pedido repetido ao mesmo
    /// tempo conta com a vaga que o primeiro já ocupou, e responder "sem vaga" a ele seria negar a quem já foi atendido. Quem perde essa
    /// disputa recebe <see cref="OperationIdInUseException"/> e converge para o resultado do primeiro.
    /// </para>
    /// </summary>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou o item antes.</exception>
    /// <exception cref="OperationIdInUseException">Outro pedido gravou o recibo com este identificador primeiro.</exception>
    Task<bool> SaveWithinCapacityAsync(Guid queueId, Guid userId, int maximum, Guid operationId, CancellationToken cancellationToken);

    /// <summary>Quantas conversas esperam na fila (em espera, ainda sem atendente).</summary>
    Task<int> CountWaitingAsync(Guid queueId, CancellationToken cancellationToken);

    /// <summary>A operação já foi concluída neste item (consta no histórico)?</summary>
    Task<bool> OperationAppliedAsync(Guid itemId, Guid operationId, CancellationToken cancellationToken);

    /// <summary>Grava o item novo e o recibo da entrada na MESMA transação.</summary>
    /// <exception cref="QueueItemConflictException">A sessão já tem um item ativo (outra entrada venceu).</exception>
    /// <exception cref="OperationIdInUseException">Outro pedido gravou o recibo com este identificador primeiro.</exception>
    Task AddItemAsync(QueueItem item, QueueOperation operation, CancellationToken cancellationToken);

    void AddHistory(QueueItemHistory history);

    Task<IReadOnlyList<QueueItem>> ListItemsAsync(QueueItemScope scope, QueueItemFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueueItemHistory>> ListHistoryAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Os itens com operação pendente iniciada antes do limite e JÁ ELEGÍVEIS para nova tentativa (a que falhou só volta depois do recuo
    /// dela), na ordem em que ficaram elegíveis: a operação que não conclui recua e não ocupa o lote para sempre.
    /// </summary>
    Task<IReadOnlyList<QueueItem>> ListPendingAsync(DateTimeOffset startedBefore, DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Os itens ativos, SEM operação pendente, que ficaram atrás do Conversations (<see cref="QueueItem.NeedsResync"/>): um evento externo mais novo
    /// que o observado foi adiado enquanto a operação deles esperava. Os mais antigos primeiro.
    /// </summary>
    Task<IReadOnlyList<QueueItem>> ListStaleProjectionsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Grava o que foi rastreado.</summary>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou o item antes, ou já concluiu esta mesma operação.</exception>
    /// <exception cref="OperationIdInUseException">Outro pedido gravou o recibo com este identificador primeiro.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A porta do ContactCenter para o Conversations: ler a sessão e o histórico dela e pedir as mudanças de atendimento. O
/// ContactCenter decide se o usuário pode (pela fila, pela unidade e pelo papel dele) ANTES de chamar; o Conversations impõe a
/// máquina de estados e a <c>StateVersion</c>. A matriz de dependências deixa o ContactCenter referenciar o
/// <c>Conversations.Contracts</c>, mas o atendente comum não tem permissão de conversa por organização (o acesso dele vem da
/// fila), então o host fornece a implementação que chama com o contexto de SISTEMA do mesmo tenant. Sem outra implementação
/// registrada, vale a que usa o contexto de quem chamou: o Conversations recusa quem não tem permissão (falha fechada).
/// </summary>
public interface IConversationGateway
{
    Task<SessionView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<Page<MessageView>?> GetHistoryAsync(GetHistoryQuery query, CancellationToken cancellationToken);

    Task<TransitionResult> TransitionAsync(TransitionHandlingModeCommand command, CancellationToken cancellationToken);

    Task<TransferSessionResult> TransferAsync(TransferSessionCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Registra a cerca do executor na sessão e devolve a sessão lida DEPOIS disso (ver <see cref="IHandlingCommands.FenceHandlingAsync"/>).
    /// Um token menor que o maior já visto é recusado: quem o apresenta foi substituído e não escreve mais.
    /// </summary>
    Task<FenceHandlingResult> FenceAsync(FenceHandlingCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// A SEXTA operação da porta, e a única que não escreve nem trava: se um efeito (transição ou encaminhamento) já foi aplicado, pelo identificador que a
    /// intenção guardou. É como o ContactCenter reconhece o que o Conversations JÁ fez quando não tem mais autoridade de escrever (a intenção sem a base completa
    /// não emite comando algum), sem usar um comando modificador como consulta. Só <see cref="EffectOutcome.Applied"/> é prova; a falha da consulta é do
    /// chamador (<see cref="EffectOutcome.Unavailable"/>).
    /// </summary>
    Task<EffectView> GetEffectAsync(GetEffectQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// A auditoria do que acontece DENTRO de uma transação que ainda pode ser desfeita (o consumo dos eventos do Conversations). A entrada
/// é guardada na mesma transação do que está sendo gravado e só chega à trilha depois da confirmação: nunca um "sucesso" para o que
/// foi desfeito, e nunca uma entrada perdida se o processo cair entre a confirmação e a entrega (a entrega é pelo menos uma vez).
/// </summary>
public interface IDeferredAudit
{
    /// <summary>
    /// Guarda a entrada junto do que o módulo está gravando: vai na mesma transação do próximo <c>SaveChanges</c>. Captura, do contexto
    /// confiável e do relógio, QUEM agiu, por qual origem e QUANDO: a entrega os grava, mesmo que venha de outro processo e muito depois.
    /// </summary>
    void Defer(AuditEntry entry);

    /// <summary>
    /// Como <see cref="Defer(AuditEntry)"/>, mas com a PROVENIÊNCIA dada (o ator, a origem e o instante da operação ORIGINAL), em vez da
    /// do contexto de quem está gravando agora. É para quem conclui ou desfaz uma intenção que outro pediu, muitas vezes em outro processo
    /// e depois (o job de reconciliação): a auditoria diz o que o autor pediu, e não o que o job é.
    /// </summary>
    void Defer(AuditEntry entry, AuditProvenance provenance);

    /// <summary>
    /// Para a auditoria que NÃO acompanha uma mudança de estado do módulo (a negação de uma operação: permissão, papel, regra): guarda a
    /// entrada e a confirma sozinha, e tenta entregá-la. Se a trilha estiver fora do ar, a entrada continua guardada e a reconciliação a
    /// entrega: a negação nunca se perde, e a operação continua recusada do mesmo jeito.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Entrega à trilha as entradas guardadas do tenant do contexto, as mais antigas primeiro, e as tira da fila depois de entregues.
    /// Não entrega de dentro de uma transação aberta (ela ainda pode ser desfeita). Falha da trilha deixa o restante para a próxima
    /// entrega. Devolve quantas entregou.
    /// </summary>
    Task<int> FlushAsync(int take, CancellationToken cancellationToken);

    /// <summary>Quantas entradas do tenant do contexto esperam entrega.</summary>
    Task<int> PendingCountAsync(CancellationToken cancellationToken);
}

/// <summary>A política de uma fila como estava em UM instante: a fila, as faixas de atendimento e os dias fechados.</summary>
public sealed record QueuePolicySnapshot(ServiceQueue Queue, IReadOnlyList<QueueScheduleWindow> Windows, IReadOnlyList<QueueClosure> Closures)
{
    /// <summary>A fila atende neste instante? Sem fuso a fila atende sempre; com fuso, vale o horário deste mesmo instantâneo.</summary>
    public bool IsOpenAt(DateTimeOffset now) =>
        Queue.TimeZoneId is not { } fuso || new QueueSchedule(fuso, Windows, Closures.Select(f => f.Date)).IsOpenAt(now);
}

/// <summary>Regras de roteamento, horário de atendimento e presença dos atendentes. Mesmas garantias de tenant dos demais repositórios.</summary>
public interface IRoutingRepository
{
    Task AddRuleAsync(RoutingRule rule, CancellationToken cancellationToken);

    /// <summary>A regra rastreada para alteração.</summary>
    Task<RoutingRule?> FindRuleAsync(Guid id, CancellationToken cancellationToken);

    Task<RoutingRule?> FindRuleReadOnlyAsync(Guid id, CancellationToken cancellationToken);

    void RemoveRule(RoutingRule rule);

    /// <param name="organizationIds">As organizações visíveis, ou <c>null</c> para todas as do tenant.</param>
    Task<IReadOnlyList<RoutingRule>> ListRulesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken);

    /// <summary>As regras ligadas da organização, na ordem de avaliação.</summary>
    Task<IReadOnlyList<RoutingRule>> ListEnabledRulesAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// A fila (política, fuso, contingência), as faixas de atendimento e os dias fechados lidos do MESMO instante: uma troca de horário
    /// em andamento aparece inteira ou não aparece, nunca o fuso novo com as faixas antigas. Devolve <c>null</c> se a fila não existe.
    /// </summary>
    Task<QueuePolicySnapshot?> GetPolicySnapshotAsync(Guid queueId, CancellationToken cancellationToken);

    /// <summary>
    /// Troca as faixas e os dias fechados da fila, e grava a fila (já alterada e rastreada) na MESMA transação: ou o horário inteiro
    /// novo vale, ou o antigo continua.
    /// </summary>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou a fila antes.</exception>
    Task ReplaceScheduleAsync(ServiceQueue queue, IReadOnlyList<QueueScheduleWindow> windows, IReadOnlyList<QueueClosure> closures, CancellationToken cancellationToken);

    /// <summary>A presença rastreada para alteração, ou <c>null</c> se o atendente nunca a informou (vale Offline).</summary>
    Task<AgentPresence?> FindPresenceAsync(Guid userId, CancellationToken cancellationToken);

    Task<AgentPresence?> FindPresenceReadOnlyAsync(Guid userId, CancellationToken cancellationToken);

    /// <exception cref="ContactCenterConflictException">Outra requisição criou a presença do mesmo atendente primeiro.</exception>
    Task AddPresenceAsync(AgentPresence presence, CancellationToken cancellationToken);

    /// <summary>
    /// Quantos atendentes da fila (atendente ou supervisor) estão disponíveis: presença <c>Available</c> e, se a fila limita
    /// as conversas por atendente, ainda abaixo do limite.
    /// </summary>
    Task<int> CountAvailableAgentsAsync(Guid queueId, int? maxItemsPerAgent, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
