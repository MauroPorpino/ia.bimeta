using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

public enum ProjectionOutcome
{
    /// <summary>O item foi atualizado (ou a operação pendente dele foi concluída).</summary>
    Applied = 1,

    /// <summary>Nada a fazer: a conversa não está em atendimento na fila, o evento é repetido ou atrasado, ou o item já espelha o estado.</summary>
    Ignored = 2,

    /// <summary>
    /// O evento é externo e mais novo que o observado, e chegou com uma operação do item pendente: a projeção dele foi ADIADA, de forma durável
    /// (a versão ficou guardada no item). O item se alinha à sessão quando a operação termina; o evento não precisa ser reentregue.
    /// </summary>
    Deferred = 3,
}

/// <summary>
/// A projeção do estado de atendimento do Conversations sobre os itens da fila (BOUNDARIES.md §6, "Um job compara a projeção do
/// ContactCenter com os eventos"). O Conversations é a autoridade do modo (IA ou humano) e do responsável; este módulo guarda a
/// fila, o histórico e a intenção. Quando o estado muda por fora deste módulo (por exemplo, alguém devolve a conversa à IA direto no
/// Conversations), o item acompanha, com o rastro. Cada método é idempotente e só avança: a versão do estado do evento decide.
/// <para>
/// Só o contexto de SISTEMA do tenant do evento (o Worker) chama: o tenant vem do envelope gravado pelo Conversations, nunca do
/// payload.
/// </para>
/// </summary>
public sealed class ConversationProjectionService
{
    private readonly IQueueItemRepository _items;
    private readonly HandlingExecutor _executor;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IDeferredAudit _audit;
    private readonly IConversationGateway _conversations;

    public ConversationProjectionService(
        IQueueItemRepository items, HandlingExecutor executor, IRequestContextAccessor context, TimeProvider clock, IDeferredAudit audit, IConversationGateway conversations)
    {
        _items = items;
        _executor = executor;
        _context = context;
        _clock = clock;
        _audit = audit;
        _conversations = conversations;
    }

    /// <summary>
    /// O modo de atendimento ou o responsável da sessão mudou. Se a mudança é a transição da operação pendente do próprio item, a
    /// operação é concluída (o processo pode ter caído entre o Conversations e o ContactCenter). Se foi feita por fora, o item
    /// espelha o novo estado.
    /// </summary>
    /// <exception cref="AccessDeniedException">O contexto não é de sistema.</exception>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou o item ao mesmo tempo: a entrega repete.</exception>
    public async Task<ProjectionOutcome> ApplyHandlingModeChangedAsync(HandlingModeChangedV1 evento, Guid eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);
        RequireSystem();

        // Evento obsoleto ou repetido (versão igual ou menor que a observada), ou conversa sem item ativo: nada a projetar.
        var item = await _items.FindActiveBySessionAsync(evento.SessionId, cancellationToken);
        if (item is null || evento.StateVersion <= item.ObservedStateVersion)
        {
            return ProjectionOutcome.Ignored;
        }

        if (item.Pending is { } pendente)
        {
            if (ScopeProof.TransitionOwner(pendente, evento.TransitionId) != OperationVerdict.Own)
            {
                // Evento EXTERNO e mais novo, com uma operação nossa em andamento: a operação converge relendo a sessão, mas o que ela leu pode
                // ser anterior a este evento (uma mudança que veio entre a releitura e a gravação). Descartá-lo (e o inbox o daria por tratado) deixaria o
                // item atrás do Conversations sem nada a reaplicar. A versão fica guardada no item, na mesma gravação do inbox, e a gravação avança a
                // versão do item: se a operação termina ao mesmo tempo, quem perde a corrida é detectado (o executor reexecuta com a sessão atual, ou
                // este evento é reentregue e projetado). Terminada a operação, o item que ficou atrás é alinhado à sessão pela reconciliação.
                if (!item.MarkResync(evento.StateVersion, _clock.GetUtcNow()))
                {
                    return ProjectionOutcome.Ignored; // a marca já cobria esta versão
                }

                await _items.SaveChangesAsync(cancellationToken);
                return ProjectionOutcome.Deferred;
            }

            // A transição é a da operação em andamento do próprio item: conclui-a. A conclusão relê a sessão e só faz o que falta.
            try
            {
                // Dentro da transação do inbox: a auditoria vai na mesma gravação e só chega à trilha depois da confirmação.
                await _executor.DriveAsync(item, cancellationToken);
            }
            catch (ContactCenterRuleException)
            {
                // O destino foi recusado de forma definitiva e a operação foi desfeita (e registrada): não há mais o que projetar.
            }

            return ProjectionOutcome.Applied;
        }

