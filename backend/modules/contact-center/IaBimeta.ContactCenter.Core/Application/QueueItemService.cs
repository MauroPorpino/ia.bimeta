using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// Entrada de conversas nas filas, atribuição, transferência e handoff IA/humano. O ContactCenter decide SE e PARA QUEM, pela
/// fila, pela unidade e pelo papel do usuário, e só então pede ao Conversations a mudança de estado, que ele impõe com a
/// <c>StateVersion</c> (ADR 0007). Tenant e permissões vêm do contexto confiável (TEN-1): nenhum método aceita tenant, e o
/// usuário que age é sempre o do contexto, nunca um parâmetro.
/// <list type="bullet">
/// <item>Um item que o usuário não enxerga responde como inexistente (404, TEN-2). Enxergar sem poder operar é negado e
/// auditado (403).</item>
/// <item>A disputa por uma conversa é decidida pela versão do item: de dois atendentes que a assumem juntos, só um grava a
/// intenção e chama o Conversations; o outro recebe o conflito.</item>
/// <item>Toda operação tem um identificador (<c>requestId</c>), que é a chave de idempotência dela e a do Conversations.</item>
/// <item>A IA nunca escolhe a fila, a unidade nem o atendente: quem chama o handoff pelo Agents passa pela mesma validação.</item>
/// </list>
/// </summary>
public sealed class QueueItemService
{
    public const int MaxPageSize = 100;

    private const string ItemResource = "queue-item";

    private readonly IQueueItemRepository _items;
    private readonly IQueueRepository _queues;
    private readonly IConversationGateway _conversations;
    private readonly HandlingExecutor _executor;
    private readonly QueueRouter _router;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IDeferredAudit _audit;
    private readonly IChannelReplyPolicy _channelReply;
    private readonly IAttachmentGateway _attachments;

    public QueueItemService(
        IQueueItemRepository items,
        IQueueRepository queues,
        IConversationGateway conversations,
        HandlingExecutor executor,
        QueueRouter router,
        IRequestContextAccessor context,
        TimeProvider clock,
        IDeferredAudit audit,
        IChannelReplyPolicy channelReply,
        IAttachmentGateway attachments)
    {
        _items = items;
        _queues = queues;
        _conversations = conversations;
        _executor = executor;
        _router = router;
        _context = context;
        _clock = clock;
        _audit = audit;
        _channelReply = channelReply;
        _attachments = attachments;
    }

    // Entrada

