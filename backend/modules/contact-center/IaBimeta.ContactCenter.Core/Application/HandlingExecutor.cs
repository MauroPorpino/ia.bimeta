using System.Security.Cryptography;
using System.Text;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// Conclui a operação pendente de um item no Conversations e depois no ContactCenter (BOUNDARIES.md §6, "Falha no meio"). A
/// intenção já está gravada no item, então a execução é convergente e repetível: a cada passo ela relê a sessão e só faz o que
/// falta, com os identificadores de idempotência da própria operação (<c>TransitionId</c> e <c>TransferId</c>). Se uma chamada
/// ao Conversations falhar, o item continua pendente, e repetir a operação (ou o job de reconciliação) conclui sem duplicar
/// efeito. A conversa permanece no modo anterior até a transição ter sucesso.
/// <para>
/// Nenhuma operação escolhe unidade ou atendente por conta própria: o destino já foi decidido e validado pelo caso de uso, e a
/// unidade só é aplicada se o Conversations a aceitar (inclusive a autorização do número, ADR 0008).
/// </para>
/// <para>
/// <b>Exclusividade.</b> A trava do banco por item evita o trabalho repetido, mas vale para UMA conexão, que pode se perder sem que o
/// executor saiba. A proteção real é a cerca (<c>fencing token</c>): depois de obter a trava, o executor pega um token monotônico e o
/// registra na sessão (<see cref="IConversationGateway.FenceAsync"/>); toda escrita seguinte o leva, e o Conversations recusa, dentro
/// da trava da sessão, o token menor que o maior já visto. Um executor que foi substituído não escreve mais, mesmo que releia a versão
/// atual da sessão ou abra outra conexão.
/// </para>
/// <para>
/// <b>Autoridade.</b> Reler a sessão e renovar a versão esperada a cada passo é seguro para a <c>StateVersion</c>, mas não para a
/// autorização: a operação foi autorizada sobre um responsável (e um período), e esse estado vai com cada comando como pré-condição
/// (<see cref="HandlingPrecondition"/>), conferida pelo Conversations dentro da trava da sessão. Se o responsável já é outro, nada é gravado
/// e a operação é desfeita (<see cref="ContactCenterRule.ConversationChanged"/>). A repetição de um comando já aplicado é reconhecida
/// antes da condição, então a conclusão após a perda da resposta continua valendo. A ENTRADA na fila também leva a base (a conversa com a IA,
/// ou esperando): ela nunca retira um atendente humano que surgiu depois de autorizada.
/// </para>
/// <para>
/// <b>Transferência confirmada em parte.</b> A transferência de unidade é feita em dois comandos (a unidade e depois quem responde), e cada
/// um é confirmado sozinho no Conversations. Se o segundo for recusado porque o atendimento mudou, a unidade JÁ está aplicada, e o
/// ContactCenter não a desfaz por conta própria: a operação é concluída em parte (<see cref="QueueItem.CompletePartialTransfer"/>). O item vai
/// para a fila, a unidade e o setor de destino, que são onde a conversa está, e espelha o atendimento que o Conversations tem agora (o
/// responsável atual é preservado). Nunca se cancela a intenção deixando o item na fila de origem com a unidade de destino: a fila de
/// origem não pode ler a conversa de outra unidade. Enquanto isso não termina, a intenção continua pendente e o acesso fica fechado.
/// </para>
/// <para>
/// <b>Auditoria.</b> Toda conclusão (e toda recusa definitiva) guarda a entrada de auditoria na MESMA gravação do item, do histórico e do
/// recibo (<see cref="IDeferredAudit"/>); a entrega à trilha vem depois da confirmação, e quem reconcilia entrega o que sobrou. Nenhuma
/// queda do processo entre a confirmação e a entrega perde a auditoria.
/// </para>
/// </summary>
public sealed class HandlingExecutor
{
    // Uma passada por escrita no Conversations (encaminhar, transitar) mais as releituras depois de uma versão defasada.
    private const int MaxIterations = 6;

    private const int FlushBatch = 50;
    private const string TransferReason = "queue-transfer";
    private const string SessionEndedReason = "session-closed";
    private const string RejectedReason = "operation-rejected";

    private const string ChangedMessage = "O atendimento da conversa mudou depois de a operação ser autorizada (outro responsável). Nada foi alterado: releia e peça de novo.";
    private const string ChangedAfterUnitMessage =
        "O atendimento da conversa mudou depois de a operação ser autorizada (outro responsável), mas a unidade de destino já tinha sido aplicada na conversa e foi mantida.";
    private const string WithoutBasisMessage = "A operação não registrou o estado do atendimento sobre o qual foi autorizada, e não é executada.";

    /// <summary>A operação (na auditoria) do que a transferência não fez porque o atendente de destino já não estava na fila de destino.</summary>
    public const string TargetUnavailableOperation = "contact-center.item.transfer.target-unavailable";

    /// <summary>A operação (na auditoria) do que a transferência não fez porque o atendimento mudou depois de a unidade já ter sido aplicada (concluída em parte).</summary>
    public const string ResponderChangedOperation = "contact-center.item.transfer.responder-changed";

    /// <summary>A operação (na auditoria) da unidade que a entrada já tinha aplicado na conversa quando o atendimento mudou e a entrada foi desfeita.</summary>
    public const string EnterUnitKeptOperation = "contact-center.item.enter.unit-kept";

    /// <summary>A operação (na auditoria) do atendimento que o item passa a espelhar depois de uma mudança feita por fora (a mesma da projeção dos eventos).</summary>
    public const string ExternalChangeOperation = "contact-center.item.external-change";

    /// <summary>A operação (na auditoria) da obrigação de ressincronização liquidada SEM verificação: a identidade do item não se comprova, e nada foi espelhado nem encerrado.</summary>
    public const string ResyncUnverifiedOperation = "contact-center.item.resync-unverified";

    private readonly IQueueItemRepository _items;
    private readonly IQueueRepository _queues;
    private readonly IConversationGateway _conversations;
    private readonly TimeProvider _clock;
    private readonly IDeferredAudit _audit;