        // A obrigação de ressincronização está ABERTA (um evento anterior ficou por aplicar): este evento NÃO é projetado normalmente. Espelhar o responsável
        // ou encerrar o item com a fronteira dele, sem aplicar o que ficou antes, atribuiria ao atendimento do item o de OUTRO (e avançar a versão observada
        // daria a obrigação por cumprida sem tê-la cumprido). A marca sobe, e a reconciliação alinha o item ao que a SESSÃO diz agora, com a prova de identidade.
        if (item.ResyncOpen)
        {
            return await DeferAsync(item, evento.StateVersion, cancellationToken);
        }

        // A IDENTIDADE do atendimento a que o evento se refere precisa ser PROVADA contra a do item: o responsável de outro atendimento (outro período
        // humano) nunca é espelhado, e o fim de outro atendimento nunca é a fronteira deste. Sem a identidade (evento anterior ao campo, ou item anterior
        // a ele) não se projeta por inferência: a obrigação fica durável e a reconciliação decide pelo que a sessão diz agora.
        var atendimento = ScopeProof.AttendanceOfEvent(item.HandlingPeriod, evento.HandlingPeriod);
        if (atendimento == ProofVerdict.Unknown)
        {
            return await DeferAsync(item, evento.StateVersion, cancellationToken);
        }

        QueueItemHistory? historico;
        if (atendimento == ProofVerdict.Other)
        {
            // O atendimento que o item representava já tinha acabado (este evento é de outro): o item é encerrado, sem fronteira (o fim dele não é este).
            historico = item.CloseBecauseReturnedToAiExternally(evento.TransitionId, evento.StateVersion, _clock.GetUtcNow());
        }
        else if (evento.To == HandlingMode.AiAutonomous)
        {
            // A fronteira do histórico é a sequência da última mensagem NO INSTANTE da transição (dada pelo Conversations no próprio evento),
            // nunca o instante em que este módulo a processou: o que chegou depois dela não é do atendimento que terminou. Só é CERTIFICADA se o evento prova
            // também a colocação do item (a geração em que a sessão terminou o atendimento é a do item, ou o item guarda um limite de saída com origem comprovada).
            var colocacao = ScopeProof.PlacementOfEvent(item.PlacementEpoch, evento.PlacementEpoch);
            var certificada = colocacao == ProofVerdict.Same || (colocacao == ProofVerdict.Other && item.HistoryLimitIsValid);
            historico = item.CloseBecauseReturnedToAiExternally(evento.TransitionId, evento.StateVersion, _clock.GetUtcNow(), evento.MessageSeq, certificada);
        }
        else if (evento.To == HandlingMode.HumanAssisted)
        {
            historico = item.ApplyExternalHandling(evento.ResponderRef, evento.TransitionId, evento.StateVersion, _clock.GetUtcNow());
        }
        else
        {
            return ProjectionOutcome.Ignored; // AiSupervised ainda não está especificado
        }

        // A auditoria é guardada na MESMA gravação do estado projetado (e entregue à trilha depois da confirmação do inbox): se a
        // transação for desfeita, a entrada some junto, e a trilha nunca diz "sucesso" para o que não aconteceu.
        if (historico is not null)
        {
            _items.AddHistory(historico);
            _audit.Defer(new AuditEntry("contact-center.item.external-change", "queue-item", item.Id.ToString(), AuditOutcome.Success));
        }