    /// <summary>
    /// Pede o atendimento humano: a conversa entra na fila e o Conversations passa a atendimento humano pendente (sem responsável),
    /// o que faz a IA deixar de responder nela. Quem pede é o sistema (o Agents) ou um usuário com permissão de gerir a conversa na
    /// organização dela. A fila pode ser PEDIDA (precisa atender a conversa como ela está) ou, com <paramref name="queueId"/> nulo,
    /// decidida pelo ROTEAMENTO (as regras de canal, unidade e setor da organização, que também dão a prioridade e podem levar a
    /// conversa para a unidade da fila, se o número a autoriza). Em ambos vale a política da fila (pausa, horário, sem atendente).
    /// Repetir o mesmo <paramref name="requestId"/> devolve o mesmo resultado, e pedir de novo para uma conversa que já está em fila
    /// também.
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">A conversa ou a fila pedida não existem ou não são visíveis ao usuário.</exception>
    /// <exception cref="AccessDeniedException">O usuário não gere a conversa na organização dela.</exception>
    /// <exception cref="ContactCenterRuleException">Nenhuma fila pode receber a conversa (não atende, pausada, fechada, sem rota), ou a conversa já tem atendente humano.</exception>
    /// <exception cref="QueueItemConflictException">A conversa já está em outra fila.</exception>
    public async Task<QueueItemOperationResult> RequestHandoffAsync(
        Guid sessionId, Guid? queueId, Guid requestId, string reason, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var motivo = QueueItem.ValidateReason(reason);

        // O mesmo pedido, do mesmo autor, para a mesma conversa, que já terminou: devolve a resposta que ele já tinha recebido.
        var esperado = new Expect(OperationKind.Enter, SessionId: sessionId);
        if (await ReplayFinishedAsync(requestId, esperado, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        // A sessão é lida pelo contexto de sistema do host; quem não a enxerga na organização dela recebe "inexistente".
        var sessao = await _conversations.GetSessionAsync(sessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Conversa");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ConversationManage, sessao.OrganizationId,
            HandlingExecutor.OperationName(PendingKind.Enter), "session", sessao.Id.ToString(), cancellationToken);

        ServiceQueue? pedida = null;
        if (queueId is { } filaPedida)
        {
            pedida = await _queues.FindQueueReadOnlyAsync(filaPedida, cancellationToken);
            if (pedida is null || pedida.OrganizationId != sessao.OrganizationId || !contexto.IsAllowed(Permission.QueueRead, pedida.OrganizationId))
            {
                throw new ContactCenterResourceNotFoundException("Fila");
            }
        }

        // Repetição do mesmo pedido, ou conversa que já está em fila: nada novo a fazer.
        var existente = await _items.FindActiveBySessionAsync(sessao.Id, cancellationToken);
        if (existente is not null)
        {
            if (existente.Pending is { } pendente && pendente.OperationId == requestId)
            {
                RequirePendingOwner(pendente, contexto);
                return await _executor.DriveAsync(existente, cancellationToken);
            }

            if (pedida is null || existente.QueueId == pedida.Id)
            {
                return await RecordNoChangeAsync(existente, requestId, OperationKind.Enter, null, esperado, contexto, cancellationToken);
            }

            throw new QueueItemConflictException(QueueItemConflict.ActiveItemExists, "A conversa já está em outra fila: transfira-a em vez de pedir um novo atendimento.");
        }

        if (sessao.Status == SessionStatus.Closed)
        {
            throw await RuleAsync(PendingKind.Enter, sessao.Id, ContactCenterRule.SessionClosed, "A conversa está encerrada.", cancellationToken);
        }

        // O percurso de cada fila (cobertura, pausa, horário, atendente disponível) e a ordem dos destinos são decididos aqui, por regra.
        var agora = _clock.GetUtcNow();
        var decisao = pedida is not null
            ? await _router.ResolveRequestedAsync(sessao, pedida, agora, cancellationToken)
            : await _router.ResolveByRulesAsync(sessao, agora, cancellationToken);
        if (decisao.Candidates.Count == 0)
        {
            var recusa = decisao.Refusals.Count > 0 ? decisao.Refusals[0].Rule : ContactCenterRule.NoRoute;
            throw await RuleAsync(PendingKind.Enter, sessao.Id, recusa, RefusalMessage(recusa), cancellationToken);
        }

        // Quem já tem atendente humano definido fora do ContactCenter não é chutado dele pela entrada na fila.
        if (sessao.HandlingMode == HandlingMode.HumanAssisted && sessao.ResponderRef is not null)
        {
            throw await RuleAsync(
                PendingKind.Enter, sessao.Id, ContactCenterRule.ConversationAlreadyHandledByHuman, "A conversa já tem um atendente humano definido.", cancellationToken);
        }

        // Tenta cada destino na ordem. Só a recusa DEFINITIVA da unidade (o número não a autoriza, ou ela não existe) passa para o
        // próximo; uma falha da confirmação (Channels fora do ar) deixa a entrada pendente e para aqui: não se escolhe outro destino
        // no lugar do que não pôde ser confirmado.
        ContactCenterRuleException? ultimaRecusa = null;
        foreach (var candidato in decisao.Candidates)
        {
            try
            {
                return await EnterAsync(sessao, candidato, requestId, motivo, contexto, agora, cancellationToken);
            }
            catch (ContactCenterRuleException ex) when (ex.Rule is ContactCenterRule.UnitNotAuthorizedForChannel or ContactCenterRule.TargetUnitNotAllowed)
            {
                ultimaRecusa = ex;
            }
        }

        throw ultimaRecusa!;
    }

    /// <summary>
    /// A pergunta "um pedido de atendimento humano feito agora seria aceito?" (<c>CheckOnly</c> do contrato de handoff). SOMENTE LEITURA: usa o mesmo roteador e as mesmas pré-condições de
    /// <see cref="RequestHandoffAsync"/> (conversa visível e gerível, fila pedida, cobertura, rota, pausa, horário, atendente da conversa), mas não registra intenção, recibo, item, transição nem auditoria
    /// de recusa: quem pergunta só quer saber, e o pedido de verdade audita o que for recusado. Volta sem exceção quando as regras aceitariam; senão lança a MESMA recusa do pedido real.
    /// </summary>
    public async Task CheckHandoffRouteAsync(Guid sessionId, Guid? queueId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var sessao = await _conversations.GetSessionAsync(sessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Conversa");
        }

        // A mesma permissão do pedido de verdade, sem a trilha: quem não pode pedir também não pode perguntar.
        if (!contexto.IsAllowed(Permission.ConversationManage, sessao.OrganizationId))
        {
            throw new AccessDeniedException(Permission.ConversationManage);
        }

        ServiceQueue? pedida = null;
        if (queueId is { } filaPedida)
        {
            pedida = await _queues.FindQueueReadOnlyAsync(filaPedida, cancellationToken);
            if (pedida is null || pedida.OrganizationId != sessao.OrganizationId || !contexto.IsAllowed(Permission.QueueRead, pedida.OrganizationId))
            {
                throw new ContactCenterResourceNotFoundException("Fila");
            }
        }

        // A conversa que já está em fila não tem o que prever: o pedido de verdade só reconhece o que já existe.
        if (await _items.FindActiveBySessionAsync(sessao.Id, cancellationToken) is not null)
        {
            return;
        }

        if (sessao.Status == SessionStatus.Closed)
        {
            throw new ContactCenterRuleException(ContactCenterRule.SessionClosed, "A conversa está encerrada.");
        }

        var agora = _clock.GetUtcNow();
        var decisao = pedida is not null
            ? await _router.ResolveRequestedAsync(sessao, pedida, agora, cancellationToken)
            : await _router.ResolveByRulesAsync(sessao, agora, cancellationToken);
        if (decisao.Candidates.Count == 0)
        {
            var recusa = decisao.Refusals.Count > 0 ? decisao.Refusals[0].Rule : ContactCenterRule.NoRoute;
            throw new ContactCenterRuleException(recusa, RefusalMessage(recusa));
        }

        if (sessao.HandlingMode == HandlingMode.HumanAssisted && sessao.ResponderRef is not null)
        {
            throw new ContactCenterRuleException(ContactCenterRule.ConversationAlreadyHandledByHuman, "A conversa já tem um atendente humano definido.");
        }
    }

    private async Task<QueueItemOperationResult> EnterAsync(
        SessionView sessao, RouteCandidate candidato, Guid requestId, string motivo, RequestContext contexto, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        // A entrada foi autorizada sobre a sessão COMO FOI LIDA no início do pedido (a IA, ou a espera sem atendente), e é esse estado que fica
        // na intenção, na mesma gravação: o executor o leva a cada comando como pré-condição. Quem assumir a conversa entre aquela leitura e a
        // gravação no Conversations não é retirado por uma entrada que foi autorizada sem ele.
        var item = QueueItem.Enter(
            contexto.TenantId, sessao.Id, sessao.OrganizationId, sessao.BusinessUnitId, sessao.DepartmentId, candidato.Queue.Id,
            requestId, contexto.Principal?.UserId, motivo, agora,
            candidato.Priority, candidato.RoutingRuleId, candidato.TargetBusinessUnitId, candidato.TargetDepartmentId, contexto.Origin,
            BasisOf(sessao));

        // O item e o recibo da entrada nascem na mesma transação: o identificador nunca vale para dois itens, nem em paralelo.
        var recibo = QueueOperation.Begin(
            contexto.TenantId, requestId, OperationKind.Enter, item.Id, sessao.Id, contexto.Principal?.UserId, candidato.Queue.Id, null, agora);

        try
        {
            await _items.AddItemAsync(item, recibo, cancellationToken);
        }
        catch (QueueItemConflictException)
        {
            // Outra entrada da mesma conversa venceu: na mesma fila é repetição, em outra é conflito.
            var vencedor = await _items.FindActiveBySessionAsync(sessao.Id, cancellationToken);
            if (vencedor is not null && vencedor.QueueId == candidato.Queue.Id)
            {
                return new QueueItemOperationResult(QueueItemView.From(vencedor), OperationOutcome.AlreadyApplied);
            }

            throw;
        }
        catch (OperationIdInUseException)
        {
            return await ResolveRaceAsync(requestId, new Expect(OperationKind.Enter, SessionId: sessao.Id), contexto, cancellationToken);
        }

        return await _executor.DriveAsync(item, cancellationToken);
    }

    private static string RefusalMessage(ContactCenterRule rule) => rule switch
    {
        ContactCenterRule.QueueDoesNotCoverSession => "A fila não atende a unidade ou o setor desta conversa.",
        ContactCenterRule.QueuePaused => "A fila está pausada e não recebe atendimentos novos.",
        ContactCenterRule.QueueClosed => "A fila está fora do horário de atendimento.",
        _ => "Nenhuma regra de roteamento serve esta conversa.",
    };

    // Atribuição

    /// <summary>O atendente assume uma conversa que espera na fila. Só quem está na fila faz isto.</summary>
    /// <exception cref="QueueItemConflictException">Outro atendente é o responsável, ou venceu a disputa.</exception>
    public Task<QueueItemOperationResult> ClaimAsync(Guid itemId, Guid requestId, string? reason = null, CancellationToken cancellationToken = default) =>
        ClaimItemAsync(itemId, requestId, reason, new Expect(OperationKind.Claim, ItemId: itemId), null, cancellationToken);

    /// <param name="fromQueueId">
    /// A fila da qual a candidata foi lida (<see cref="ClaimNextAsync"/>), ou <c>null</c> quando o pedido é por uma conversa específica,
    /// em qualquer fila. A conversa pode ter saído da fila entre a listagem e esta tentativa.
    /// </param>
    private async Task<QueueItemOperationResult> ClaimItemAsync(
        Guid itemId, Guid requestId, string? reason, Expect esperado, Guid? fromQueueId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);

        // O recibo do pedido: já terminou (devolve a resposta dada), ou está pendente (o fluxo abaixo, com as checagens, a conclui).
        var recibo = await FindReceiptAsync(requestId, esperado, contexto, cancellationToken);
        if (recibo is not null && await ReplayOfAsync(recibo, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        // A próxima da fila: a operação pendente deste pedido está em OUTRA conversa (outra tentativa levou esta na frente).
        if (recibo is not null && recibo.ItemId != itemId)
        {
            return await ReplayOrDriveAsync(recibo, cancellationToken);
        }

        var aberto = await OpenAsync(itemId, requestId, cancellationToken);

        // A candidata foi lida da fila pedida, mas a conversa pode ter saído dela (transferida) antes de esta tentativa carregá-la. A
        // conferência vale sobre o item recém-carregado, e a gravação da intenção é versionada: se a conversa sair da fila DEPOIS desta
        // leitura, a versão do item mudou e a gravação falha. Nunca se reserva uma conversa que já não é da fila pedida (nem se confere
        // o acesso do atendente a uma fila que ele não pediu).
        if (fromQueueId is { } filaPedida && aberto.Item.QueueId != filaPedida)
        {
            throw new QueueItemConflictException(QueueItemConflict.ConcurrentChange, "A conversa saiu da fila pedida.");
        }

        var usuario = aberto.Context.RequirePrincipal().UserId;
        var motivo = QueueItem.ValidateReason(reason ?? "claim");
        var operacao = HandlingExecutor.OperationName(PendingKind.Assign);

        if (!aberto.Access.IsMember)
        {
            throw await AuditedAccess.DenyAsync(_audit, operacao, ItemResource, aberto.Item.Id.ToString(), cancellationToken);
        }

        if (await RepeatAsync(aberto, requestId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        // Assumir é para a conversa que ESPERA, e quem diz se ela espera é o Conversations, não a projeção local do item: se ele já tem um
        // responsável, o item (ainda "em espera" aqui) não deixa outro atendente tomá-la. A base guardada na intenção é este estado (esperando).
        var sessao = await ReadEffectiveHandlingAsync(aberto.Item, cancellationToken);
        if (sessao is { Status: SessionStatus.Open, ResponderRef: { } responsavelEfetivo })
        {
            return responsavelEfetivo == usuario
                ? await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.Claim, usuario, esperado, contexto, cancellationToken)
                : throw new QueueItemConflictException(QueueItemConflict.AlreadyAssigned, "Outro atendente já é o responsável por esta conversa.");
        }

        if (aberto.Item.Status == QueueItemStatus.Assigned)
        {
            return aberto.Item.AssignedUserId == usuario
                ? await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.Claim, usuario, esperado, contexto, cancellationToken)
                : throw new QueueItemConflictException(QueueItemConflict.AlreadyAssigned, "Outro atendente já é o responsável por esta conversa.");
        }

        // O limite de conversas simultâneas por atendente, se a fila tem. É um limite do atendente que assume por conta própria: o
        // supervisor que atribui decide pelo atendente e não passa por aqui. A conferência acontece na gravação da intenção, sob a
        // trava do atendente na fila (nunca só antes dela), e conta também as atribuições pendentes.
        var limite = aberto.Queue.MaxItemsPerAgent is { } maximo ? new Capacity(usuario, maximo) : null;
        return await BeginAndDriveAsync(
            aberto, requestId, OperationKind.Claim, aberto.Item.QueueId, usuario, esperado,
            () => aberto.Item.BeginAssign(usuario, requestId, usuario, motivo, _clock.GetUtcNow(), aberto.Context.Origin, BasisOf(sessao)), cancellationToken, limite);
    }

    /// <summary>O limite de conversas que o atendente pode ocupar ao mesmo tempo na fila do item.</summary>
    private sealed record Capacity(Guid UserId, int Maximum);

    /// <summary>
    /// O atendente assume a PRÓXIMA conversa da fila: a mais urgente e, na mesma prioridade, a mais antiga, entre as que esperam sem
    /// operação em andamento. Se outro atendente levar a conversa na frente, tenta a seguinte. Devolve <c>null</c> se a fila não tem
    /// nada esperando. Repetir o mesmo <paramref name="requestId"/> devolve a conversa que ele já assumiu (ou conclui a que ficou
    /// pendente), sem pegar outra.
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">A fila não existe ou não é visível ao usuário.</exception>
    /// <exception cref="QueueAccessDeniedException">O usuário enxerga a fila mas não é atendente dela.</exception>
    /// <exception cref="ContactCenterRuleException">O atendente já tem o máximo de conversas que a fila permite.</exception>
    public async Task<QueueItemOperationResult?> ClaimNextAsync(Guid queueId, Guid requestId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var usuario = contexto.RequirePrincipal().UserId;
        var motivo = QueueItem.ValidateReason(reason ?? "claim");

        // O identificador deste pedido fica preso à fila e ao autor: é a chave de idempotência de UMA conversa assumida. O que ele já
        // fez é devolvido ao autor original (mesmo que ele já não acesse a fila, ou a conversa tenha saído dela); outro autor, ou outra
        // fila, não o reaproveita.
        var esperado = new Expect(OperationKind.Claim, QueueId: queueId);
        var recibo = await FindReceiptAsync(requestId, esperado, contexto, cancellationToken);
        if (recibo is not null && await ReplayOfAsync(recibo, contexto, cancellationToken) is { } jaAssumido)
        {
            return jaAssumido;
        }

        var fila = await _queues.FindQueueReadOnlyAsync(queueId, cancellationToken);
        var acesso = fila is null ? null : await ResolveAccessAsync(contexto, fila, cancellationToken);
        if (fila is null || acesso is null || !acesso.CanSeeQueue)
        {
            throw new ContactCenterResourceNotFoundException("Fila");
        }

        if (!acesso.IsMember)
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.Assign), "service-queue", fila.Id.ToString(), cancellationToken);
        }

        // O mesmo pedido de antes, ainda pendente: o autor, agora com o acesso à fila verificado, conclui o que ficou pendente.
        if (recibo is not null)
        {
            return await ReplayOrDriveAsync(recibo, cancellationToken);
        }

        foreach (var candidato in await _items.ListWaitingAsync(fila.Id, NextCandidates, cancellationToken))
        {
            try
            {
                return await ClaimItemAsync(candidato.Id, requestId, motivo, esperado, fila.Id, cancellationToken);
            }
            catch (QueueItemConflictException ex) when (ex.Conflict is QueueItemConflict.AlreadyAssigned or QueueItemConflict.OperationInProgress
                or QueueItemConflict.ConcurrentChange or QueueItemConflict.ItemClosed)
            {
                // Outro atendente levou esta na frente: tenta a seguinte.
            }
            catch (ContactCenterResourceNotFoundException)
            {
                // A conversa saiu da fila entre a leitura e a tentativa.
            }
        }

        return null;
    }

    private const int NextCandidates = 10;