    public HandlingExecutor(IQueueItemRepository items, IQueueRepository queues, IConversationGateway conversations, TimeProvider clock, IDeferredAudit audit)
    {
        _items = items;
        _queues = queues;
        _conversations = conversations;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>O nome da operação na auditoria, por tipo de fato concluído.</summary>
    public static string OperationName(HistoryKind kind) => kind switch
    {
        HistoryKind.Entered => "contact-center.item.enter",
        HistoryKind.Assigned => "contact-center.item.assign",
        HistoryKind.Reassigned => "contact-center.item.reassign",
        HistoryKind.Released => "contact-center.item.release",
        HistoryKind.TransferredQueue => "contact-center.item.transfer",
        HistoryKind.ReturnedToAi => "contact-center.item.return-to-ai",
        HistoryKind.SessionClosed => "contact-center.item.session-closed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string OperationName(PendingKind kind) => kind switch
    {
        PendingKind.Enter => "contact-center.item.enter",
        PendingKind.Assign => "contact-center.item.assign",
        PendingKind.Release => "contact-center.item.release",
        PendingKind.TransferQueue => "contact-center.item.transfer",
        PendingKind.ReturnToAi => "contact-center.item.return-to-ai",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Conclui a operação pendente do item. Só UMA tentativa por vez escreve no Conversations por conta do item (trava do banco, e a
    /// cerca no Conversations, que vale mesmo se a trava se perder): quem chega depois relê o item e, se a operação já foi concluída ou
    /// desfeita, devolve o resultado dela sem escrever nada. Assim uma tentativa atrasada nunca aplica no Conversations o que já não vale,
    /// nem desfaz o que veio depois.
    /// <para>
    /// Dentro de uma transação que ainda pode ser desfeita (o consumo dos eventos do Conversations) a auditoria só é guardada, e a
    /// entrega à trilha acontece depois da confirmação (quem chamou, ou a reconciliação).
    /// </para>
    /// </summary>
    /// <exception cref="ContactCenterRuleException">O Conversations ou o Channels recusou o destino: nada mudou na conversa e a operação foi desfeita.</exception>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou o item durante esta.</exception>
    public async Task<QueueItemOperationResult> DriveAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var operacao = (item.Pending ?? throw new InvalidOperationException("O item não tem operação pendente.")).OperationId;

        await using var trava = await _items.TryLockItemAsync(item.Id, cancellationToken);
        if (trava is null)
        {
            // Outra tentativa está concluindo esta operação agora: ela (ou o job de reconciliação) termina o trabalho.
            return new QueueItemOperationResult(QueueItemView.From(item), OperationOutcome.Pending);
        }

        item = await _items.ReloadItemAsync(item, cancellationToken);
        if (item.Pending is not { } pendente || pendente.OperationId != operacao)
        {
            return await ResolveEndedAsync(item, operacao, cancellationToken);
        }

        // A BASE da intenção é validada ANTES de qualquer cerca ou comando (ScopeProof.BaseOf): a intenção sem a base completa (o estado de atendimento e a
        // geração da colocação sobre os quais foi autorizada) não tem autoridade para ALTERAR o Conversations, e isso inclui registrar a cerca. Ela fica inerte:
        // só o que o Conversations JÁ aplicou é reconhecido, por evidência de leitura, nunca por um comando modificador usado como consulta.
        if (!ScopeProof.BaseOf(pendente).IsComplete)
        {
            return await RecognizeWithoutAuthorityAsync(item, pendente, cancellationToken);
        }

        // O token desta execução. A emissão e a revalidação da intenção são UM passo atômico sob a trava da linha do item: quem perdeu a
        // operação (cancelada, ou substituída por outra) não recebe token, por mais tarde que chegue, e não escreve nada. O token avança a
        // versão do item, então um executor que pegou o token antes deste não grava mais no item, e o Conversations recusa o token menor.
        if (await _items.AcquireFenceTokenAsync(item.Id, operacao, cancellationToken) is not { } cerca)
        {
            return await ResolveEndedAsync(await _items.ReloadItemAsync(item, cancellationToken), operacao, cancellationToken);
        }

        // Recarregar traz a versão e o token que a linha tem AGORA, e podem não ser os desta emissão: outro executor pode ter tomado a linha
        // entre a nossa emissão e esta leitura (o token t2 dele, a versão v2 dele). Seguir com a versão v2 seria gravar no item com a
        // autoridade de outro: o token da linha precisa ser EXATAMENTE o que recebemos. Se a operação já terminou, o que vale é o resultado dela.
        item = await _items.ReloadItemAsync(item, cancellationToken);
        if (item.Pending is not { } vigente || vigente.OperationId != operacao)
        {
            return await ResolveEndedAsync(item, operacao, cancellationToken);
        }

        if (item.ExecutorFenceToken != cerca)
        {
            return Superseded(item);
        }

        // A AUTORIDADE da operação está presa ao estado sobre o qual ela foi autorizada (o responsável efetivo e o período, que o serviço leu no
        // Conversations e guardou na intenção), e esse estado vai com CADA comando como pré-condição, conferida dentro da trava da sessão, junto
        // da gravação. Reler a sessão e renovar a versão esperada (a corrida de sempre) não troca a autorização dada sobre um responsável pela
        // permissão de alterar o próximo: se o responsável mudou, o Conversations não grava (HandlingChanged) e a operação é desfeita. A entrada
        // leva a base dela também (a conversa com a IA, ou esperando): ela não retira um atendente humano que surgiu depois de autorizada.
        // Operação sem base (gravada antes de existir) não é executada no Conversations.
        // A COLOCAÇÃO também é autorizada: a geração da colocação da sessão (a identidade durável da unidade e do setor, no Conversations) é conferida
        // antes de o comando mudá-la, e o comando seguinte de uma operação que a muda espera a geração seguinte. Assim a operação só vale sobre a
        // colocação autorizada, e uma mudança de unidade ou setor feita por outro no intervalo (inclusive A -> B -> A) a desfaz em vez de passar.
        var modoDaBase = pendente.Basis is { InAiMode: true } ? HandlingMode.AiAutonomous : HandlingMode.HumanAssisted;
        var precondicao = pendente.Basis is { } autorizado
            ? new HandlingPrecondition(autorizado.ResponderUserId, autorizado.HandlingPeriod, modoDaBase, autorizado.PlacementEpoch)
            : null;
        var precondicaoDepoisDaUnidade = pendente.Basis is { } autorizada
            ? new HandlingPrecondition(autorizada.ResponderUserId, autorizada.HandlingPeriod, modoDaBase, ScopeProof.ExpectedEpoch(pendente))
            : null;
        var geracaoDaColocacao = ScopeProof.ExpectedEpoch(pendente);

        var unidadeFeita = false;
        var destinoVerificado = pendente.UserId is null;
        var semDestino = false;
        string? ultimoErro = null;

        for (var iteracao = 0; iteracao < MaxIterations; iteracao++)
        {
            SessionView? sessao;
            try
            {
                if (iteracao == 0)
                {
                    // O registro da cerca e a leitura são um passo só: a sessão lida já reflete tudo o que um executor mais antigo conseguiu
                    // gravar, e nada que ele tente depois disto é aceito.
                    var registro = await _conversations.FenceAsync(new FenceHandlingCommand(item.SessionId, cerca), cancellationToken);
                    if (registro.Outcome == FenceOutcome.FencedOut)
                    {
                        return Superseded(item);
                    }

                    sessao = registro.Outcome == FenceOutcome.Fenced ? registro.Session : null;
                }
                else
                {
                    sessao = await _conversations.GetSessionAsync(item.SessionId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await StayPendingAsync(item, ex.GetType().Name, cancellationToken);
            }

            // Sessão que não existe mais no tenant, ou já encerrada: o atendimento humano acabou e a operação perdeu o sentido. A sessão
            // encerrada traz a sequência da última mensagem (nenhuma chega depois): a fronteira do que a fila viu.
            if (sessao is null || sessao.Status == SessionStatus.Closed)
            {
                return await EndBecauseSessionEndedAsync(item, pendente, sessao, sessao is not null && UnitApplied(sessao, pendente), cancellationToken);
            }

            // O atendente de destino precisa continuar na fila de destino: alguém pode tê-lo tirado entre o pedido e a conclusão. Mas se
            // o Conversations JÁ aplicou o efeito (a sessão já está com ele), a operação aconteceu: o ContactCenter a reconhece e
            // reconcilia, em vez de desfazê-la e passar a divergir da sessão.
            if (!destinoVerificado)
            {
                destinoVerificado = true;
                if (await _queues.FindMembershipReadOnlyAsync(pendente.QueueId, pendente.UserId!.Value, cancellationToken) is null && !EffectApplied(sessao, pendente))
                {
                    // A transferência composta pode ter aplicado a unidade e ficado sem o atendente (ele saiu da fila de destino): desfazê-la
                    // deixaria o item numa fila incompatível com a unidade em que a sessão JÁ está. Conclui o que foi aplicado: a conversa
                    // chega à fila de destino, em espera, sem responsável, com o rastro e a auditoria do que não pôde ser feito.
                    if (pendente.Kind == PendingKind.TransferQueue && UnitApplied(sessao, pendente))
                    {
                        semDestino = true;
                    }
                    else
                    {
                        throw await CancelAsync(
                            item, pendente, ContactCenterRule.TargetNotQueueMember, "O atendente de destino não está na fila de destino.", cancellationToken);
                    }
                }
            }

            // 1) A unidade (e o setor) de destino, se a operação inclui encaminhar a sessão. Quem decide se o número pode atender
            // a unidade é o Channels, consultado pelo Conversations a cada pedido (ADR 0008): aqui só se aplica o que ele aceita.
            if (!unidadeFeita && pendente.BusinessUnitId is { } unidade
                && (sessao.BusinessUnitId != unidade || sessao.DepartmentId != pendente.DepartmentId))
            {
                // A unidade ainda não foi aplicada (a condição acima): nada do Conversations foi confirmado, e a operação pode ser desfeita inteira.
                if (precondicao is null)
                {
                    throw await CancelAsync(item, pendente, ContactCenterRule.ConversationChanged, WithoutBasisMessage, cancellationToken);
                }

                TransferSessionResult encaminhamento;
                try
                {
                    encaminhamento = await _conversations.TransferAsync(
                        new TransferSessionCommand(sessao.Id, unidade, pendente.DepartmentId, sessao.StateVersion, pendente.TransferId, TransferReason, cerca, precondicao),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return await StayPendingAsync(item, ex.GetType().Name, cancellationToken);
                }

                switch (encaminhamento.Outcome)
                {
                    case TransferOutcome.Applied:
                    case TransferOutcome.AlreadyApplied:
                    case TransferOutcome.Unchanged:
                        unidadeFeita = true;
                        continue;

                    case TransferOutcome.StaleState:
                        ultimoErro = nameof(TransferOutcome.StaleState);
                        continue;

                    case TransferOutcome.FencedOut:
                        return Superseded(item);

                    case TransferOutcome.HandlingChanged:
                        // A condição vale ANTES de a unidade ser aplicada: nada foi gravado, e a operação é desfeita inteira.
                        throw await CancelAsync(item, pendente, ContactCenterRule.ConversationChanged, ChangedMessage, cancellationToken);

                    case TransferOutcome.SessionClosed:
                        // A sessão encerrou entre a leitura e a escrita: a próxima leitura a traz encerrada, com a fronteira do histórico.
                        ultimoErro = nameof(TransferOutcome.SessionClosed);
                        continue;

                    case TransferOutcome.SessionNotFound:
                        return await EndBecauseSessionEndedAsync(item, pendente, null, false, cancellationToken);

                    case TransferOutcome.TargetNotAuthorizedForChannel:
                        throw await CancelAsync(
                            item, pendente, ContactCenterRule.UnitNotAuthorizedForChannel,
                            "A unidade da fila de destino não está entre as autorizadas do número desta conversa.", cancellationToken);

                    default:
                        throw await CancelAsync(
                            item, pendente, ContactCenterRule.TargetUnitNotAllowed,
                            "A unidade ou o setor de destino não existem na organização desta conversa.", cancellationToken);
                }
            }

            // 2) O estado de atendimento: quem responde (IA ou atendente) e qual atendente. Na transferência sem o atendente, o destino é
            // "humano, sem responsável", com um identificador PRÓPRIO e determinístico (o da transição original pode já estar gravado, com
            // o atendente, no Conversations): repetir esta conclusão repete a mesma transição.
            var (modo, responsavel) = Target(pendente, semDestino);
            var transicaoId = semDestino ? ScopeProof.DerivedTransitionId(pendente.TransitionId) : pendente.TransitionId;

            // NÃO há atalho "o estado já é o pedido, conclui sem comando": igualdade do estado corrente não é prova de que foi ESTA operação (nem de que o período é o
            // dela): humano -> IA -> humano é OUTRO atendimento com o mesmo modo e responsável. A conclusão passa SEMPRE pela transição idempotente, com o mesmo
            // identificador: repetição de uma operação já aplicada devolve o recibo ORIGINAL (a versão, a fronteira e o período dela, que não dependem do estado de
            // agora); uma sessão que já estava no estado pedido por outra via devolve "sem efeito" (identidade desconhecida, e nenhum período inventado).

            // A unidade da operação, se ela a inclui, já está aplicada no Conversations por esta execução ou por uma anterior (o passo 1 só
            // deixa de rodar quando a unidade já é a de destino): ela não se desfaz, e a operação não pode ser cancelada como se nada tivesse
            // acontecido.
            var unidadeConfirmada = pendente.BusinessUnitId is not null && (unidadeFeita || UnitApplied(sessao, pendente));

            if (precondicao is null)
            {
                return await StopAfterUnitAsync(item, pendente, sessao, unidadeConfirmada, false, WithoutBasisMessage, WithoutBasisMessage, cancellationToken);
            }

            TransitionResult transicao;
            try
            {
                transicao = await _conversations.TransitionAsync(
                    new TransitionHandlingModeCommand(sessao.Id, modo, responsavel, sessao.StateVersion, transicaoId, pendente.Reason, cerca, precondicaoDepoisDaUnidade),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await StayPendingAsync(item, ex.GetType().Name, cancellationToken);
            }

            switch (transicao.Outcome)
            {
                case TransitionOutcome.Applied:
                case TransitionOutcome.AlreadyApplied:
                    return await CompleteFromReceiptAsync(item, pendente, transicao, semDestino, geracaoDaColocacao, cancellationToken);

                case TransitionOutcome.FencedOut:
                    return Superseded(item);

                case TransitionOutcome.HandlingChanged:
                    // O atendimento mudou depois de a operação ser autorizada. Se a unidade JÁ foi aplicada, o que o Conversations confirmou
                    // fica, e a operação é concluída em parte (a sessão é relida: o que vale é o atendimento que ela tem agora).
                    return await StopAfterUnitAsync(item, pendente, sessao, unidadeConfirmada, true, ChangedMessage, ChangedAfterUnitMessage, cancellationToken);

                case TransitionOutcome.SessionClosed:
                    // A próxima leitura traz a sessão encerrada, com a fronteira do histórico.
                    ultimoErro = nameof(TransitionOutcome.SessionClosed);
                    continue;

                case TransitionOutcome.SessionNotFound:
                    return await EndBecauseSessionEndedAsync(item, pendente, null, false, cancellationToken);

                case TransitionOutcome.InvalidTransition when sessao.HandlingMode == modo && sessao.ResponderRef == responsavel:
                    // A devolução à IA de uma sessão que JÁ estava em modo de IA por outra mudança (não por esta operação): o efeito
                    // existe, mas o instante dele não é conhecido, então a fronteira do histórico fica desconhecida (histórico fechado).
                    return await CompleteAsync(
                        item, pendente, sessao.StateVersion, null, PeriodOfAuthorizedState(pendente, sessao), semDestino, geracaoDaColocacao, false, null, cancellationToken);

                default:
                    // Versão defasada (outra mudança venceu) ou combinação que acabou de ficar igual ao estado atual: relê a sessão
                    // e decide de novo. Se não convergir, a operação continua pendente e quem repetir a conclui.
                    ultimoErro = transicao.Outcome.ToString();
                    continue;
            }
        }

        return await StayPendingAsync(item, ultimoErro ?? "NotConverged", cancellationToken);
    }

    /// <summary>
    /// Alinha à sessão um item que ficou ATRÁS do Conversations: um evento externo mais novo que o observado chegou com uma operação do item
    /// pendente (a projeção dele foi adiada, e a versão ficou guardada no item), e a operação já terminou, concluída ou desfeita. A autoridade é a
    /// sessão, lida agora: o item espelha o responsável de agora, ou é encerrado se o atendimento (ou a sessão) acabou, com a fronteira do histórico
    /// decidida contra a sessão. Repetível e convergente: alinhado, a marca está atendida e uma segunda rodada não encontra nada. Se a sessão ainda
    /// não mostra a versão marcada, ou a leitura falha, nada muda e a próxima rodada tenta de novo.
    /// </summary>
    /// <returns><c>true</c> se o item foi alinhado.</returns>
    public async Task<bool> ResyncAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (!item.NeedsResync)
        {
            return false;
        }

        SessionView? sessao;
        try
        {
            sessao = await _conversations.GetSessionAsync(item.SessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        var agora = _clock.GetUtcNow();
        if (sessao is null || sessao.Status == SessionStatus.Closed)
        {
            var fronteira = HistoryBoundary.ForEndedSession(item, sessao);
            var fim = item.CloseBecauseSessionEnded(
                DerivedHistoryId($"resync:{item.ResyncStateVersion}", item.Id), SessionEndedReason, agora, fronteira.Seq, fronteira.Certified);
            if (fim is null)
            {
                return false;
            }

            _items.AddHistory(fim);
            _audit.Defer(new AuditEntry(OperationName(HistoryKind.SessionClosed), "queue-item", item.Id.ToString(), AuditOutcome.Success));
        }
        else
        {
            // Uma sessão que ainda não mostra a versão do evento adiado não é o estado a que se alinhar (a autoridade nunca fica atrás do que ela mesma publicou).
            if (sessao.StateVersion < item.ResyncStateVersion)
            {
                return false;
            }

            // O item representa o atendimento que a sessão tem AGORA? Só a PROVA espelha (Same) ou encerra (Other): período desconhecido não é correspondência.
            var atendimento = ScopeProof.AttendanceOfSession(item.HandlingPeriod, sessao.HandlingMode == HandlingMode.HumanAssisted, sessao.HandlingPeriod);
            if (atendimento == ProofVerdict.Unknown)
            {
                // Legado SEM prova, e que a consulta não consegue produzir: a obrigação é liquidada como NÃO VERIFICADA (nenhuma identidade é preenchida, o conteúdo
                // e os novos comandos continuam negados), e a auditoria registra que não houve convergência. Falha de leitura, tempo esgotado ou operação ainda
                // recuperável nunca chegam aqui: voltam antes, com a obrigação aberta.
                if (!item.SettleUnverified(sessao.StateVersion, agora))
                {
                    return false;
                }

                _audit.Defer(new AuditEntry(ResyncUnverifiedOperation, "queue-item", item.Id.ToString(), AuditOutcome.Denied));
            }
            else
            {
                var espelho = item.MirrorSession(atendimento, sessao.ResponderRef, sessao.StateVersion, DerivedHistoryId($"resync:{sessao.StateVersion}", item.Id), agora);
                if (espelho is not null)
                {
                    _items.AddHistory(espelho);
                    _audit.Defer(new AuditEntry(ExternalChangeOperation, "queue-item", item.Id.ToString(), AuditOutcome.Success));
                }
            }
        }

        try
        {
            await _items.SaveChangesAsync(cancellationToken);
        }
        catch (ContactCenterConflictException)
        {
            // Outra operação mexeu no item ao mesmo tempo: a próxima rodada relê e decide de novo.
            return false;
        }

        await FlushAuditAsync(cancellationToken);
        return true;
    }

    private static (HandlingMode Mode, Guid? Responder) Target(PendingOperation pendente, bool withoutTarget) => pendente.Kind switch
    {
        PendingKind.ReturnToAi => (HandlingMode.AiAutonomous, null),
        PendingKind.Assign or PendingKind.TransferQueue => (HandlingMode.HumanAssisted, withoutTarget ? null : pendente.UserId),
        _ => (HandlingMode.HumanAssisted, null),
    };

    /// <summary>O Conversations já reflete o que a operação pede (quem responde e, se há encaminhamento, a unidade)?</summary>
    private static bool EffectApplied(SessionView sessao, PendingOperation pendente)
    {
        var (modo, responsavel) = Target(pendente, false);
        return sessao.HandlingMode == modo
            && sessao.ResponderRef == responsavel
            && (pendente.BusinessUnitId is not { } unidade || (sessao.BusinessUnitId == unidade && sessao.DepartmentId == pendente.DepartmentId));
    }

    /// <summary>A operação inclui encaminhar a sessão para outra unidade E o Conversations já fez isso?</summary>
    private static bool UnitApplied(SessionView sessao, PendingOperation pendente) =>
        pendente.BusinessUnitId is { } unidade && sessao.BusinessUnitId == unidade && sessao.DepartmentId == pendente.DepartmentId;

    /// <summary>
    /// O identificador, derivado e estável, de um registro de histórico a mais que a MESMA operação deixa (o histórico tem um registro por
    /// identificador): repetir a conclusão repete o mesmo identificador, e nunca colide com o de outra operação.
    /// </summary>
    private static Guid DerivedHistoryId(string purpose, Guid operationId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"contact-center:{purpose}:{operationId:N}"))[..16]);

    /// <summary>
    /// A operação não pode aplicar o que falta no Conversations (o atendimento mudou depois de ser autorizada, ou ela não tem a base da
    /// autorização). Se NADA do que ela pede foi confirmado, é desfeita inteira, sem rastro no Conversations. Se a unidade JÁ foi aplicada,
    /// o Conversations tem uma parte da operação confirmada, e não é possível desfazê-la daqui: a transferência é concluída em parte (o item
    /// vai para onde a conversa está, e espelha o atendimento que ela tem agora), e a entrada é desfeita com a unidade registrada.
    /// </summary>
    /// <param name="unitConfirmed">O Conversations já aplicou a unidade e o setor que a operação pedia.</param>
    /// <param name="reread">Reler a sessão para saber o atendimento de agora (o comando recusado não o devolve); do contrário, <paramref name="sessao"/> é o estado conhecido.</param>
    private async Task<QueueItemOperationResult> StopAfterUnitAsync(
        QueueItem item, PendingOperation pendente, SessionView sessao, bool unitConfirmed, bool reread, string message, string messageWhenUnitKept, CancellationToken cancellationToken)
    {
        if (!unitConfirmed)
        {
            throw await CancelAsync(item, pendente, ContactCenterRule.ConversationChanged, message, cancellationToken);
        }

        if (pendente.Kind != PendingKind.TransferQueue)
        {
            throw await CancelAsync(item, pendente, ContactCenterRule.ConversationChanged, messageWhenUnitKept, cancellationToken, unitKept: true);
        }

        SessionView? efetiva = sessao;
        if (reread)
        {
            try
            {
                efetiva = await _conversations.GetSessionAsync(item.SessionId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sem saber o atendimento de agora não se conclui: a intenção fica pendente (a leitura continua fechada) e a reconciliação repete.
                return await StayPendingAsync(item, ex.GetType().Name, cancellationToken);
            }
        }

        return await CompletePartialTransferAsync(item, pendente, efetiva, cancellationToken);
    }

    /// <summary>
    /// Conclui a transferência de unidade que o Conversations confirmou só em parte (<see cref="QueueItem.CompletePartialTransfer"/>), na mesma
    /// gravação do item, do histórico, do recibo e da auditoria. O recibo registra o que foi efetivamente confirmado (a transferência para a
    /// fila de destino), e o histórico e a auditoria dizem o que não foi (quem responde não é o da autorização). Repetível: uma segunda
    /// conclusão encontra o recibo fechado.
    /// </summary>
    private async Task<QueueItemOperationResult> CompletePartialTransferAsync(
        QueueItem item, PendingOperation pendente, SessionView? efetiva, CancellationToken cancellationToken)
    {
        // A sessão acabou, ou não existe mais: o atendimento terminou e o item acompanha o fim, já na fila onde a conversa estava.
        if (efetiva is null || efetiva.Status == SessionStatus.Closed)
        {
            return await EndBecauseSessionEndedAsync(item, pendente, efetiva, true, cancellationToken);
        }

        // O atendimento que o item representa continua? Em IA, ou em outro período humano, ele acabou (Other); período desconhecido não prova nada (Unknown: o item
        // não espelha nem é encerrado por inferência).
        var atendimento = ScopeProof.AttendanceOfSession(item.HandlingPeriod, efetiva.HandlingMode == HandlingMode.HumanAssisted, efetiva.HandlingPeriod);
        var registros = item.CompletePartialTransfer(
            atendimento, efetiva.ResponderRef, efetiva.StateVersion, DerivedHistoryId("partial-transfer:mirror", pendente.OperationId), _clock.GetUtcNow(),
            ScopeProof.ExpectedEpoch(pendente));
        foreach (var registro in registros)
        {
            _items.AddHistory(registro);
        }

        await CloseReceiptAsync(item, pendente.OperationId, OperationState.Applied, cancellationToken);

        // O que o Conversations confirmou (a transferência) e o que não foi feito (quem responde): as duas coisas ficam na trilha, com a proveniência do PEDIDO.
        DeferAudit(new AuditEntry(OperationName(HistoryKind.TransferredQueue), "queue-item", item.Id.ToString(), AuditOutcome.Success), pendente);
        DeferAudit(new AuditEntry(ResponderChangedOperation, "queue-item", item.Id.ToString(), AuditOutcome.Denied), pendente);
        if (registros.Count > 1)
        {
            _audit.Defer(new AuditEntry(ExternalChangeOperation, "queue-item", item.Id.ToString(), AuditOutcome.Success));
        }

        if (await SaveCompletionAsync(item, pendente.OperationId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        await FlushAuditAsync(cancellationToken);
        return new QueueItemOperationResult(QueueItemView.From(item), OperationOutcome.Completed);
    }

    /// <summary>Outro executor, com cerca maior, assumiu a operação: este não escreve nem anota nada, e a conclusão fica com o outro.</summary>
    private static QueueItemOperationResult Superseded(QueueItem item) => new(QueueItemView.From(item), OperationOutcome.Pending);

    /// <summary>
    /// A operação pendente já não está no item: outra tentativa a concluiu ou a desfez. Devolve o resultado gravado no recibo dela (a
    /// recusa, se foi desfeita), sem escrever nada no Conversations.
    /// </summary>
    private async Task<QueueItemOperationResult> ResolveEndedAsync(QueueItem item, Guid operationId, CancellationToken cancellationToken)
    {
        var recibo = await _items.FindOperationReadOnlyAsync(operationId, cancellationToken);
        if (recibo is not null && OperationReceipts.ToResult(recibo) is { } resultado)
        {
            return resultado;
        }

        if (recibo is null && await _items.OperationAppliedAsync(item.Id, operationId, cancellationToken))
        {
            return new QueueItemOperationResult(QueueItemView.From(item), OperationOutcome.AlreadyApplied);
        }

        throw new ContactCenterConflictException();
    }

    /// <summary>Fecha o recibo da operação, na mesma gravação da conclusão do item. Operação sem recibo (gravada por outro caminho) segue sem ele.</summary>
    private async Task CloseReceiptAsync(QueueItem item, Guid operationId, OperationState state, CancellationToken cancellationToken)
    {
        if (await _items.FindOperationAsync(operationId, cancellationToken) is { IsPending: true } recibo)
        {
            recibo.Complete(state, OperationReceipts.Serialize(QueueItemView.From(item)), _clock.GetUtcNow());
        }
    }

    // A entrega à trilha, depois de a gravação ter sido confirmada. Dentro de uma transação aberta (o consumo dos eventos) não entrega
    // (IDeferredAudit); se a trilha falhar, as entradas continuam guardadas e a reconciliação as entrega: a falha nunca desfaz nem
    // contradiz o que já foi confirmado.
    private async Task FlushAuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audit.FlushAsync(FlushBatch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fica para a reconciliação.
        }
    }

    /// <summary>
    /// A proveniência da operação que o item guardou na intenção (o ator, a origem e o instante do PEDIDO): quem conclui ou desfaz a
    /// intenção, inclusive o job de reconciliação depois de uma queda, audita com ela. Intenção anterior a este campo (sem origem) usa o
    /// contexto de quem conclui, como antes.
    /// </summary>
    private static AuditProvenance? ProvenanceOf(PendingOperation pendente) =>
        pendente.ActorOrigin is { } origem ? new AuditProvenance(pendente.ActorUserId, origem, pendente.StartedAt) : null;

    private void DeferAudit(AuditEntry entrada, PendingOperation pendente)
    {
        if (ProvenanceOf(pendente) is { } proveniencia)
        {
            _audit.Defer(entrada, proveniencia);
        }
        else
        {
            _audit.Defer(entrada);
        }
    }

    /// <summary>
    /// Conclui a operação com o RECIBO dela (<c>Applied</c>, ou <c>AlreadyApplied</c> da repetição): a versão, a fronteira e o período vêm do registro da PRÓPRIA
    /// operação, que não depende do estado de agora. O período do recibo é CONFERIDO contra o esperado da base (<see cref="ScopeProof.ExpectedPeriod"/>): a entrada
    /// vinda da IA abre o período seguinte; já em espera humana, o mesmo. Divergente, ou ausente (recibo sem período: sem efeito, ou anterior ao campo), o item
    /// NÃO adota período algum (identidade desconhecida, acesso fechado): o período CORRENTE da sessão nunca o substitui.
    /// <para>
    /// A fronteira de uma devolução à IA só é CERTIFICADA quando o recibo prova a mudança (sequência e período) e o item está preso ao período e à geração da base. A
    /// obrigação de ressincronização só é COBERTA (<see cref="QueueItem.CompletePending"/>) com essa mesma prova e as identidades relevantes conferidas: versão maior
    /// ou estado igual ao de agora não bastam.
    /// </para>
    /// </summary>
    private Task<QueueItemOperationResult> CompleteFromReceiptAsync(
        QueueItem item, PendingOperation pendente, TransitionResult recibo, bool withoutTarget, long? placementEpoch, CancellationToken cancellationToken)
    {
        var esperado = ScopeProof.ExpectedPeriod(pendente);
        var periodoConferido = recibo.HandlingPeriod is { } doRecibo && doRecibo == esperado ? doRecibo : (long?)null;

        // Cobrir a obrigação e certificar a fronteira exigem a prova do efeito E que ele cobre as identidades relevantes: sem a base completa (o reconhecimento por
        // evidência de uma intenção inerte) a colocação do efeito não é uma identidade comprovada, e a operação só conclui o item, sem liquidar nem certificar nada.
        var efeitoComprovado = ScopeProof.BaseOf(pendente).IsComplete
            && recibo.MessageSeq is not null && periodoConferido is not null && recibo.StateVersion is not null;

        var presoAoVinculo = pendente.Basis is { } basis
            && item.HandlingPeriod is { } periodoDoItem && periodoDoItem == basis.HandlingPeriod
            && item.PlacementEpoch is { } geracaoDoItem && geracaoDoItem == basis.PlacementEpoch;
        var fronteiraCertificada = pendente.Kind == PendingKind.ReturnToAi && efeitoComprovado && presoAoVinculo;

        return CompleteAsync(
            item, pendente, recibo.StateVersion, recibo.MessageSeq, periodoConferido, withoutTarget, placementEpoch, fronteiraCertificada,
            efeitoComprovado ? recibo.StateVersion : null, cancellationToken);
    }

    /// <summary>
    /// O período a que o item se prende quando a operação NÃO mudou nada (a sessão já estava no estado pedido, e o recibo é "sem efeito", sem período). Só existe
    /// quando a operação foi AUTORIZADA já em atendimento humano (a base não parte da IA) e a sessão continua NO PERÍODO que a base autorizou: é a identidade da
    /// própria autorização, e não "o período de agora". Entrada que partiu da IA, ou sessão em outro período (humano → IA → humano), não tem prova: nulo, e o
    /// acesso do item fica fechado.
    /// </summary>
    private static long? PeriodOfAuthorizedState(PendingOperation pendente, SessionView sessao) =>
        pendente.Basis is { InAiMode: false } basis && sessao.HandlingMode == HandlingMode.HumanAssisted && sessao.HandlingPeriod == basis.HandlingPeriod
            ? basis.HandlingPeriod
            : null;

    /// <summary>
    /// A intenção SEM a base completa não tem autoridade para escrever no Conversations (nem a cerca), e fica inerte. O que o Conversations já aplicou é
    /// reconhecido por EVIDÊNCIA de leitura, pelo identificador que a intenção guardou (a transição, a transição derivada e o encaminhamento, que são
    /// distintos), nunca por um comando modificador: <c>Applied</c> conclui localmente, vinculando só identidades comprovadas pelo recibo; <c>NotFound</c> não
    /// presume falha nem concede autoridade; <c>Unavailable</c> preserva a pendência para repetir; <c>Indeterminate</c> preserva a evidência e não conclui nem
    /// repete. A sessão encerrada ou inexistente acaba o item, como sempre: isso não exige autoridade alguma.
    /// </summary>
    private async Task<QueueItemOperationResult> RecognizeWithoutAuthorityAsync(QueueItem item, PendingOperation pendente, CancellationToken cancellationToken)
    {
        SessionView? sessao;
        try
        {
            sessao = await _conversations.GetSessionAsync(item.SessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await StayPendingAsync(item, ex.GetType().Name, cancellationToken);
        }

        // O que a operação não inclui (o encaminhamento, a transição derivada) não é perguntado: vale "nada encontrado", que não prova nada.
        var naoPerguntado = new EffectView(EffectOutcome.NotFound);
        var encaminhamento = pendente.BusinessUnitId is null
            ? naoPerguntado
            : await QueryEffectAsync(item.SessionId, EffectKind.Transfer, pendente.TransferId, cancellationToken);
        var transicao = await QueryEffectAsync(item.SessionId, EffectKind.Transition, pendente.TransitionId, cancellationToken);
        var derivada = transicao.Outcome == EffectOutcome.NotFound && pendente.Kind == PendingKind.TransferQueue && pendente.UserId is not null
            ? await QueryEffectAsync(item.SessionId, EffectKind.Transition, ScopeProof.DerivedTransitionId(pendente.TransitionId), cancellationToken)
            : naoPerguntado;

        // A unidade só vale como aplicada pela EVIDÊNCIA do encaminhamento, nunca por a sessão estar numa unidade igual à pedida.
        var unidadeConfirmada = encaminhamento.Outcome == EffectOutcome.Applied;
        if (sessao is null || sessao.Status == SessionStatus.Closed)
        {
            return await EndBecauseSessionEndedAsync(item, pendente, sessao, unidadeConfirmada, cancellationToken);
        }

        if (encaminhamento.Outcome == EffectOutcome.Unavailable || transicao.Outcome == EffectOutcome.Unavailable || derivada.Outcome == EffectOutcome.Unavailable)
        {
            return await StayPendingAsync(item, nameof(EffectOutcome.Unavailable), cancellationToken);
        }

        if (transicao.Outcome == EffectOutcome.Applied || derivada.Outcome == EffectOutcome.Applied)
        {
            var aplicada = transicao.Outcome == EffectOutcome.Applied ? transicao : derivada;
            var recibo = new TransitionResult(TransitionOutcome.AlreadyApplied, aplicada.StateVersion, aplicada.MessageSeq, aplicada.HandlingPeriod);
            return await CompleteFromReceiptAsync(item, pendente, recibo, transicao.Outcome != EffectOutcome.Applied, ScopeProof.ExpectedEpoch(pendente), cancellationToken);
        }

        if (transicao.Outcome == EffectOutcome.Indeterminate || derivada.Outcome == EffectOutcome.Indeterminate || encaminhamento.Outcome == EffectOutcome.Indeterminate)
        {
            return await StayPendingAsync(item, nameof(EffectOutcome.Indeterminate), cancellationToken);
        }

        if (unidadeConfirmada)
        {
            // A unidade JÁ foi aplicada (evidência), e a segunda parte não pode ser escrita sem a base: a parte confirmada não se desfaz (ver StopAfterUnitAsync).
            return await StopAfterUnitAsync(item, pendente, sessao, true, false, ChangedMessage, ChangedAfterUnitMessage, cancellationToken);
        }

        // Nada comprovado e nenhuma autoridade nova: a intenção segue inerte, com a evidência preservada, e a reconciliação a reavalia com o recuo.
        return await StayPendingAsync(item, "BaseIncomplete", cancellationToken);
    }

    /// <summary>A consulta de efeito, que não escreve nem trava. Falha de infraestrutura ou tempo esgotado é <see cref="EffectOutcome.Unavailable"/> (o provedor nunca a devolve).</summary>
    private async Task<EffectView> QueryEffectAsync(Guid sessionId, EffectKind kind, Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            return await _conversations.GetEffectAsync(new GetEffectQuery(sessionId, kind, operationId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EffectView(EffectOutcome.Unavailable);
        }
    }

    private async Task<QueueItemOperationResult> CompleteAsync(
        QueueItem item, PendingOperation pendente, long? stateVersion, long? messageSeq, long? handlingPeriod, bool withoutTarget, long? placementEpoch,
        bool certifiedBoundary, long? coversResyncThrough, CancellationToken cancellationToken)
    {
        var historico = item.CompletePending(stateVersion, _clock.GetUtcNow(), messageSeq, withoutTarget, handlingPeriod, placementEpoch, certifiedBoundary, coversResyncThrough);
        _items.AddHistory(historico);
        await CloseReceiptAsync(item, pendente.OperationId, OperationState.Applied, cancellationToken);

        // A auditoria entra na MESMA gravação do item, do histórico e do recibo, com a proveniência do PEDIDO (não a de quem conclui).
        DeferAudit(new AuditEntry(OperationName(historico.Kind), "queue-item", item.Id.ToString(), AuditOutcome.Success), pendente);
        if (withoutTarget)
        {
            DeferAudit(new AuditEntry(TargetUnavailableOperation, "queue-item", item.Id.ToString(), AuditOutcome.Denied), pendente);
        }

        if (await SaveCompletionAsync(item, pendente.OperationId, cancellationToken) is { } repetida)
        {
            return repetida;
        }

        await FlushAuditAsync(cancellationToken);
        return new QueueItemOperationResult(QueueItemView.From(item), OperationOutcome.Completed);
    }

    /// <param name="sessao">
    /// A sessão como foi lida quando se soube que terminou (a visão traz a sequência da última mensagem, a unidade, o setor, o período e o modo),
    /// ou <c>null</c> se ela já não existe: a fronteira do histórico é decidida sobre ela (<see cref="HistoryBoundary"/>), nunca só pela sequência.
    /// </param>
    /// <param name="unitConfirmed">
    /// A operação era uma transferência de unidade e o Conversations já a tinha aplicado quando a sessão terminou. O item é encerrado já na fila,
    /// na unidade e no setor onde a conversa estava: o histórico que ela deixou depois da transferência não é da fila de origem. O efeito
    /// confirmado vai para a trilha na MESMA gravação do encerramento, com a proveniência do pedido, separado do registro do encerramento da sessão.
    /// </param>
    private async Task<QueueItemOperationResult> EndBecauseSessionEndedAsync(
        QueueItem item, PendingOperation pendente, SessionView? sessao, bool unitConfirmed, CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var moveu = unitConfirmed && pendente is { Kind: PendingKind.TransferQueue, BusinessUnitId: not null } && !item.IsClosed;
        if (moveu)
        {
            _items.AddHistory(item.AdoptConfirmedTransferDestination(agora, QueueItem.SessionEndedSuffix, ScopeProof.ExpectedEpoch(pendente)));
        }

        // O histórico tem um registro por identificador: o do fim da sessão usa um derivado quando a transferência já deixou o da operação. A
        // fronteira é calculada DEPOIS de o item acompanhar o destino: o lugar dele é o da sessão.
        var fronteira = HistoryBoundary.ForEndedSession(item, sessao);
        var historico = item.CloseBecauseSessionEnded(
            moveu ? DerivedHistoryId("partial-transfer:ended", pendente.OperationId) : pendente.OperationId, SessionEndedReason, agora, fronteira.Seq, fronteira.Certified);
        if (historico is not null)
        {
            _items.AddHistory(historico);
            await CloseReceiptAsync(item, pendente.OperationId, OperationState.SessionEnded, cancellationToken);
            if (moveu)
            {
                // O que o Conversations confirmou (a transferência) não se perde porque a sessão terminou antes da conclusão: a auditoria dela tem
                // o ator, a origem e o instante do PEDIDO, e o encerramento da sessão é outra entrada, do contexto de quem o projeta.
                DeferAudit(new AuditEntry(OperationName(HistoryKind.TransferredQueue), "queue-item", item.Id.ToString(), AuditOutcome.Success), pendente);
            }

            _audit.Defer(new AuditEntry(OperationName(HistoryKind.SessionClosed), "queue-item", item.Id.ToString(), AuditOutcome.Success));
            if (await SaveCompletionAsync(item, pendente.OperationId, cancellationToken) is { } repetida)
            {
                return repetida;
            }

            await FlushAuditAsync(cancellationToken);
        }

        return new QueueItemOperationResult(QueueItemView.From(item), OperationOutcome.SessionEnded);
    }

    /// <summary>
    /// Grava a conclusão. Se outra tentativa concluiu a MESMA operação primeiro (o usuário repetiu o pedido enquanto o job
    /// reconciliava), devolve o resultado dela como repetição idempotente; qualquer outra mudança concorrente continua sendo
    /// conflito.
    /// </summary>
    private async Task<QueueItemOperationResult?> SaveCompletionAsync(QueueItem item, Guid operationId, CancellationToken cancellationToken)
    {
        var itemId = item.Id;
        try
        {
            await _items.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (ContactCenterConflictException)
        {
            var recibo = await _items.FindOperationReadOnlyAsync(operationId, cancellationToken);
            if (recibo is not null && OperationReceipts.ToResult(recibo) is { } feito)
            {
                return feito;
            }

            var atual = await _items.FindItemReadOnlyAsync(itemId, cancellationToken);
            if (atual is not null && await _items.OperationAppliedAsync(itemId, operationId, cancellationToken))
            {
                return new QueueItemOperationResult(QueueItemView.From(atual), OperationOutcome.AlreadyApplied);
            }

            throw;
        }
    }

    /// <summary>Anota a falha da tentativa e devolve o item ainda pendente: o estado da conversa não mudou e a operação pode ser repetida.</summary>
    private async Task<QueueItemOperationResult> StayPendingAsync(QueueItem item, string errorType, CancellationToken cancellationToken)
    {
        item.RecordAttemptFailure(errorType, _clock.GetUtcNow());
        try
        {
            await _items.SaveChangesAsync(cancellationToken);
        }
        catch (ContactCenterConflictException)
        {
            // Outra tentativa mexeu no item ao mesmo tempo: a anotação de diagnóstico se perde e a operação continua pendente.
        }

        return new QueueItemOperationResult(QueueItemView.From(item), OperationOutcome.Pending);
    }

    /// <summary>
    /// Desfaz a operação pendente porque o destino foi recusado de forma definitiva (ou o atendimento mudou), deixa a negação na auditoria e
    /// devolve a exceção a lançar. Só se chega aqui quando o Conversations NÃO confirmou parte da operação que o item precisaria acompanhar: a
    /// transferência de unidade já aplicada é concluída em parte (<see cref="CompletePartialTransferAsync"/>), e o item nunca fica na fila de
    /// origem com a unidade de destino.
    /// </summary>
    /// <param name="unitKept">A ENTRADA já tinha aplicado a unidade na conversa quando foi desfeita: a unidade é mantida e fica registrada na trilha.</param>
    private async Task<ContactCenterRuleException> CancelAsync(
        QueueItem item, PendingOperation pendente, ContactCenterRule rule, string message, CancellationToken cancellationToken, bool unitKept = false)
    {
        var agora = _clock.GetUtcNow();
        item.CancelPending(RejectedReason, agora);

        // O recibo registra a recusa (repetir o pedido devolve a mesma recusa). A entrada recusada não deixa recibo: o pedido de
        // atendimento ainda pode tentar o destino seguinte, com o mesmo identificador.
        if (await _items.FindOperationAsync(pendente.OperationId, cancellationToken) is { IsPending: true } recibo)
        {
            if (pendente.Kind == PendingKind.Enter)
            {
                _items.RemoveOperation(recibo);
            }
            else
            {
                recibo.Cancel((int)rule, agora);
            }
        }

        DeferAudit(new AuditEntry(OperationName(pendente.Kind), "queue-item", item.Id.ToString(), AuditOutcome.Denied), pendente);
        if (unitKept)
        {
            DeferAudit(new AuditEntry(EnterUnitKeptOperation, "queue-item", item.Id.ToString(), AuditOutcome.Success), pendente);
        }

        await _items.SaveChangesAsync(cancellationToken);
        await FlushAuditAsync(cancellationToken);
        return new ContactCenterRuleException(rule, message);
    }
}