        await _items.SaveChangesAsync(cancellationToken);
        return ProjectionOutcome.Applied;
    }

    /// <summary>A sessão foi encerrada: o item ativo dela é encerrado, com o rastro. Repetir é inofensivo.</summary>
    public async Task<ProjectionOutcome> ApplySessionClosedAsync(SessionClosedV1 evento, Guid eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);
        RequireSystem();

        var item = await _items.FindActiveBySessionAsync(evento.SessionId, cancellationToken);
        if (item is null)
        {
            return ProjectionOutcome.Ignored;
        }

        // Com operação pendente, quem decide como o item termina é o executor: ele conhece o que o Conversations JÁ confirmou (a unidade de uma
        // transferência, por exemplo), leva o item para onde a conversa estava, fecha o recibo e deixa a auditoria do efeito, com a proveniência do pedido.
        if (item.HasPending)
        {
            try
            {
                await _executor.DriveAsync(item, cancellationToken);
            }
            catch (ContactCenterRuleException)
            {
                // A operação foi desfeita (e registrada); o item é encerrado na própria execução, se a sessão terminou.
            }

            return ProjectionOutcome.Applied;
        }

        // A fronteira do histórico é decidida contra a sessão, não só pela sequência final: se ela já tinha saído do escopo deste item (outra unidade
        // ou setor, outro atendimento) quando terminou, o encerramento não amplia o que o item pôde ver, projetada ou não a saída.
        var sessao = await _conversations.GetSessionAsync(evento.SessionId, cancellationToken);
        var fronteira = HistoryBoundary.ForEndedSession(item, sessao, evento.LastMessageSeq);
        var historico = item.CloseBecauseSessionEnded(eventId, "session-closed", _clock.GetUtcNow(), fronteira.Seq, fronteira.Certified);
        if (historico is null)
        {
            return ProjectionOutcome.Ignored;
        }

        _items.AddHistory(historico);
        _audit.Defer(new AuditEntry(HandlingExecutor.OperationName(HistoryKind.SessionClosed), "queue-item", item.Id.ToString(), AuditOutcome.Success));
        await _items.SaveChangesAsync(cancellationToken);
        return ProjectionOutcome.Applied;
    }

    /// <summary>
    /// A sessão foi encaminhada para outra unidade ou setor (<c>SessionTransferredV1</c>). Se isso a tira do escopo do item (o lugar dele já não é o
    /// de destino) e não é o encaminhamento da PRÓPRIA operação pendente dele, o item passa a limitar o histórico que expõe à sequência da última
    /// mensagem no instante do encaminhamento: o que a conversa receber depois é da unidade de destino, mesmo que a sessão volte e mesmo que ela
    /// termine depois. Sem a sequência (evento anterior ao campo), o limite é zero. Repetir é inofensivo (o limite só diminui).
    /// </summary>
    public async Task<ProjectionOutcome> ApplySessionTransferredAsync(SessionTransferredV1 evento, Guid eventId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);
        RequireSystem();

        var item = await _items.FindActiveBySessionAsync(evento.SessionId, cancellationToken);
        if (item is null)
        {
            return ProjectionOutcome.Ignored;
        }

        // O encaminhamento da própria operação pendente: o item vai para o destino ao concluir, e a saída da fila de origem é intencional. A identidade é a da
        // OPERAÇÃO (o TransferId que a intenção guardou), nunca o destino: um encaminhamento de outro para o mesmo destino não é o da operação do item.
        if (ScopeProof.TransferOwner(item.Pending, evento.TransferId) == OperationVerdict.Own)
        {
            return ProjectionOutcome.Ignored;
        }

        // A sessão SAIU da geração da colocação a que o item está preso? A prova é a geração de origem do evento contra a do item (não o destino): de outra
        // geração (o item já tinha saído, ou a saída é de outra colocação) a fronteira é ignorada, e sem a geração (evento ou item anteriores ao campo) não
        // se grava um limite sem origem. Nos dois casos o acesso já fica fechado pela divergência de geração, no momento da leitura.
        if (ScopeProof.PlacementOfEvent(item.PlacementEpoch, evento.FromPlacementEpoch) != ProofVerdict.Same
            || !item.LeaveScope(evento.MessageSeq, evento.FromPlacementEpoch, _clock.GetUtcNow()))
        {
            return ProjectionOutcome.Ignored;
        }

        _audit.Defer(new AuditEntry("contact-center.item.scope-left", "queue-item", item.Id.ToString(), AuditOutcome.Success));
        await _items.SaveChangesAsync(cancellationToken);
        return ProjectionOutcome.Applied;
    }

    /// <summary>
    /// Guarda, de forma durável, que o item ficou atrás do Conversations (a obrigação de ressincronização sobe até a versão do evento), sem projetar o evento. O
    /// evento não precisa ser reentregue: a reconciliação alinha o item à sessão.
    /// </summary>
    private async Task<ProjectionOutcome> DeferAsync(QueueItem item, long stateVersion, CancellationToken cancellationToken)
    {
        if (!item.MarkResync(stateVersion, _clock.GetUtcNow()))
        {
            return ProjectionOutcome.Ignored; // a marca já cobria esta versão
        }

        await _items.SaveChangesAsync(cancellationToken);
        return ProjectionOutcome.Deferred;
    }

    private void RequireSystem()
    {
        if (!_context.GetRequired().IsSystem)
        {
            throw new AccessDeniedException(Permission.SystemOperation);
        }
    }
}

/// <summary>
/// A única leitura ENTRE tenants do módulo: quais tenants têm operação pendente há tempo e já elegível para nova tentativa. Só o
/// Worker a usa, para abrir um contexto de sistema por tenant e reconciliar; devolve só identificadores de tenant, nunca dado do item.
/// Os tenants vêm na ordem em que a pendência mais antiga de cada um ficou elegível, e quem acabou de falhar recua (não ocupa o lote
/// do ciclo seguinte), de modo que nenhum tenant com falha persistente impede os outros de serem atendidos.
/// </summary>
public interface IPendingWorkSource
{
    Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(DateTimeOffset startedBefore, DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>Os tenants com auditoria guardada esperando entrega à trilha (<see cref="IDeferredAudit"/>), a mais antiga primeiro.</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithPendingAuditAsync(int take, CancellationToken cancellationToken);
}