    /// <summary>
    /// Muda a prioridade do atendimento. Só quem supervisiona a fila decide (a IA e o atendente comum não escolhem a prioridade).
    /// Repetir o mesmo <paramref name="requestId"/> é inofensivo. Fica no histórico, com a prioridade antes e depois.
    /// </summary>
    /// <exception cref="QueueAccessDeniedException">O usuário enxerga o item mas não supervisiona a fila dele.</exception>
    public async Task<QueueItemOperationResult> SetPriorityAsync(Guid itemId, int priority, Guid requestId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var esperado = new Expect(OperationKind.SetPriority, ItemId: itemId);
        if (await ReplayFinishedAsync(requestId, esperado, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        var aberto = await OpenAsync(itemId, requestId, cancellationToken);
        var motivo = QueueItem.ValidateReason(reason ?? "priority-change");

        if (!aberto.Access.IsSupervisor)
        {
            throw await AuditedAccess.DenyAsync(_audit, "contact-center.item.priority", ItemResource, aberto.Item.Id.ToString(), cancellationToken);
        }

        QueueItemHistory? historico;
        try
        {
            historico = aberto.Item.ChangePriority(priority, requestId, aberto.Context.Principal?.UserId, motivo, _clock.GetUtcNow());
        }
        catch (QueueItemStateException ex)
        {
            throw new QueueItemConflictException(QueueItemConflict.ItemClosed, ex.Message);
        }

        if (historico is null)
        {
            return await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.SetPriority, null, esperado, contexto, cancellationToken);
        }

        _items.AddHistory(historico);
        var itemId2 = aberto.Item.Id;
        var visao = QueueItemView.From(aberto.Item);
        _items.AddOperation(QueueOperation.Immediate(
            contexto.TenantId, requestId, OperationKind.SetPriority, itemId2, aberto.Item.SessionId, contexto.Principal?.UserId, aberto.Item.QueueId, null,
            OperationState.Applied, OperationReceipts.Serialize(visao), _clock.GetUtcNow()));

        // A auditoria do sucesso entra na MESMA gravação do item, do histórico e do recibo; a entrega à trilha vem depois da confirmação.
        _audit.Defer(new AuditEntry("contact-center.item.priority", ItemResource, itemId2.ToString(), AuditOutcome.Success));
        try
        {
            await _items.SaveChangesAsync(cancellationToken);
        }
        catch (ContactCenterConflictException)
        {
            return await ResolveConflictAsync(requestId, esperado, itemId2, contexto, cancellationToken);
        }
        catch (OperationIdInUseException)
        {
            return await ResolveRaceAsync(requestId, esperado, contexto, cancellationToken);
        }

        await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        return new QueueItemOperationResult(visao, OperationOutcome.Completed);
    }

    /// <summary>
    /// Passa a conversa para um atendente da fila: o supervisor (ou quem gere as filas) atribui uma conversa em espera, e o
    /// responsável a passa para outro atendente. O destino precisa estar na fila.
    /// </summary>
    public async Task<QueueItemOperationResult> AssignAsync(Guid itemId, Guid userId, Guid requestId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var esperado = new Expect(OperationKind.Assign, ItemId: itemId, CheckTarget: true, TargetUserId: userId);
        if (await ReplayFinishedAsync(requestId, esperado, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        var aberto = await OpenAsync(itemId, requestId, cancellationToken);
        var ator = aberto.Context.Principal?.UserId;
        var motivo = QueueItem.ValidateReason(reason ?? "assign");

        if (!aberto.Access.CanWork(aberto.Item))
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.Assign), ItemResource, aberto.Item.Id.ToString(), cancellationToken);
        }

        if (await RepeatAsync(aberto, requestId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        if (userId == Guid.Empty)
        {
            throw new ContactCenterValidationException("O atendente de destino é obrigatório.");
        }

        // A autorização é sobre o responsável EFETIVO (o do Conversations), não só sobre o que o item ainda mostra.
        var sessao = await ReadEffectiveHandlingAsync(aberto.Item, cancellationToken);
        if (sessao is { Status: SessionStatus.Open })
        {
            if (!aberto.Access.CanWork(sessao.ResponderRef))
            {
                throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.Assign), ItemResource, aberto.Item.Id.ToString(), cancellationToken);
            }

            if (sessao.ResponderRef == userId)
            {
                return await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.Assign, userId, esperado, contexto, cancellationToken);
            }
        }

        if (aberto.Item.Status == QueueItemStatus.Assigned && aberto.Item.AssignedUserId == userId)
        {
            return await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.Assign, userId, esperado, contexto, cancellationToken);
        }

        await RequireMemberAsync(aberto.Queue, userId, aberto.Item.Id, PendingKind.Assign, cancellationToken);
        return await BeginAndDriveAsync(
            aberto, requestId, OperationKind.Assign, aberto.Item.QueueId, userId, esperado,
            () => aberto.Item.BeginAssign(userId, requestId, ator, motivo, _clock.GetUtcNow(), aberto.Context.Origin, BasisOf(sessao)), cancellationToken);
    }

    /// <summary>O responsável (ou o supervisor) devolve a conversa à fila: ela volta a esperar, sem atendente.</summary>
    public async Task<QueueItemOperationResult> ReleaseAsync(Guid itemId, Guid requestId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var esperado = new Expect(OperationKind.Release, ItemId: itemId);
        if (await ReplayFinishedAsync(requestId, esperado, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        var aberto = await OpenAsync(itemId, requestId, cancellationToken);
        var ator = aberto.Context.Principal?.UserId;
        var motivo = QueueItem.ValidateReason(reason ?? "release");

        if (!aberto.Access.CanWork(aberto.Item))
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.Release), ItemResource, aberto.Item.Id.ToString(), cancellationToken);
        }

        if (await RepeatAsync(aberto, requestId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        // A autorização é sobre o responsável EFETIVO (o do Conversations): quem deixou de ser o responsável não tira a conversa de quem é.
        var sessao = await ReadEffectiveHandlingAsync(aberto.Item, cancellationToken);
        if (sessao is { Status: SessionStatus.Open })
        {
            if (!aberto.Access.CanWork(sessao.ResponderRef))
            {
                throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.Release), ItemResource, aberto.Item.Id.ToString(), cancellationToken);
            }

            if (sessao.ResponderRef is null)
            {
                return await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.Release, null, esperado, contexto, cancellationToken);
            }
        }

        if (aberto.Item.Status != QueueItemStatus.Assigned)
        {
            return await RecordNoChangeAsync(aberto.Item, requestId, OperationKind.Release, null, esperado, contexto, cancellationToken);
        }

        return await BeginAndDriveAsync(
            aberto, requestId, OperationKind.Release, aberto.Item.QueueId, null, esperado,
            () => aberto.Item.BeginRelease(requestId, ator, motivo, _clock.GetUtcNow(), aberto.Context.Origin, BasisOf(sessao)), cancellationToken);
    }

    // Transferência entre filas

    /// <summary>
    /// A conversa vai para outra fila da mesma organização, com ou sem atendente de destino. Se a fila de destino é de outra
    /// unidade, a conversa é encaminhada para essa unidade, e o Conversations só aceita se o número pelo qual o cliente fala a
    /// autoriza (ADR 0008): do contrário a operação é recusada e a conversa fica onde estava.
    /// </summary>
    /// <exception cref="ContactCenterRuleException">A fila de destino está pausada, o atendente não está nela ou a unidade não é autorizada.</exception>
    public async Task<QueueItemOperationResult> TransferAsync(
        Guid itemId, Guid targetQueueId, Guid? targetUserId, Guid requestId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var esperado = new Expect(OperationKind.TransferQueue, ItemId: itemId, QueueId: targetQueueId, CheckTarget: true, TargetUserId: targetUserId);
        if (await ReplayFinishedAsync(requestId, esperado, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        var aberto = await OpenAsync(itemId, requestId, cancellationToken);
        var ator = aberto.Context.Principal?.UserId;
        var motivo = QueueItem.ValidateReason(reason ?? "transfer");
        var item = aberto.Item;

        if (!aberto.Access.CanWork(item))
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.TransferQueue), ItemResource, item.Id.ToString(), cancellationToken);
        }

        if (await RepeatAsync(aberto, requestId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        // A autorização é sobre o responsável EFETIVO (o do Conversations), lido antes de qualquer outra regra; a sessão lida é a mesma da base
        // guardada na intenção e do posicionamento abaixo.
        var sessao = await ReadEffectiveHandlingAsync(item, cancellationToken);
        if (sessao is { Status: SessionStatus.Open } && !aberto.Access.CanWork(sessao.ResponderRef))
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.TransferQueue), ItemResource, item.Id.ToString(), cancellationToken);
        }

        // Uma fila de outra organização é inexistente: a conversa só circula entre as filas da organização dela.
        var destino = await _queues.FindQueueReadOnlyAsync(targetQueueId, cancellationToken);
        if (destino is null || destino.OrganizationId != item.OrganizationId)
        {
            throw new ContactCenterResourceNotFoundException("Fila");
        }

        if (destino.Id == item.QueueId)
        {
            throw new QueueItemConflictException(QueueItemConflict.NothingToChange, "A conversa já está nesta fila.");
        }

        if (destino.Status == QueueStatus.Paused)
        {
            throw await RuleAsync(PendingKind.TransferQueue, item.Id, ContactCenterRule.QueuePaused, "A fila de destino está pausada e não recebe atendimentos novos.", cancellationToken, ItemResource);
        }

        if (targetUserId is { } usuarioDestino)
        {
            await RequireMemberAsync(destino, usuarioDestino, item.Id, PendingKind.TransferQueue, cancellationToken);
        }

        if (sessao is null || sessao.Status == SessionStatus.Closed)
        {
            throw await RuleAsync(PendingKind.TransferQueue, item.Id, ContactCenterRule.SessionClosed, "A conversa está encerrada.", cancellationToken, ItemResource);
        }

        var (unidade, setor) = QueueRouter.Placement(destino, sessao);
        return await BeginAndDriveAsync(
            aberto, requestId, OperationKind.TransferQueue, destino.Id, targetUserId, esperado,
            () => item.BeginTransferToQueue(destino.Id, targetUserId, unidade, setor, requestId, ator, motivo, _clock.GetUtcNow(), aberto.Context.Origin, BasisOf(sessao)),
            cancellationToken);
    }

    // Devolução à IA

    /// <summary>O atendimento volta para a IA e o item é encerrado. O responsável ou o supervisor decidem; um atendente comum não devolve a conversa de outro.</summary>
    public async Task<QueueItemOperationResult> ReturnToAiAsync(Guid itemId, Guid requestId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var esperado = new Expect(OperationKind.ReturnToAi, ItemId: itemId);
        if (await ReplayFinishedAsync(requestId, esperado, contexto, cancellationToken) is { } jaRespondido)
        {
            return jaRespondido;
        }

        var aberto = await OpenAsync(itemId, requestId, cancellationToken);
        var ator = aberto.Context.Principal?.UserId;
        var motivo = QueueItem.ValidateReason(reason ?? "return-to-ai");

        if (!aberto.Access.CanWork(aberto.Item))
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.ReturnToAi), ItemResource, aberto.Item.Id.ToString(), cancellationToken);
        }

        if (await RepeatAsync(aberto, requestId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        // A autorização é sobre o responsável EFETIVO (o do Conversations): quem deixou de ser o responsável não interrompe o atendimento de quem é.
        var sessao = await ReadEffectiveHandlingAsync(aberto.Item, cancellationToken);
        if (sessao is { Status: SessionStatus.Open } && !aberto.Access.CanWork(sessao.ResponderRef))
        {
            throw await AuditedAccess.DenyAsync(_audit, HandlingExecutor.OperationName(PendingKind.ReturnToAi), ItemResource, aberto.Item.Id.ToString(), cancellationToken);
        }

        return await BeginAndDriveAsync(
            aberto, requestId, OperationKind.ReturnToAi, aberto.Item.QueueId, null, esperado,
            () => aberto.Item.BeginReturnToAi(requestId, ator, motivo, _clock.GetUtcNow(), aberto.Context.Origin, BasisOf(sessao)), cancellationToken);
    }

    /// <summary>
    /// A resposta do atendente ao cliente (7ª operação do gateway elevado; ADR 0014, seção A). Só o RESPONSÁVEL pela conversa responde (o supervisor opera a fila, mas não fala pelo atendente), e só enquanto
    /// o atendimento humano é o do item. O <paramref name="requestId"/> é a chave de idempotência da ação: repetir o mesmo pedido devolve a mesma mensagem, sem duplicar o envio. A base da autorização (período,
    /// colocação e responsável) é lida no Conversations AGORA e levada ao comando como pré-condição atômica.
    /// <para>
    /// SIMPLIFICAÇÃO DO MVP (registrada no ADR 0018): a ação é DIRETA, sem a intenção persistida nem o recibo do ContactCenter (a retomada por job): se o processo cair entre a decisão e a resposta, o cliente da API
    /// repete o MESMO <paramref name="requestId"/> e o Conversations (idempotente por <c>OperationId</c>) converge. O token de cerca é zero (não há executor).
    /// </para>
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">O item não existe ou não é visível.</exception>
    /// <exception cref="AccessDeniedException">O usuário não é o responsável pela conversa.</exception>
    /// <exception cref="QueueItemConflictException">O atendimento mudou (outro período, modo ou responsável).</exception>
    /// <exception cref="ContactCenterRuleException">A janela de resposta livre do provedor está fechada (<see cref="ContactCenterRule.ReplyWindowClosed"/>): só um modelo aprovado.</exception>
    public Task<RegisterHumanReplyResult> ReplyAsync(Guid itemId, Guid requestId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ContactCenterValidationException("A resposta não pode ser vazia.");
        }

        return ReplyCoreAsync(itemId, requestId, text, templateId: null, parameters: null, attachment: null, cancellationToken);
    }

    /// <summary>
    /// A resposta do atendente por um MODELO de mensagem aprovado (ADR 0027): a única que o provedor aceita fora da janela de resposta livre, mas que também vale dentro dela. Mesmas regras e mesma idempotência da
    /// resposta de texto: só o responsável, base lida AGORA, <paramref name="requestId"/> como chave de efeito. Os parâmetros são validados ANTES de qualquer gravação (faltando, a mais, vazios, longos demais ou com
    /// caractere que a Meta recusa): o atendente vê os erros no formulário, e nada é registrado nem enviado. O texto da mensagem no histórico é o que o cliente lerá, já com os parâmetros.
    /// </summary>
    /// <param name="parameters">Os valores por chave de parâmetro, como <see cref="ReplyTemplateParameterOption.Key"/>.</param>
    /// <exception cref="ReplyTemplateInvalidException">Algum parâmetro é inválido (a lista traz todos).</exception>
    /// <exception cref="ContactCenterRuleException">O modelo não existe para este número ou não está aprovado (<see cref="ContactCenterRule.ReplyTemplateUnavailable"/>).</exception>
    public Task<RegisterHumanReplyResult> ReplyWithTemplateAsync(
        Guid itemId, Guid requestId, Guid templateId, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken = default)
    {
        if (templateId == Guid.Empty)
        {
            throw new ContactCenterValidationException("Escolha o modelo de mensagem.");
        }

        return ReplyCoreAsync(itemId, requestId, text: null, templateId, parameters ?? new Dictionary<string, string>(), attachment: null, cancellationToken);
    }

    /// <summary>
    /// A resposta do atendente com um ARQUIVO (imagem, áudio ou documento; ADR 0031): o MESMO caminho da resposta de texto (mesma autorização, mesma base lida AGORA, <paramref name="requestId"/> como chave de efeito,
    /// mesma gravação atômica da mensagem e do evento de envio no Conversations, mesmo outbox e mesmo Sender). Arquivo é mensagem livre, então vale só com a janela de resposta do provedor aberta; fora dela, só modelo.
    /// O Conversations valida e guarda o arquivo (o tipo REAL pelo conteúdo, o tamanho, o nome e a legenda): a recusa volta em <see cref="RegisterHumanReplyResult.RejectionCode"/>.
    /// </summary>
    /// <exception cref="ContactCenterRuleException">A janela de resposta livre do provedor está fechada (<see cref="ContactCenterRule.ReplyWindowClosed"/>).</exception>
    public Task<RegisterHumanReplyResult> ReplyWithMediaAsync(Guid itemId, Guid requestId, OutboundAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.Content is not { Length: > 0 })
        {
            throw new ContactCenterValidationException("O arquivo está vazio.");
        }

        return ReplyCoreAsync(itemId, requestId, text: null, templateId: null, parameters: null, attachment, cancellationToken);
    }

    /// <summary>
    /// O que o atendente precisa para responder: se o texto livre ainda é aceito (a janela de 24 horas do provedor, contada da última mensagem do CLIENTE) e os modelos aprovados do número. Só enxerga quem enxerga o
    /// item. Sem a consulta do canal, o texto livre vale e não há modelos (o provedor continua sendo a defesa final).
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">O item não existe ou não é visível.</exception>
    public async Task<ReplyOptionsView> GetReplyOptionsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var item = await _items.FindItemReadOnlyAsync(itemId, cancellationToken) ?? throw new ContactCenterResourceNotFoundException("Conversa na fila");
        var acesso = await ResolveAccessAsync(contexto, item, cancellationToken);
        if (!acesso.CanSee(item))
        {
            throw new ContactCenterResourceNotFoundException("Conversa na fila");
        }

        var sessao = await _conversations.GetSessionAsync(item.SessionId, cancellationToken) ?? throw new ContactCenterResourceNotFoundException("Conversa");
        var opcoes = await _channelReply.GetOptionsAsync(sessao.ChannelInstanceId, cancellationToken);
        return BuildReplyOptions(opcoes, sessao, _clock.GetUtcNow());
    }

    private static ReplyOptionsView BuildReplyOptions(ReplyOptions? opcoes, SessionView sessao, DateTimeOffset agora)
    {
        var modelos = opcoes?.Templates ?? Array.Empty<ReplyTemplateOption>();
        if (opcoes?.Window is not { } janela)
        {
            // Sem janela no provedor (ou sem a consulta do canal): o texto livre vale sempre.
            return new ReplyOptionsView(true, true, null, sessao.LastInboundAt, null, modelos);
        }

        // A janela conta da última mensagem do CLIENTE. Cliente que nunca escreveu: nada a responder com texto livre.
        var fecha = sessao.LastInboundAt is { } ultima ? ultima + janela : (DateTimeOffset?)null;
        var aberta = fecha is { } limite && agora < limite;
        return new ReplyOptionsView(aberta, aberta, janela.TotalHours, sessao.LastInboundAt, fecha, modelos);
    }

    private async Task<RegisterHumanReplyResult> ReplyCoreAsync(
        Guid itemId, Guid requestId, string? text, Guid? templateId, IReadOnlyDictionary<string, string>? parameters, OutboundAttachment? attachment, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);

        var item = await _items.FindItemReadOnlyAsync(itemId, cancellationToken) ?? throw new ContactCenterResourceNotFoundException("Conversa na fila");
        var acesso = await ResolveAccessAsync(contexto, item, cancellationToken);
        if (!acesso.CanSee(item))
        {
            throw new ContactCenterResourceNotFoundException("Conversa na fila");
        }

        if (item.IsClosed)
        {
            throw new QueueItemConflictException(QueueItemConflict.ConcurrentChange, "A conversa já foi encerrada.");
        }

        if (contexto.Principal?.UserId is not { } autor || item.Status != QueueItemStatus.Assigned || item.AssignedUserId != autor)
        {
            throw await AuditedAccess.DenyAsync(_audit, "contact-center.item.reply", ItemResource, item.Id.ToString(), cancellationToken);
        }

        var sessao = await ReadEffectiveHandlingAsync(item, cancellationToken);
        if (sessao is not { Status: SessionStatus.Open, HandlingMode: HandlingMode.HumanAssisted } || sessao.ResponderRef != autor)
        {
            throw new QueueItemConflictException(QueueItemConflict.ConcurrentChange, "A conversa não está mais sob o seu atendimento. Releia e tente de novo.");
        }

        // O conteúdo, decidido ANTES de gravar qualquer coisa: texto livre só com a janela do provedor aberta; modelo só aprovado, da conta do número e com os parâmetros válidos.
        var conteudo = await BuildReplyContentAsync(sessao, text, templateId, parameters, attachment, cancellationToken);

        var precondicao = new ReplyPrecondition(sessao.HandlingPeriod, sessao.PlacementEpoch, ResponderMatch.Exact, autor);
        var resultado = await _conversations.RegisterHumanReplyAsync(
            new RegisterHumanReplyCommand(sessao.Id, requestId, autor, precondicao, sessao.StateVersion, 0, conteudo), cancellationToken);

        // A versão do estado mudou e a pré-condição ainda vale: relê a versão e repete com a MESMA pré-condição (uma vez). Se o atendimento mudou de verdade, a repetição devolve HandlingChanged.
        if (resultado.Outcome == RegisterHumanReplyOutcome.StaleState && await _conversations.GetSessionAsync(sessao.Id, cancellationToken) is { } atual)
        {
            resultado = await _conversations.RegisterHumanReplyAsync(
                new RegisterHumanReplyCommand(sessao.Id, requestId, autor, precondicao, atual.StateVersion, 0, conteudo), cancellationToken);
        }

        // A nova resposta é a RECUPERAÇÃO de uma falha de envio (SND-7): o atendente já agiu, então o sinal sai da fila. Sinal de monitoramento: não muda o item nem avança a versão.
        if (resultado.Outcome is RegisterHumanReplyOutcome.Applied or RegisterHumanReplyOutcome.AlreadyApplied && item.DeliveryFailures > 0)
        {
            await _items.ClearDeliveryFailuresAsync(item.Id, cancellationToken);
        }

        return resultado;
    }

    private async Task<ReplyContent> BuildReplyContentAsync(
        SessionView sessao, string? text, Guid? templateId, IReadOnlyDictionary<string, string>? parameters, OutboundAttachment? attachment, CancellationToken cancellationToken)
    {
        if (templateId is not { } modelo)
        {
            var opcoes = await _channelReply.GetOptionsAsync(sessao.ChannelInstanceId, cancellationToken);
            var vista = BuildReplyOptions(opcoes, sessao, _clock.GetUtcNow());
            if (!vista.FreeTextAllowed)
            {
                throw new ContactCenterRuleException(
                    ContactCenterRule.ReplyWindowClosed,
                    $"A janela de {vista.WindowHours:0.##} horas para responder livremente a este cliente está fechada. Envie um modelo de mensagem aprovado.");
            }

            // O arquivo vai sem texto: a legenda mora nele (o texto da mensagem fica vazio e nunca é lido pela IA).
            return attachment is not null ? new ReplyContent(string.Empty, Attachment: attachment) : new ReplyContent(text!);
        }

        var preparo = await _channelReply.PrepareTemplateAsync(sessao.ChannelInstanceId, modelo, parameters ?? new Dictionary<string, string>(), cancellationToken);
        if (preparo.FailureCode is not null)
        {
            throw new ContactCenterRuleException(ContactCenterRule.ReplyTemplateUnavailable, "O modelo de mensagem não está disponível para este número (não existe ou não está aprovado).");
        }

        if (preparo.Errors.Count > 0)
        {
            throw new ReplyTemplateInvalidException(preparo.Errors);
        }

        if (!preparo.IsReady)
        {
            throw new ContactCenterRuleException(ContactCenterRule.ReplyTemplateUnavailable, "O modelo de mensagem não pôde ser preparado.");
        }

        return new ReplyContent(preparo.PreviewText!, null, preparo.ChannelData);
    }

    /// <summary>
    /// O encerramento do atendimento por quem o conduz (9ª operação do gateway elevado; ADR 0016, seção M): o responsável pela conversa ou quem supervisiona a fila. A base é a mesma da resposta, e a
    /// sessão encerrada converge em <see cref="CloseSessionDelegatedOutcome.AlreadyClosed"/>. O item é concluído pela projeção do <c>SessionClosedV1</c> (o Worker), como em qualquer encerramento. Mesma
    /// simplificação do MVP da resposta: ação direta, idempotente pelo estado, sem intenção persistida.
    /// </summary>
    /// <exception cref="ContactCenterResourceNotFoundException">O item não existe ou não é visível.</exception>
    /// <exception cref="AccessDeniedException">O usuário não opera esta conversa.</exception>
    /// <exception cref="QueueItemConflictException">O atendimento mudou (outro período, modo ou responsável).</exception>
    public async Task<CloseSessionDelegatedResult> CloseAsync(Guid itemId, Guid requestId, string? reasonCode, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);
        var motivo = QueueItem.ValidateReason(reasonCode ?? "resolved");

        var item = await _items.FindItemReadOnlyAsync(itemId, cancellationToken) ?? throw new ContactCenterResourceNotFoundException("Conversa na fila");
        var acesso = await ResolveAccessAsync(contexto, item, cancellationToken);
        if (!acesso.CanSee(item))
        {
            throw new ContactCenterResourceNotFoundException("Conversa na fila");
        }

        // O item já foi concluído (pela projeção do encerramento anterior; quem o via continua vendo): repetir a ação converge, sem erro e sem nenhuma chamada ao Conversations. Concluído, ele já não tem responsável.
        if (item.IsClosed)
        {
            return new CloseSessionDelegatedResult(CloseSessionDelegatedOutcome.AlreadyClosed, Reason: "closed");
        }

        if (!acesso.CanWork(item))
        {
            throw await AuditedAccess.DenyAsync(_audit, "contact-center.item.close", ItemResource, item.Id.ToString(), cancellationToken);
        }

        var sessao = await ReadEffectiveHandlingAsync(item, cancellationToken);
        if (sessao is null)
        {
            throw new ContactCenterResourceNotFoundException("Conversa");
        }

        if (sessao.Status == SessionStatus.Closed)
        {
            return new CloseSessionDelegatedResult(CloseSessionDelegatedOutcome.AlreadyClosed, LastMessageSeq: sessao.LastMessageSeq, Reason: "closed");
        }

        if (sessao.HandlingMode != HandlingMode.HumanAssisted || !acesso.CanWork(sessao.ResponderRef))
        {
            throw new QueueItemConflictException(QueueItemConflict.ConcurrentChange, "A conversa não está mais sob o seu atendimento. Releia e tente de novo.");
        }

        var precondicao = HandlingExecutor.CloseBasis(sessao);
        var resultado = await _conversations.CloseSessionAsync(
            new CloseSessionDelegatedCommand(sessao.Id, requestId, contexto.Principal?.UserId, contexto.Origin, precondicao, sessao.StateVersion, 0, motivo), cancellationToken);
        if (resultado.Outcome == CloseSessionDelegatedOutcome.StaleState && await _conversations.GetSessionAsync(sessao.Id, cancellationToken) is { } atual)
        {
            resultado = await _conversations.CloseSessionAsync(
                new CloseSessionDelegatedCommand(sessao.Id, requestId, contexto.Principal?.UserId, contexto.Origin, precondicao, atual.StateVersion, 0, motivo), cancellationToken);
        }

        return resultado;
    }

    /// <summary>
    /// O atendimento da conversa como o Conversations o vê AGORA. É a autoridade sobre o responsável atual (BOUNDARIES.md §6), e as operações
    /// que mudam o atendimento autorizam sobre ele, não só sobre o que a projeção local do item ainda mostra. Uma sessão em atendimento humano
    /// que já não é a deste item (outro período, fora do atendimento humano, outra unidade ou setor: ver <see cref="IsSamePeriod"/>) fecha a
    /// operação com conflito, sem nada alterado. Sessão encerrada ou inexistente é devolvida como está: não há atendimento a proteger, e o
    /// executor encerra o item sem gravar base alguma.
    /// </summary>
    /// <exception cref="QueueItemConflictException">A sessão não está mais no atendimento que este item representa.</exception>
    private async Task<SessionView?> ReadEffectiveHandlingAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var sessao = await _conversations.GetSessionAsync(item.SessionId, cancellationToken);
        if (sessao is { Status: SessionStatus.Open } && !IsSamePeriod(item, sessao))
        {
            throw new QueueItemConflictException(
                QueueItemConflict.ConcurrentChange, "O atendimento desta conversa mudou (outro período, outro modo ou outra unidade). Releia e tente de novo.");
        }

        return sessao;
    }

    /// <summary>
    /// A base da autorização que vai na intenção: o modo, o período e o responsável EFETIVOS lidos no Conversations (<see cref="HandlingBasis"/>),
    /// que o executor leva a cada comando como pré-condição. Sem sessão em atendimento (encerrada ou inexistente), não há base. A entrada parte da
    /// IA: a base dela diz "em IA", sem responsável.
    /// </summary>
    private static HandlingBasis? BasisOf(SessionView? sessao) =>
        sessao is { Status: SessionStatus.Open }
            ? new HandlingBasis(sessao.HandlingPeriod, sessao.ResponderRef, sessao.HandlingMode != HandlingMode.HumanAssisted, sessao.PlacementEpoch)
            : null;

    // Reconciliação

    /// <summary>
    /// Conclui as operações que ficaram pendentes (o Conversations estava fora do ar, ou o processo caiu entre a intenção e a
    /// conclusão). Só o contexto de sistema (o job) chama. Devolve quantas concluiu. Cada conclusão é idempotente: se o usuário
    /// repetir o pedido na mesma hora, uma das duas tentativas vira repetição.
    /// </summary>
    /// <param name="minimumAge">Só retoma o que está pendente há pelo menos este tempo, para não disputar com a requisição que acabou de gravar a intenção.</param>
    public async Task<int> ReconcilePendingAsync(TimeSpan minimumAge, int take, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsSystem)
        {
            throw new AccessDeniedException(Permission.SystemOperation);
        }

        if (take < 1 || take > MaxPageSize)
        {
            throw new ContactCenterValidationException($"O tamanho do lote deve ser de 1 a {MaxPageSize}.");
        }

        var agora = _clock.GetUtcNow();
        var pendentes = await _items.ListPendingAsync(agora - minimumAge, agora, take, cancellationToken);
        var concluidas = 0;
        foreach (var leitura in pendentes)
        {
            var item = await _items.FindItemAsync(leitura.Id, cancellationToken);
            if (item is null || !item.HasPending)
            {
                continue;
            }

            try
            {
                var resultado = await _executor.DriveAsync(item, cancellationToken);
                if (resultado.Outcome != OperationOutcome.Pending)
                {
                    concluidas++;
                }
            }
            catch (Exception ex) when (ex is ContactCenterRuleException or ContactCenterConflictException)
            {
                // A operação foi desfeita (destino recusado) ou outra tentativa a concluiu: não há mais o que retomar.
            }
        }

        // Os itens que ficaram ATRÁS do Conversations: um evento externo mais novo chegou com a operação deles pendente (a projeção foi adiada, de forma
        // durável) e a operação já terminou. Incluem os que acabaram de ser concluídos ou desfeitos acima. A sessão é a autoridade; o item se alinha a ela.
        foreach (var atrasado in await _items.ListStaleProjectionsAsync(take, cancellationToken))
        {
            var item = await _items.FindItemAsync(atrasado.Id, cancellationToken);
            if (item is null || !item.NeedsResync)
            {
                continue;
            }

            try
            {
                if (await _executor.ResyncAsync(item, cancellationToken))
                {
                    concluidas++;
                }
            }
            catch (ContactCenterConflictException)
            {
                // Outra operação mexeu no item ao mesmo tempo: a próxima rodada relê.
            }
        }

        return concluidas;
    }

    // Leitura

    /// <summary>Um item que o usuário não enxerga devolve <c>null</c> (TEN-2).</summary>
    public async Task<QueueItemView?> GetAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var item = await _items.FindItemReadOnlyAsync(itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var acesso = await ResolveAccessAsync(contexto, item, cancellationToken);
        return acesso.CanSee(item) ? QueueItemView.From(item) : null;
    }

    /// <summary>Os itens que o usuário enxerga, os mais antigos primeiro. Fora do alcance dele, nada aparece.</summary>
    /// <exception cref="AccessDeniedException">O usuário não lê filas nem é atendente de nenhuma.</exception>
    public async Task<IReadOnlyList<QueueItemView>> ListAsync(Guid? queueId, QueueItemStatus? status, bool assignedToMe, int take, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (take < 1 || take > MaxPageSize)
        {
            throw new ContactCenterValidationException($"O tamanho da página deve ser de 1 a {MaxPageSize}.");
        }

        if (status is { } s && !Enum.IsDefined(s))
        {
            throw new ContactCenterValidationException("A situação do item é inválida.");
        }

        var organizacoes = contexto.OrganizationsWithPermission(Permission.QueueRead);
        var usuario = contexto.Principal?.UserId;
        var participacoes = organizacoes is not null && usuario is { } id
            ? (await _queues.ListMembershipsOfUserAsync(id, cancellationToken)).Select(m => m.QueueId).ToList()
            : new List<Guid>();

        // Sessão embutida (ADR 0039): só as filas da organização da conexão, e nenhuma no modo CustomerOnly. A participação em fila é acesso fora das concessões, então o limite é aplicado aqui.
        if (contexto.Principal?.Embedded is not null && participacoes.Count > 0)
        {
            var dentro = (await _queues.ListQueuesOfMemberAsync(usuario!.Value, cancellationToken))
                .Where(f => !contexto.IsOutsideEmbeddedScope(f.OrganizationId))
                .Select(f => f.Id)
                .ToHashSet();
            participacoes = participacoes.Where(dentro.Contains).ToList();
        }

        if (organizacoes is { Count: 0 } && participacoes.Count == 0)
        {
            throw new AccessDeniedException(Permission.QueueRead);
        }

        var itens = await _items.ListItemsAsync(
            new QueueItemScope(organizacoes, participacoes, usuario),
            new QueueItemFilter(queueId, status, assignedToMe, take),
            cancellationToken);
        return itens.Select(QueueItemView.From).ToList();
    }

    /// <summary>O histórico de atribuições e transferências do item, do mais antigo para o mais novo. Item invisível devolve <c>null</c>.</summary>
    public async Task<IReadOnlyList<QueueItemHistoryView>?> GetHistoryAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var item = await _items.FindItemReadOnlyAsync(itemId, cancellationToken);
        if (item is null || !(await ResolveAccessAsync(contexto, item, cancellationToken)).CanSee(item))
        {
            return null;
        }

        var historico = await _items.ListHistoryAsync(item.Id, cancellationToken);
        return historico.Select(QueueItemHistoryView.From).ToList();
    }

    /// <summary>
    /// A sessão da conversa do item. Sem acesso ao conteúdo, a negação é auditada; sem ver o item, devolve <c>null</c>. O estado da
    /// sessão é o de AGORA, que só é do item enquanto ele está ativo: o item encerrado (a conversa voltou para a IA, ou foi para outra
    /// fila ou unidade) devolve <c>null</c>.
    /// </summary>
    public async Task<SessionView?> GetConversationAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (await OpenReadAsync(itemId, cancellationToken) is not { } leitura)
        {
            return null;
        }

        // O item ainda está ativo AQUI, mas quem manda no estado da conversa é o Conversations: se a sessão já não está no período de
        // atendimento que este item autoriza (a conversa voltou para a IA, e talvez já seja outro atendimento humano, ou ela mudou de
        // unidade; o evento ainda não foi projetado), a conversa já não é deste atendimento.
        var (item, sessao) = (leitura.Item, leitura.Session);
        if (item.IsClosed || sessao is null || sessao.Status != SessionStatus.Open || !IsSamePeriod(item, sessao))
        {
            return null;
        }

        // O que decide o acesso não pode ter mudado entre a autorização e a resposta (uma atribuição ou transferência concorrente).
        return await StillAuthorizedAsync(item, sessao, cancellationToken) ? sessao : null;
    }

    /// <summary>
    /// A sessão continua no MESMO período de atendimento que o acesso deste item autoriza? É a autoridade do Conversations sobre o estado:
    /// o item pode ainda estar ativo por atraso da projeção (o evento do fim não chegou), e nesse intervalo o acesso à conversa não pode ir
    /// além do que o atendimento viu. Só "a sessão está em atendimento humano" NÃO basta: humano → IA → humano é outro período (e, para
    /// quem foi autorizado pelo primeiro, o intervalo da IA e o segundo atendimento são conteúdo alheio). O acesso prende-se a:
    /// (1) a identidade do período de atendimento humano que o Conversations deu à entrada do item (<see cref="QueueItem.HandlingPeriod"/>,
    /// desconhecida fecha); (2) o modo humano agora; (3) a mesma unidade e o mesmo setor do item (uma transferência de unidade, concorrente
    /// ou feita direto no Conversations, tira a conversa de quem a lê por esta fila). Qualquer divergência FECHA o acesso.
    /// </summary>
    private static bool IsSamePeriod(QueueItem item, SessionView sessao) =>
        IsSameAttendance(item, sessao) && IsSamePlacement(item, sessao);

    /// <summary>
    /// O atendimento humano que o item representa continua, sem interrupção: a sessão está em atendimento humano agora e no MESMO período que o item guardou. O
    /// período desconhecido (item anterior ao campo) NUNCA é correspondência: sem a identidade, nada prova a continuidade, e o acesso fica fechado.
    /// </summary>
    private static bool IsSameAttendance(QueueItem item, SessionView sessao) =>
        ScopeProof.AttendanceOfSession(item.HandlingPeriod, sessao.HandlingMode == HandlingMode.HumanAssisted, sessao.HandlingPeriod) == ProofVerdict.Same;

    /// <summary>
    /// A sessão está na MESMA colocação que o item guardou, e é a mesma colocação CONTÍNUA: a unidade e o setor são os do item e a geração da colocação (a
    /// identidade durável que o Conversations avança, na mesma transação, a cada mudança de unidade ou setor) é a que o item prendeu. A geração distingue
    /// uma sessão que ficou continuamente na unidade de uma que foi para outra e VOLTOU (A → B → A): a unidade, o setor e o período são de novo os do item, e
    /// a geração não é. Não depende de nenhum evento ter chegado ao ContactCenter. Geração desconhecida (item anterior ao campo) FECHA.
    /// </summary>
    private static bool IsSamePlacement(QueueItem item, SessionView sessao) =>
        PlacementOf(item, sessao) == ProofVerdict.Same;

    private static ProofVerdict PlacementOf(QueueItem item, SessionView sessao) =>
        ScopeProof.PlacementOfSession(item.PlacementEpoch, item.BusinessUnitId, item.DepartmentId, sessao.PlacementEpoch, sessao.BusinessUnitId, sessao.DepartmentId);

    private static Page<MessageView> ClosedPage() => new(Array.Empty<MessageView>(), null);

    /// <summary>A sessão continua na MESMA geração de colocação que autorizou a leitura (nenhuma mudança de unidade ou setor durante a busca do conteúdo)?</summary>
    private async Task<bool> PlacementUnchangedAsync(SessionView autorizada, Guid sessionId, CancellationToken cancellationToken) =>
        await _conversations.GetSessionAsync(sessionId, cancellationToken) is { } agora && agora.PlacementEpoch == autorizada.PlacementEpoch;

    /// <summary>
    /// A autorização de leitura foi dada sobre o item como estava ANTES da consulta; ela vale para o que foi lido só se nada do que decide
    /// o acesso mudou desde então (<see cref="ReadAuthority"/>: fila, responsável, operação pendente, unidade e setor, período,
    /// fronteira) e o usuário ainda pode lê-lo. Se algo mudou durante a consulta (uma atribuição ou transferência concorrente, mesmo a
    /// que só chegou à intenção, ou a perda do acesso), o que foi lido não é devolvido: o acesso fecha.
    /// </summary>
    private async Task<Page<MessageView>> StillAuthorizedAsync(QueueItem lido, SessionView? sessao, Page<MessageView> pagina, CancellationToken cancellationToken) =>
        await StillAuthorizedAsync(lido, sessao, cancellationToken) ? pagina : ClosedPage();

    private async Task<bool> StillAuthorizedAsync(QueueItem lido, SessionView? sessao, CancellationToken cancellationToken)
    {
        var agora = await _items.FindItemReadOnlyAsync(lido.Id, cancellationToken);
        if (agora is null || ReadAuthority.Of(agora) != ReadAuthority.Of(lido))
        {
            return false;
        }

        // O responsável é o da MESMA resposta do Conversations que limitou a leitura: o que foi lido chegou até ela, e o que veio depois
        // (uma mudança de responsável confirmada ali durante a consulta) não está no que foi lido.
        return await EvaluateReadAsync(_context.GetRequired(), agora, sessao, cancellationToken) == ReadVerdict.Allowed;
    }

    /// <summary>
    /// O que decide o acesso de leitura de um item, para comparar o item de antes com o de depois da consulta: igual, nada mudou no que
    /// autoriza; diferente, o que foi lido foi autorizado sobre outro estado.
    /// </summary>
    private sealed record ReadAuthority(
        Guid QueueId, bool IsClosed, QueueItemStatus Status, Guid? AssignedUserId, Guid? PendingOperationId, long? HandlingPeriod,
        long? ClosedThroughMessageSeq, Guid? BusinessUnitId, Guid? DepartmentId, long? HistoryLimitSeq, long? PlacementEpoch, long? HistoryLimitOriginEpoch, short BoundaryModel)
    {
        public static ReadAuthority Of(QueueItem item) =>
            new(item.QueueId, item.IsClosed, item.Status, item.AssignedUserId, item.Pending?.OperationId, item.HandlingPeriod,
                item.ClosedThroughMessageSeq, item.BusinessUnitId, item.DepartmentId, item.HistoryLimitSeq, item.PlacementEpoch, item.HistoryLimitOriginEpoch, item.BoundaryModel);
    }

    /// <summary>O item e, se ele está ativo, a sessão como o Conversations a viu na MESMA leitura que autorizou o acesso.</summary>
    private sealed record ReadLease(QueueItem Item, SessionView? Session);

    private enum ReadVerdict
    {
        Allowed,
        NotVisible,
        Denied,
    }

    /// <summary>
    /// Autoriza a leitura do conteúdo da conversa sobre a AUTORIDADE EFETIVA, não sobre a projeção local do item. O período
    /// (<see cref="QueueItem.HandlingPeriod"/>) diz de QUE atendimento é o acesso; esta regra diz QUEM pode lê-lo, e as duas valem juntas.
    /// <list type="bullet">
    /// <item><description>
    /// <b>O responsável é o do Conversations</b> (BOUNDARIES.md §6): enquanto o item está ativo, quem responde pela conversa é
    /// <see cref="SessionView.ResponderRef"/>, e não <see cref="QueueItem.AssignedUserId"/>. Uma atribuição, reatribuição ou devolução à fila
    /// que o Conversations já confirmou, mas que o ContactCenter ainda não concluiu (a resposta se perdeu, o processo caiu), vale desde a
    /// confirmação: o responsável anterior perde o acesso, e os atendentes da fila não leem pela "espera" de uma conversa que já tem dono.
    /// </description></item>
    /// <item><description>
    /// <b>A fila em transferência pendente é ambígua</b>: o Conversations não conhece filas, então não há como saber se a transferência
    /// já valeu. Enquanto ela está pendente, a leitura exige acesso nas DUAS filas (a de origem e a de destino): ninguém lê por uma fila
    /// que a conversa pode já ter deixado, e ninguém lê por uma fila que ainda não é dela. Concluída, vale só a fila do item.
    /// </description></item>
    /// <item><description>
    /// Item encerrado não tem responsável a consultar (a sessão seguiu adiante): vale o que o item guardou ao encerrar.
    /// </description></item>
    /// </list>
    /// Quem não enxerga alguma das filas responde como inexistente; quem enxerga e não pode ler é negado (e o chamador audita).
    /// </summary>
    private async Task<ReadVerdict> EvaluateReadAsync(RequestContext contexto, QueueItem item, SessionView? sessao, CancellationToken cancellationToken)
    {
        var (situacao, responsavel) = item.IsClosed || sessao is null
            ? (item.Status, item.AssignedUserId)
            : (sessao.ResponderRef is null ? QueueItemStatus.Waiting : QueueItemStatus.Assigned, sessao.ResponderRef);

        var acessos = new List<QueueAccess> { await ResolveAccessAsync(contexto, item, cancellationToken) };

        // A fila do item atende o lugar onde o item diz que a conversa está (organização, unidade e setor)? Uma fila de outra unidade não autoriza
        // a leitura de uma conversa que está em outra: o que o item guardou fora da cobertura da fila (um estado intermediário de uma transferência,
        // ou um registro incoerente) fecha o acesso até ser resolvido, nunca o abre. Vale também para o item encerrado.
        if (!acessos[0].Covers(item))
        {
            return ReadVerdict.NotVisible;
        }

        if (!item.IsClosed && item.Pending is { Kind: PendingKind.TransferQueue } transferencia && transferencia.QueueId != item.QueueId)
        {
            // A fila de destino que já não existe não autoriza nada: o acesso fecha.
            if (await _queues.FindQueueReadOnlyAsync(transferencia.QueueId, cancellationToken) is not { } destino)
            {
                return ReadVerdict.NotVisible;
            }

            acessos.Add(await ResolveAccessAsync(contexto, destino, cancellationToken));
        }

        if (acessos.Any(a => !a.CanSee(item)))
        {
            return ReadVerdict.NotVisible;
        }

        return acessos.All(a => a.CanReadConversation(situacao, responsavel)) ? ReadVerdict.Allowed : ReadVerdict.Denied;
    }

    /// <summary>
    /// Abre o item para leitura do conteúdo: quem não enxerga a fila dele recebe "inexistente" antes de qualquer consulta ao Conversations;
    /// para o item ativo, o Conversations dá o estado e o responsável, e a autorização vale sobre eles (<see cref="EvaluateReadAsync"/>). A
    /// negação é auditada.
    /// </summary>
    private async Task<ReadLease?> OpenReadAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var item = await _items.FindItemReadOnlyAsync(itemId, cancellationToken);
        if (item is null || !(await ResolveAccessAsync(contexto, item, cancellationToken)).CanSee(item))
        {
            return null;
        }

        var sessao = item.IsClosed ? null : await _conversations.GetSessionAsync(item.SessionId, cancellationToken);
        switch (await EvaluateReadAsync(contexto, item, sessao, cancellationToken))
        {
            case ReadVerdict.NotVisible:
                return null;
            case ReadVerdict.Denied:
                throw await AuditedAccess.DenyAsync(_audit, "contact-center.item.read-conversation", ItemResource, item.Id.ToString(), cancellationToken);
            default:
                return new ReadLease(item, sessao);
        }
    }

    /// <summary>As mensagens da conversa do item, paginadas. As mesmas regras de acesso de <see cref="GetConversationAsync"/>.</summary>
    public Task<Page<MessageView>?> GetMessagesAsync(Guid itemId, int take, string? cursor, HistoryOrder order, CancellationToken cancellationToken = default) =>
        GetMessagesCoreAsync(itemId, take, cursor, order, null, cancellationToken);

    /// <summary>
    /// O arquivo (imagem, áudio ou documento) de UMA mensagem da conversa do item (ADR 0028), ou nulo. Passa pelas MESMAS regras do histórico (acesso ao item, período, colocação, fronteira do atendimento): só abre o
    /// arquivo de uma mensagem que o histórico mostraria a este usuário agora, e só se o arquivo estiver disponível. O conteúdo é lido do armazenamento privado pelo Conversations; nenhuma URL é criada.
    /// </summary>
    public async Task<AttachmentContent?> OpenAttachmentAsync(Guid itemId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var pagina = await GetMessagesCoreAsync(itemId, 1, null, HistoryOrder.OldestFirst, messageId, cancellationToken);
        var mensagem = pagina?.Items.FirstOrDefault(m => m.Id == messageId);
        if (mensagem?.Attachment is not { Status: AttachmentStatus.Available })
        {
            return null;
        }

        return await _attachments.OpenAsync(mensagem.SessionId, messageId, cancellationToken);
    }

    private async Task<Page<MessageView>?> GetMessagesCoreAsync(Guid itemId, int take, string? cursor, HistoryOrder order, Guid? messageId, CancellationToken cancellationToken)
    {
        // Parâmetros do cliente são validados aqui, antes de qualquer leitura: parâmetro inválido é erro do pedido (400).
        if (take < 1 || take > MaxPageSize)
        {
            throw new ContactCenterValidationException($"O tamanho da página deve ser de 1 a {MaxPageSize}.");
        }

        if (!Enum.IsDefined(order))
        {
            throw new ContactCenterValidationException("A ordem do histórico é inválida.");
        }

        if (await OpenReadAsync(itemId, cancellationToken) is not { } leitura)
        {
            return null;
        }

        var (item, sessao) = (leitura.Item, leitura.Session);

        // Item ativo: a conversa inteira (o contexto anterior à entrada na fila faz parte do atendimento). Item encerrado: só o período
        // dele, até a fronteira que o Conversations deu ao encerrar (a sequência da última mensagem naquele instante, que não depende de
        // relógio nem de quando este módulo processou o fim). O corte vale na própria consulta, antes da paginação: as páginas e os
        // cursores seguem a ordem de exibição, e nenhuma mensagem do período some nem uma de fora entra. Sem fronteira conhecida, o
        // período fica fechado (nenhuma mensagem), nunca aberto.
        if (!item.IsClosed)
        {
            // Item ativo AQUI: a autoridade sobre o estado é o Conversations, e o fim do atendimento pode já ter acontecido lá, sem a
            // projeção ter chegado (a janela entre os commits dos dois módulos, que uma queda prolonga), ou a conversa pode ter voltado ao
            // atendimento humano em OUTRO período. Pergunta-se a ele: se a sessão não está no período que este item autoriza (mesma
            // identidade de período, modo humano, mesma unidade e setor), o período fica fechado até a fronteira chegar. Estando, a
            // leitura é limitada à sequência vista NESSA resposta: o que chegou depois dela (uma devolução à IA e as mensagens que se
            // seguem, durante esta mesma consulta) não é lido. Sessão encerrada no mesmo período: nada chega depois, e a sequência final
            // é a fronteira. A sessão é a que já autorizou o acesso (quem responde por ela, nesta mesma leitura).
            if (sessao is null)
            {
                return null;
            }

            // A sessão está na MESMA colocação CONTÍNUA do item (a geração durável, que o Conversations avança a cada mudança de unidade ou setor, é a
            // que o item prendeu: uma sessão que foi para outra unidade e voltou não casa), no mesmo atendimento humano? Então a leitura vai até a
            // sequência vista nesta resposta (e até o limite que o item guardou ao ver uma saída, se houve). Se a sessão saiu da colocação (e pode ter
            // voltado, em outra geração), o que o atendimento pôde ver é só o que veio ATÉ a saída: só se lê isso, e só se a fronteira da saída foi
            // demonstrada (o limite que o item projetou); sem ela, fechado. Nunca se reabre porque a sessão voltou, e nada disto depende de o evento
            // do encaminhamento ter chegado antes desta consulta: sem ele, a divergência de geração fecha.
            var continua = IsSamePeriod(item, sessao);
            long ate;
            if (continua)
            {
                ate = sessao.LastMessageSeq;
                if (item.HistoryLimitSeq is { } limite)
                {
                    if (limite <= 0)
                    {
                        return ClosedPage();
                    }

                    ate = Math.Min(ate, limite);
                }
            }
            else if (IsSameAttendance(item, sessao) && PlacementOf(item, sessao) == ProofVerdict.Other && item.HistoryLimitIsValid && item.HistoryLimitSeq is > 0 and var limiteDaSaida)
            {
                // A sessão SAIU da geração do item (Other: a geração dela é conhecida e é outra). O limite só autoriza conteúdo com ORIGEM comprovada (certificado e
                // da geração do item): o número guardado sem origem, ou com a geração do item desconhecida (Unknown), não autoriza nada.
                ate = Math.Min(sessao.LastMessageSeq, limiteDaSaida);
            }
            else
            {
                return ClosedPage();
            }

            var pagina = await _conversations.GetHistoryAsync(new GetHistoryQuery(item.SessionId, take, cursor, order, ate, messageId), cancellationToken);
            if (pagina is null)
            {
                return null;
            }

            // Revalidação da colocação DEPOIS da busca: a leitura foi autorizada sobre a colocação que a sessão tinha naquela resposta, e a colocação pode ter
            // mudado enquanto se buscava o conteúdo (uma transferência, e a volta). Mudou, nada é devolvido. (A busca já é limitada pela sequência vista,
            // então nenhuma mensagem posterior à autorização entra; isto fecha também o que a página tinha de legítimo, por conservadorismo.)
            if (continua && !await PlacementUnchangedAsync(sessao, item.SessionId, cancellationToken))
            {
                return ClosedPage();
            }

            return await StillAuthorizedAsync(item, sessao, pagina, cancellationToken);
        }

        // Item encerrado: a fronteira só autoriza conteúdo se foi CERTIFICADA pelo caminho que o encerrou (o período e a geração do item comprovados no fechamento,
        // vínculo imutável depois). Registro anterior ao modelo certificado preserva o número como metadado e nega o conteúdo.
        if (item.ClosedThroughMessageSeq is not { } fronteira || !item.ClosedBoundaryIsCertified)
        {
            return ClosedPage();
        }

        var doPeriodo = await _conversations.GetHistoryAsync(new GetHistoryQuery(item.SessionId, take, cursor, order, fronteira, messageId), cancellationToken);
        return doPeriodo is null ? null : await StillAuthorizedAsync(item, null, doPeriodo, cancellationToken);
    }

    // Peças comuns

    private sealed record Opened(RequestContext Context, QueueItem Item, ServiceQueue Queue, QueueAccess Access);

    /// <summary>Carrega o item (rastreado) e o acesso do usuário a ele. O que o usuário não enxerga responde como inexistente.</summary>
    private async Task<Opened> OpenAsync(Guid itemId, Guid requestId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        RequireId(requestId);

        var item = await _items.FindItemAsync(itemId, cancellationToken);
        if (item is null)
        {
            throw new ContactCenterResourceNotFoundException("Conversa na fila");
        }

        var fila = await _queues.FindQueueReadOnlyAsync(item.QueueId, cancellationToken)
            ?? throw new InvalidOperationException("O item aponta para uma fila que não existe.");
        var acesso = await ResolveAccessAsync(contexto, fila, cancellationToken);
        if (!acesso.CanSee(item))
        {
            throw new ContactCenterResourceNotFoundException("Conversa na fila");
        }

        return new Opened(contexto, item, fila, acesso);
    }

    private async Task<QueueAccess> ResolveAccessAsync(RequestContext contexto, QueueItem item, CancellationToken cancellationToken)
    {
        var fila = await _queues.FindQueueReadOnlyAsync(item.QueueId, cancellationToken)
            ?? throw new InvalidOperationException("O item aponta para uma fila que não existe.");
        return await ResolveAccessAsync(contexto, fila, cancellationToken);
    }

    private async Task<QueueAccess> ResolveAccessAsync(RequestContext contexto, ServiceQueue fila, CancellationToken cancellationToken)
    {
        QueueMemberRole? papel = null;
        if (contexto.Principal?.UserId is { } usuario)
        {
            papel = (await _queues.FindMembershipReadOnlyAsync(fila.Id, usuario, cancellationToken))?.Role;
        }

        return new QueueAccess(contexto, fila, papel);
    }

    // O identificador do pedido: o recibo da operação (idempotência presa ao tenant, à operação, ao item ou à fila, e ao autor)

    /// <summary>
    /// O que um pedido diz que é: o tipo da operação e o que ele alcança (o item, a fila, a sessão e, nas operações que levam um
    /// atendente de destino, quem é). O recibo guardado com o identificador precisa corresponder a isto, senão o identificador está
    /// sendo reusado para outra coisa.
    /// </summary>
    private sealed record Expect(
        OperationKind Kind, Guid? ItemId = null, Guid? QueueId = null, Guid? SessionId = null, bool CheckTarget = false, Guid? TargetUserId = null);

    /// <summary>
    /// O recibo do identificador, ou <c>null</c> se nunca foi usado. Um recibo que não corresponde ao pedido (outro tipo, outro autor,
    /// outro item, outra fila ou sessão) é reuso do identificador: conflito, sem devolver nem concluir nada do que era do primeiro pedido.
    /// </summary>
    private async Task<QueueOperation?> FindReceiptAsync(Guid requestId, Expect esperado, RequestContext contexto, CancellationToken cancellationToken)
    {
        var recibo = await _items.FindOperationReadOnlyAsync(requestId, cancellationToken);
        if (recibo is null)
        {
            return null;
        }

        if (!recibo.Matches(esperado.Kind, contexto.Principal?.UserId, esperado.ItemId, esperado.QueueId, esperado.SessionId)
            || (esperado.CheckTarget && !recibo.HasTarget(esperado.TargetUserId)))
        {
            throw new QueueItemConflictException(
                QueueItemConflict.RequestIdReused, "O identificador do pedido já foi usado por outra operação. Use um identificador novo.");
        }

        // Sessão embutida (ADR 0039): o recibo de uma operação sobre item de OUTRA organização (ou qualquer um, no modo CustomerOnly) nunca é devolvido nem concluído por ela. A resposta é a mesma do reuso indevido.
        if (contexto.Principal?.Embedded is not null
            && (await _items.FindItemReadOnlyAsync(recibo.ItemId, cancellationToken) is not { } doRecibo || contexto.IsOutsideEmbeddedScope(doRecibo.OrganizationId)))
        {
            throw new QueueItemConflictException(
                QueueItemConflict.RequestIdReused, "O identificador do pedido já foi usado por outra operação. Use um identificador novo.");
        }

        return recibo;
    }

    /// <summary>
    /// A resposta de um pedido que JÁ TERMINOU, devolvida ao autor original sem nova checagem de acesso ao item: é o que ele já tinha
    /// recebido, mesmo que a própria operação tenha mudado o que ele enxerga (e ele não ganha nada além disso). <c>null</c> se o
    /// identificador é novo, ou se a operação ainda está pendente (então o fluxo normal, com todas as checagens, a conclui).
    /// </summary>
    private async Task<QueueItemOperationResult?> ReplayFinishedAsync(Guid requestId, Expect esperado, RequestContext contexto, CancellationToken cancellationToken) =>
        await FindReceiptAsync(requestId, esperado, contexto, cancellationToken) is { } recibo
            ? await ReplayOfAsync(recibo, contexto, cancellationToken)
            : null;

    /// <summary>
    /// A resposta de um recibo que terminou (<c>null</c> se está pendente). A repetição devolve o item COMO O AUTOR O ENXERGA AGORA; se
    /// a própria operação (ou outra depois dela) o tirou do alcance dele, devolve a resposta gravada, que é o que ele já recebeu: nunca
    /// uma visão do item que ele não poderia ler.
    /// </summary>
    private async Task<QueueItemOperationResult?> ReplayOfAsync(QueueOperation recibo, RequestContext contexto, CancellationToken cancellationToken)
    {
        if (OperationReceipts.ToResult(recibo) is not { } guardada)
        {
            return null;
        }

        var atual = await _items.FindItemReadOnlyAsync(recibo.ItemId, cancellationToken);
        return atual is not null && (await ResolveAccessAsync(contexto, atual, cancellationToken)).CanSee(atual)
            ? guardada with { Item = QueueItemView.From(atual) }
            : guardada;
    }

    /// <summary>A resposta do recibo; se a operação ainda está pendente, conclui-a no item dela.</summary>
    private async Task<QueueItemOperationResult> ReplayOrDriveAsync(QueueOperation recibo, CancellationToken cancellationToken)
    {
        if (OperationReceipts.ToResult(recibo) is { } feito)
        {
            return feito;
        }

        var item = await _items.FindItemAsync(recibo.ItemId, cancellationToken);
        if (item?.Pending is { } pendente && pendente.OperationId == recibo.Id)
        {
            return await _executor.DriveAsync(item, cancellationToken);
        }

        // O recibo ainda dizia pendente, mas outra tentativa concluiu entre as duas leituras: relê o recibo.
        var atual = await _items.FindOperationReadOnlyAsync(recibo.Id, cancellationToken);
        return atual is not null && OperationReceipts.ToResult(atual) is { } resultado ? resultado : throw new ContactCenterConflictException();
    }

    /// <summary>Outro pedido gravou o recibo deste identificador primeiro: se é o mesmo pedido, é repetição; senão, reuso indevido.</summary>
    private async Task<QueueItemOperationResult> ResolveRaceAsync(Guid requestId, Expect esperado, RequestContext contexto, CancellationToken cancellationToken)
    {
        var recibo = await FindReceiptAsync(requestId, esperado, contexto, cancellationToken) ?? throw new ContactCenterConflictException();
        return await ReplayOrDriveAsync(recibo, cancellationToken);
    }

    /// <summary>
    /// A versão do item mudou durante o pedido. Se foi o MESMO pedido repetido ao mesmo tempo, é repetição; senão, é conflito, com o
    /// motivo lido do estado que venceu.
    /// </summary>
    private async Task<QueueItemOperationResult> ResolveConflictAsync(Guid requestId, Expect esperado, Guid itemId, RequestContext contexto, CancellationToken cancellationToken)
    {
        if (await FindReceiptAsync(requestId, esperado, contexto, cancellationToken) is { } recibo)
        {
            return await ReplayOrDriveAsync(recibo, cancellationToken);
        }

        throw await ConflictFromCurrentStateAsync(itemId, cancellationToken);
    }

    /// <summary>
    /// O pedido não muda nada (a conversa já está como ele pede), mas deixa recibo, como qualquer operação: repeti-lo depois devolve a
    /// mesma resposta, em vez de refazer o que já foi respondido (e desfazer o que veio depois).
    /// </summary>
    private async Task<QueueItemOperationResult> RecordNoChangeAsync(
        QueueItem item, Guid requestId, OperationKind kind, Guid? targetUserId, Expect esperado, RequestContext contexto, CancellationToken cancellationToken)
    {
        var visao = QueueItemView.From(item);
        _items.AddOperation(QueueOperation.Immediate(
            contexto.TenantId, requestId, kind, item.Id, item.SessionId, contexto.Principal?.UserId, item.QueueId, targetUserId,
            OperationState.NoChange, OperationReceipts.Serialize(visao), _clock.GetUtcNow()));
        try
        {
            await _items.SaveChangesAsync(cancellationToken);
        }
        catch (OperationIdInUseException)
        {
            return await ResolveRaceAsync(requestId, esperado, contexto, cancellationToken);
        }

        return new QueueItemOperationResult(visao, OperationOutcome.AlreadyApplied);
    }

    /// <summary>A operação pendente com este identificador é de outro autor: o identificador não é dele.</summary>
    private static void RequirePendingOwner(PendingOperation pendente, RequestContext contexto)
    {
        if (pendente.ActorUserId != contexto.Principal?.UserId)
        {
            throw new QueueItemConflictException(
                QueueItemConflict.RequestIdReused, "O identificador do pedido já foi usado por outra operação. Use um identificador novo.");
        }
    }

    /// <summary>
    /// Repetição do pedido pendente (as repetições de um pedido que já terminou são resolvidas antes, pelo recibo): se este identificador é
    /// o da operação pendente do item, de quem o pediu, conclui-a; se há outra operação em andamento, ou o item foi encerrado, é
    /// conflito. Devolve <c>null</c> quando é um pedido novo e o item está livre.
    /// </summary>
    private async Task<QueueItemOperationResult?> RepeatAsync(Opened aberto, Guid requestId, CancellationToken cancellationToken)
    {
        var item = aberto.Item;
        if (item.Pending is { } pendente)
        {
            if (pendente.OperationId == requestId)
            {
                RequirePendingOwner(pendente, aberto.Context);
                return await _executor.DriveAsync(item, cancellationToken);
            }

            throw new QueueItemConflictException(
                pendente.Kind is PendingKind.Assign ? QueueItemConflict.AlreadyAssigned : QueueItemConflict.OperationInProgress,
                "Há outra operação em andamento nesta conversa.");
        }

        if (item.IsClosed)
        {
            throw new QueueItemConflictException(QueueItemConflict.ItemClosed, "A conversa não está mais em atendimento na fila.");
        }

        return null;
    }

    /// <summary>
    /// Grava a intenção e o recibo na MESMA transação (a versão do item decide a disputa pelo item; a chave do recibo, a disputa pelo
    /// identificador) e só então conclui no Conversations. Quem perde a disputa não chama o Conversations: se foi o mesmo pedido
    /// repetido, recebe o resultado dele; senão recebe o conflito, com o motivo lido do estado que venceu.
    /// </summary>
    private async Task<QueueItemOperationResult> BeginAndDriveAsync(
        Opened aberto, Guid requestId, OperationKind kind, Guid queueId, Guid? targetUserId, Expect esperado, Action begin, CancellationToken cancellationToken,
        Capacity? capacity = null)
    {
        var item = aberto.Item;
        var itemId = item.Id;
        try
        {
            begin();
            _items.AddOperation(QueueOperation.Begin(
                aberto.Context.TenantId, requestId, kind, itemId, item.SessionId, aberto.Context.Principal?.UserId, queueId, targetUserId, _clock.GetUtcNow()));
            if (capacity is null)
            {
                await _items.SaveChangesAsync(cancellationToken);
            }
            else if (!await _items.SaveWithinCapacityAsync(queueId, capacity.UserId, capacity.Maximum, requestId, cancellationToken))
            {
                throw await RuleAsync(
                    PendingKind.Assign, itemId, ContactCenterRule.AgentAtCapacity,
                    "Você já tem o máximo de conversas que esta fila permite ao mesmo tempo.", cancellationToken, ItemResource);
            }
        }
        catch (ContactCenterConflictException)
        {
            return await ResolveConflictAsync(requestId, esperado, itemId, aberto.Context, cancellationToken);
        }
        catch (OperationIdInUseException)
        {
            return await ResolveRaceAsync(requestId, esperado, aberto.Context, cancellationToken);
        }
        catch (QueueItemStateException ex)
        {
            throw new QueueItemConflictException(QueueItemConflict.NothingToChange, ex.Message);
        }

        return await _executor.DriveAsync(item, cancellationToken);
    }

    private async Task<Exception> ConflictFromCurrentStateAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var atual = await _items.FindItemReadOnlyAsync(itemId, cancellationToken);
        if (atual is null || atual.IsClosed)
        {
            return new QueueItemConflictException(QueueItemConflict.ItemClosed, "A conversa não está mais em atendimento na fila.");
        }

        return atual.Pending is { Kind: PendingKind.Assign } || atual.Status == QueueItemStatus.Assigned
            ? new QueueItemConflictException(QueueItemConflict.AlreadyAssigned, "Outro atendente já assumiu esta conversa.")
            : atual.HasPending
                ? new QueueItemConflictException(QueueItemConflict.OperationInProgress, "Há outra operação em andamento nesta conversa.")
                : new QueueItemConflictException(QueueItemConflict.ConcurrentChange, "A conversa foi alterada por outra operação. Releia e tente de novo.");
    }

    private async Task RequireMemberAsync(ServiceQueue fila, Guid usuario, Guid itemId, PendingKind operacao, CancellationToken cancellationToken)
    {
        if (await _queues.FindMembershipReadOnlyAsync(fila.Id, usuario, cancellationToken) is null)
        {
            throw await RuleAsync(operacao, itemId, ContactCenterRule.TargetNotQueueMember, "O atendente de destino não está nesta fila.", cancellationToken, ItemResource);
        }
    }

    /// <summary>Registra a recusa por regra na auditoria (como negação) e devolve a exceção a lançar.</summary>
    private async Task<ContactCenterRuleException> RuleAsync(
        PendingKind operacao, Guid resourceId, ContactCenterRule rule, string message, CancellationToken cancellationToken, string resourceType = "session")
    {
        // A recusa por regra é guardada de forma durável (e entregue, se a trilha responder): a trilha fora do ar não a perde.
        await _audit.RecordAsync(
            new AuditEntry(HandlingExecutor.OperationName(operacao), resourceType, resourceId.ToString(), AuditOutcome.Denied),
            cancellationToken);
        return new ContactCenterRuleException(rule, message);
    }

    private static void RequireId(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ContactCenterValidationException("O identificador do pedido (chave de idempotência) é obrigatório.");
        }
    }
}
