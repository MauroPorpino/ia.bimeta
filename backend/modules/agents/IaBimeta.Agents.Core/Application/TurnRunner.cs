using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;

namespace IaBimeta.Agents.Application;

/// <summary>Como uma execução do turno terminou (para o executor medir e registrar). O código é curto e estável, nunca conteúdo de mensagem.</summary>
public enum TurnRunOutcome
{
    /// <summary>O turno não estava com este executor (já terminou, ou outro executor o levou).</summary>
    Skipped = 1,

    /// <summary>A resposta do modelo foi aceita pelo Conversations (T5).</summary>
    Replied = 2,

    /// <summary>A resposta informativa determinística (áudio, MED-3) foi aceita.</summary>
    InformativeReply = 3,

    /// <summary>O atendimento humano foi pedido ao ContactCenter (decisão da IA, política de conteúdo ou falha permanente).</summary>
    HandedOff = 4,

    /// <summary>O grupo tem conteúdo pendente dentro da espera adicional: o turno espera, sem modelo.</summary>
    WaitingForContent = 5,

    /// <summary>O turno volta a <c>Ready</c> (falha transitória, capacidade, encerramento limpo).</summary>
    Retrying = 6,

    /// <summary>A resposta foi recusada pelas cercas, ou não havia o que responder (T7, T7b).</summary>
    Discarded = 7,

    /// <summary>O responsável deixou de ser a IA, a sessão acabou, ou o turno foi cancelado (T4b, T8).</summary>
    Cancelled = 8,

    /// <summary>O turno morreu por falha permanente ou tentativas esgotadas (T10).</summary>
    Dead = 9,

    /// <summary>Outra operação (uma mensagem nova, a mudança de modo) alterou o turno durante a execução: nada mais é gravado por este executor.</summary>
    Lost = 10,
}

public sealed record TurnRunResult(TurnRunOutcome Outcome, string Code);

/// <summary>
/// Executa UM turno já reivindicado (T4 a T11): <c>BeginTurn</c>, snapshot da sessão autorizada, política de conteúdo, prompt, <see cref="IAiGateway"/>, validação da decisão e o registro da resposta ou o
/// pedido de handoff. O Agents PROPÕE; quem aceita a resposta é o Conversations, pelas cercas transacionais (turno ativo, época, <c>StateVersion</c>, cerca de entrada): a resposta defasada é sempre recusada
/// e o cancelamento físico da chamada é só otimização. O modelo SUGERE: nada é executado fora da lista fechada de ações, e toda falha fecha para o lado seguro (transferir, nunca inventar). Roda no
/// contexto de SISTEMA do tenant do turno, criado pelo host. Sem I/O próprio além das portas, e sem log: o resultado devolve o código para o executor registrar.
/// </summary>
public sealed class TurnRunner
{
    private const string AlreadyHandledByHuman = "already-handled-by-human";

    private readonly IAiTurnStore _store;
    private readonly ITurnCommands _turns;
    private readonly IAiGateway _gateway;
    private readonly IHandoffRequests _handoff;
    private readonly IAgentDefinitionReader _definitions;
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;

    public TurnRunner(
        IAiTurnStore store,
        ITurnCommands turns,
        IAiGateway gateway,
        IHandoffRequests handoff,
        IAgentDefinitionReader definitions,
        AgentsOptions options,
        TimeProvider clock)
    {
        _store = store;
        _turns = turns;
        _gateway = gateway;
        _handoff = handoff;
        _definitions = definitions;
        _options = options;
        _clock = clock;
    }

    /// <summary>Executa o turno <paramref name="turnId"/>, que o executor reivindicou (estado <c>Running</c> com o token de lane).</summary>
    public async Task<TurnRunResult> RunAsync(Guid turnId, CancellationToken cancellationToken)
    {
        var turno = await _store.FindAsync(turnId, cancellationToken);
        if (turno is null || turno.State != AiTurnState.Running || turno.LaneToken is not { } lane)
        {
            return new TurnRunResult(TurnRunOutcome.Skipped, "not-running");
        }

        try
        {
            return await RunClaimedAsync(turno, lane, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // T11: encerramento limpo do worker. O turno volta a Ready sem gastar tentativa; nenhuma garantia de correção depende disto (as cercas do Conversations).
            await ApplyAsync(turnId, lane, t => { t.ReleaseForRetry(_clock.GetUtcNow(), _options.CleanShutdownRetry, countAttempt: false); return null; });
            return new TurnRunResult(TurnRunOutcome.Retrying, "shutdown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha inesperada (banco, defeito de um adaptador): conta como falha transitória da tentativa. O TIPO vai no código; a mensagem pode carregar dado.
            return await FailAttemptAsync(turno, lane, "exception-" + ex.GetType().Name, null, null, null);
        }
    }

    private async Task<TurnRunResult> RunClaimedAsync(AiTurn turno, long lane, CancellationToken ct)
    {
        // Handoff já decidido por uma execução anterior: só se repete o pedido (idempotente pelo TurnId), sem nova chamada ao modelo.
        if (turno.HandoffReason is not null)
        {
            return await ResumeHandoffAsync(turno, lane, ct);
        }

        // T4: assume o turno ativo no Conversations e recebe o grant e o snapshot da MESMA leitura consistente.
        var inicio = await _turns.BeginTurnAsync(new BeginTurnCommand(turno.SessionId, turno.Id, lane, _options.MaxInputMessages), ct);
        switch (inicio.Outcome)
        {
            case BeginTurnOutcome.SessionNotFound:
                return await CancelAsync(turno, lane, "session-not-found");
            case BeginTurnOutcome.SessionClosed:
                return await CancelAsync(turno, lane, "session-closed");
            case BeginTurnOutcome.NotAiResponder:
                return await CancelAsync(turno, lane, "not-ai-responder");
            case BeginTurnOutcome.LeaseTokenStale:
                return await RetryAsync(turno, lane, _options.CleanShutdownRetry, countAttempt: false, "lease-token-stale");
        }

        var concessao = inicio.Grant!;
        var snapshot = inicio.Snapshot!;

        // O grupo: as mensagens do cliente ainda sem resposta. Nenhuma: nada a fazer (a resposta já cobriu, ou o retorno à IA veio sem pendentes).
        var grupo = snapshot.Messages.Where(m => m.IsPending).OrderBy(m => m.MessageSeq).ToList();
        if (grupo.Count == 0)
        {
            await EndConversationTurnAsync(turno, concessao, TurnEndReason.Discarded);
            return await DiscardAsync(turno, lane, "nothing-pending");
        }

        // MED-1 e MED-2: o modelo nunca vê conteúdo que não está pronto. Dentro do prazo adicional o turno espera; vencido, vale a política do tipo, ao grupo inteiro.
        var conteudo = ContentPolicy.Evaluate(grupo, turno.ContentWaitStartedAt, _clock.GetUtcNow(), _options);
        if (conteudo.Verdict == ContentVerdict.Wait)
        {
            await EndConversationTurnAsync(turno, concessao, TurnEndReason.Discarded);
            return await ApplyAsync(turno.Id, lane, t => { t.DeferForContent(_clock.GetUtcNow(), _options.ContentRecheck); return null; })
                ? new TurnRunResult(TurnRunOutcome.WaitingForContent, "content-pending")
                : Lost();
        }

        var definicao = await _definitions.GetPublishedAsync(ct);

        if (conteudo.Verdict == ContentVerdict.Handoff)
        {
            return await HandoffFlowAsync(turno, lane, concessao, snapshot, definicao, HandoffReasons.ContentUnavailable, "content-unavailable", failure: false, ct);
        }

        if (conteudo.Verdict == ContentVerdict.InformativeReply)
        {
            // MED-3: resposta determinística, texto aprovado do tenant, sem modelo. Sem texto aprovado, o atendente (a IA não improvisa).
            if (string.IsNullOrWhiteSpace(definicao?.AudioInformativeText))
            {
                return await HandoffFlowAsync(turno, lane, concessao, snapshot, definicao, HandoffReasons.ContentUnavailable, "content-unavailable", failure: false, ct);
            }

            var informativa = await RegisterAsync(turno, concessao, snapshot, ReplyKind.Informative, definicao.AudioInformativeText, Array.Empty<Guid>(), ct);
            return await AfterRegisterAsync(turno, lane, concessao, snapshot, informativa, "informative-reply", TurnRunOutcome.InformativeReply, definicao, ct);
        }

        // Sem definição publicada o agente não tem instruções aprovadas: falha fechada, o atendente.
        if (definicao is null)
        {
            return await HandoffFlowAsync(turno, lane, concessao, snapshot, null, HandoffReasons.AiNotConfigured, "ai-not-configured", failure: true, ct);
        }

        var mensagens = AgentPrompt.BuildMessages(snapshot);
        if (mensagens.Count == 0)
        {
            // Conteúdo pronto, mas sem texto para o modelo (por exemplo, uma mídia já processada sem legenda): o modelo não tem o que responder.
            return await HandoffFlowAsync(turno, lane, concessao, snapshot, definicao, HandoffReasons.ContentUnavailable, "no-text-input", failure: false, ct);
        }

        var pedido = new AiGenerationRequest(
            turno.Id.ToString(),
            AgentPrompt.BuildSystemInstruction(definicao, snapshot),
            mensagens,
            _options.MaxOutputTokens,
            _options.ModelCallTimeout,
            AiOutputMode.StructuredJson,
            AgentPrompt.DecisionSchema);

        var resultado = await _gateway.GenerateAsync(pedido, ct);

        // Voltou a chamada: o turno ainda é nosso? (CAN-2: cancelar a chamada física é otimização; esta releitura só poupa o registro, a correção é das cercas do Conversations.)
        var vivo = await _store.FindAsync(turno.Id, CancellationToken.None);
        if (vivo is null || vivo.State != AiTurnState.Running || vivo.LaneToken != lane)
        {
            if (vivo?.State == AiTurnState.Superseded)
            {
                await EndConversationTurnAsync(turno, concessao, TurnEndReason.Superseded);
            }

            return Lost();
        }

        if (!resultado.IsSuccess)
        {
            return await HandleFailureAsync(turno, lane, resultado.Failure!, concessao, snapshot, definicao, ct);
        }

        switch (resultado.Completion!.StopReason)
        {
            case AiStopReason.Refused:
                return await FailPermanentlyAsync(turno, lane, "model-refused", concessao, snapshot, definicao, ct);
            case AiStopReason.Truncated:
                return await FailAttemptAsync(turno, lane, "output-truncated", concessao, snapshot, definicao);
        }

        if (!DecisionParser.TryParse(resultado.Completion.Text, out var decisao, out var invalido))
        {
            return await FailAttemptAsync(turno, lane, "invalid-output-" + invalido, concessao, snapshot, definicao);
        }

        if (decisao!.Action == AgentAction.Handoff)
        {
            return await HandoffFlowAsync(
                turno, lane, concessao, snapshot, definicao, HandoffReasons.ForAiDecision(decisao.HandoffReason!), "handoff-" + decisao.HandoffReason, failure: false, ct);
        }

        var resposta = await RegisterAsync(turno, concessao, snapshot, ReplyKind.Generated, decisao.ReplyText!, new[] { resultado.AiRequestId }, ct);
        return await AfterRegisterAsync(turno, lane, concessao, snapshot, resposta, "replied", TurnRunOutcome.Replied, definicao, ct);
    }

    // O registro da resposta e o que cada recusa significa (CONVERSATION-LIFECYCLE.md §6.1, T5, T7 e T7b).

    private Task<RegisterAiReplyResult> RegisterAsync(
        AiTurn turno, TurnGrant concessao, TurnSnapshot snapshot, ReplyKind tipo, string texto, IReadOnlyList<Guid> aiRequestIds, CancellationToken ct) =>
        _turns.RegisterAiReplyAsync(
            new RegisterAiReplyCommand(
                turno.SessionId, turno.Id, concessao.Epoch, concessao.StateVersion, concessao.BasedOnInboundSeq, tipo, snapshot.ConversationId, texto, aiRequestIds),
            ct);

    private async Task<TurnRunResult> AfterRegisterAsync(
        AiTurn turno,
        long lane,
        TurnGrant concessao,
        TurnSnapshot snapshot,
        RegisterAiReplyResult registro,
        string reason,
        TurnRunOutcome sucesso,
        AgentDefinitionContent? definicao,
        CancellationToken ct)
    {
        switch (registro.Outcome)
        {
            case RegisterReplyOutcome.Accepted:
            case RegisterReplyOutcome.AlreadyRegistered:
                // TRN-1: nenhuma mensagem fica para trás. Numa repetição idempotente (o processo caiu depois de aceita) pode haver mensagem posterior à cobertura: o sucessor a cobre.
                var pendentes = registro.Outcome == RegisterReplyOutcome.AlreadyRegistered
                    && registro.LastInboundSeq is { } ultima
                    && registro.AnsweredThroughSeq is { } coberta
                    && ultima > coberta;
                return await ApplyAsync(
                    turno.Id,
                    lane,
                    t =>
                    {
                        var agora = _clock.GetUtcNow();
                        t.Complete(agora, reason);
                        return pendentes ? AiTurn.Schedule(t.TenantId, t.SessionId, agora, agora, _options, agora, immediate: true) : null;
                    })
                    ? new TurnRunResult(sucesso, reason)
                    : Lost();

            case RegisterReplyOutcome.StaleInput:
            case RegisterReplyOutcome.StaleState:
                // T7: a resposta está defasada (chegou mensagem, ou o estado mudou) e é SEMPRE recusada. O turno é descartado e o sucessor cobre as pendentes (cada descarte exige entrada ou mudança nova).
                await EndConversationTurnAsync(turno, concessao, TurnEndReason.Discarded);
                return await ApplyAsync(
                    turno.Id,
                    lane,
                    t =>
                    {
                        var agora = _clock.GetUtcNow();
                        t.Discard(agora, registro.Outcome == RegisterReplyOutcome.StaleInput ? "stale-input" : "stale-state");
                        return AiTurn.Schedule(t.TenantId, t.SessionId, agora, agora, _options, agora, t.SupersedeCount + 1, immediate: true);
                    })
                    ? new TurnRunResult(TurnRunOutcome.Discarded, registro.Outcome == RegisterReplyOutcome.StaleInput ? "stale-input" : "stale-state")
                    : Lost();

            case RegisterReplyOutcome.TurnNotActive:
                // T7b: quem substituiu ou cancelou o turno cuida do que vem depois.
                return await DiscardAsync(turno, lane, "turn-not-active");

            case RegisterReplyOutcome.NotAiResponder:
                return await DiscardAsync(turno, lane, "not-ai-responder");

            case RegisterReplyOutcome.SessionNotFound:
                return await CancelAsync(turno, lane, "session-not-found");

            default:
                // ShapeNotAllowed: a forma da resposta não é aceita (defeito, não instabilidade). Falha permanente: o atendente.
                return await FailPermanentlyAsync(turno, lane, "reply-shape-not-allowed", concessao, snapshot, definicao, ct);
        }
    }

    // Falhas do modelo (T9 e T10)

    private async Task<TurnRunResult> HandleFailureAsync(
        AiTurn turno, long lane, AiFailure falha, TurnGrant concessao, TurnSnapshot snapshot, AgentDefinitionContent definicao, CancellationToken ct)
    {
        var codigo = falha.Code ?? KindCode(falha.Kind);

        // A saturação das chamadas simultâneas do tenant passa sozinha: o turno espera a capacidade, sem gastar tentativa, até a idade máxima.
        if (falha.IsCapacityBusy)
        {
            if (_clock.GetUtcNow() - turno.CreatedAt > _options.MaxTurnAge)
            {
                return await FailPermanentlyAsync(turno, lane, "capacity-wait-exceeded", concessao, snapshot, definicao, ct);
            }

            return await RetryAsync(turno, lane, _options.CapacityRetry, countAttempt: false, "capacity");
        }

        // Só a falha transitória se tenta de novo (credencial inválida, cota, limite do tenant, pedido inválido, recusa e configuração ausente são permanentes). Nunca troca de provedor.
        return falha.Retryable
            ? await FailAttemptAsync(turno, lane, "ai-" + codigo, concessao, snapshot, definicao, falha.RetryAfter)
            : await FailPermanentlyAsync(turno, lane, "ai-" + KindCode(falha.Kind), concessao, snapshot, definicao, ct);
    }

    /// <summary>T9 ou T10: a tentativa falhou. Abaixo do máximo o turno volta a <c>Ready</c> com recuo exponencial; esgotadas as tentativas, a falha é permanente.</summary>
    private async Task<TurnRunResult> FailAttemptAsync(
        AiTurn turno, long lane, string codigo, TurnGrant? concessao, TurnSnapshot? snapshot, AgentDefinitionContent? definicao, TimeSpan? sugerida = null)
    {
        var tentativas = turno.Attempts + 1;
        if (tentativas >= _options.MaxTurnAttempts)
        {
            return await FailPermanentlyAsync(turno, lane, "attempts-exhausted-" + codigo, concessao, snapshot, definicao, CancellationToken.None);
        }

        return await RetryAsync(turno, lane, RetryDelay(tentativas, _options, sugerida), countAttempt: true, codigo);
    }

    /// <summary>T10 e PRO-5: falha permanente. A política (única no MVP) é transferir ao atendente, com o aviso aprovado do tenant se houver; o turno termina <c>Dead</c> com o motivo.</summary>
    private Task<TurnRunResult> FailPermanentlyAsync(
        AiTurn turno, long lane, string endReason, TurnGrant? concessao, TurnSnapshot? snapshot, AgentDefinitionContent? definicao, CancellationToken ct) =>
        HandoffFlowAsync(turno, lane, concessao, snapshot, definicao, HandoffReasons.AiProcessingFailed, endReason, failure: true, ct);

    // O handoff (MED-4, PRO-5 e a decisão da IA)

    /// <summary>
    /// Guarda a decisão no turno (a retomada só repete o pedido), registra o aviso de transferência do tenant, se houver (uma vez, antes do handoff: depois dele a IA já não responde), e pede o atendimento
    /// humano ao ContactCenter com o <c>TurnId</c> como chave de idempotência.
    /// </summary>
    private async Task<TurnRunResult> HandoffFlowAsync(
        AiTurn turno,
        long lane,
        TurnGrant? concessao,
        TurnSnapshot? snapshot,
        AgentDefinitionContent? definicao,
        string reason,
        string endReason,
        bool failure,
        CancellationToken ct)
    {
        if (!await ApplyAsync(turno.Id, lane, t => { t.RecordHandoff(reason, _clock.GetUtcNow()); return null; }))
        {
            return Lost();
        }

        if (!turno.NoticeDone && concessao is not null && snapshot is not null && !string.IsNullOrWhiteSpace(definicao?.HandoffNoticeText))
        {
            // O aviso afirma ao cliente que ele vai ser transferido, e depois da transferência a IA já não pode falar (as cercas do Conversations). Por isso ele só sai se as regras de roteamento
            // aceitam o pedido AGORA (verificação somente de leitura, nada é gravado). Recusado, seja de vez (nenhuma fila atende a conversa) ou por agora (fila pausada, fora do horário), o cliente
            // não recebe uma promessa que não se cumpre: o pedido de verdade segue, e se um dia passar, o aviso sai antes dele (a retomada verifica de novo).
            var verificacao = await _handoff.RequestHandoffAsync(new RequestHandoffCommand(turno.SessionId, turno.Id, reason, CheckOnly: true), CancellationToken.None);
            switch (verificacao.Outcome)
            {
                case RequestHandoffOutcome.SessionEnded:
                    return await CancelAsync(turno, lane, "session-ended");

                case RequestHandoffOutcome.Refused when verificacao.Code == AlreadyHandledByHuman:
                    return await CancelAsync(turno, lane, "handled-by-human");

                case RequestHandoffOutcome.Routable:
                    var resultadoDoAviso = await RegisterNoticeAsync(turno, lane, concessao, snapshot, definicao.HandoffNoticeText, ct);
                    if (resultadoDoAviso is not null)
                    {
                        return resultadoDoAviso;
                    }

                    break;
            }
        }

        return await RequestHandoffAsync(turno, lane, reason, endReason, failure, concessao);
    }

    /// <summary>Registra o aviso de transferência (uma resposta determinística do tenant). Devolve um resultado quando o turno termina aqui (o humano já assumiu, ou o turno foi perdido); nulo para seguir ao pedido.</summary>
    private async Task<TurnRunResult?> RegisterNoticeAsync(AiTurn turno, long lane, TurnGrant concessao, TurnSnapshot snapshot, string texto, CancellationToken ct)
    {
        var aviso = await RegisterAsync(turno, concessao, snapshot, ReplyKind.Informative, texto, Array.Empty<Guid>(), ct);
        switch (aviso.Outcome)
        {
            case RegisterReplyOutcome.Accepted:
            case RegisterReplyOutcome.AlreadyRegistered:
                await ApplyAsync(turno.Id, lane, t => { t.RecordNoticeDone(_clock.GetUtcNow()); return null; });
                return null;

            case RegisterReplyOutcome.NotAiResponder:
                // O humano já assumiu: não há o que transferir.
                return await CancelAsync(turno, lane, "handled-by-human");

            case RegisterReplyOutcome.TurnNotActive:
                // Substituído ou cancelado por quem tem a autoridade: este executor sai.
                return Lost();

            case RegisterReplyOutcome.SessionNotFound:
                return await CancelAsync(turno, lane, "session-not-found");

            default:
                // StaleInput, StaleState ou forma recusada: o aviso não sai (chegou mensagem nova, ou o estado mudou), mas a transferência continua.
                return null;
        }
    }

    /// <summary>Retoma um handoff já decidido (o processo caiu, ou o ContactCenter respondeu <c>Pending</c>): repete o pedido e, se o aviso ainda não saiu, o registra antes.</summary>
    private async Task<TurnRunResult> ResumeHandoffAsync(AiTurn turno, long lane, CancellationToken ct)
    {
        var motivo = turno.HandoffReason!;
        var falha = motivo is HandoffReasons.AiProcessingFailed or HandoffReasons.AiNotConfigured;

        if (!turno.NoticeDone)
        {
            var definicao = await _definitions.GetPublishedAsync(ct);
            if (!string.IsNullOrWhiteSpace(definicao?.HandoffNoticeText))
            {
                var inicio = await _turns.BeginTurnAsync(new BeginTurnCommand(turno.SessionId, turno.Id, lane, _options.MaxInputMessages), ct);
                switch (inicio.Outcome)
                {
                    case BeginTurnOutcome.Granted:
                        return await HandoffFlowAsync(turno, lane, inicio.Grant, inicio.Snapshot, definicao, motivo, "handoff-resumed", falha, ct);
                    case BeginTurnOutcome.NotAiResponder:
                        // Já está com o humano (o pedido anterior chegou a ser aplicado): nada a repetir.
                        return await CancelAsync(turno, lane, "handled-by-human");
                    case BeginTurnOutcome.SessionClosed:
                    case BeginTurnOutcome.SessionNotFound:
                        return await CancelAsync(turno, lane, "session-closed");
                    case BeginTurnOutcome.LeaseTokenStale:
                        return await RetryAsync(turno, lane, _options.CleanShutdownRetry, countAttempt: false, "lease-token-stale");
                }
            }
        }

        return await RequestHandoffAsync(turno, lane, motivo, "handoff-resumed", falha, null);
    }

    private async Task<TurnRunResult> RequestHandoffAsync(AiTurn turno, long lane, string reason, string endReason, bool failure, TurnGrant? concessao)
    {
        var resultado = await _handoff.RequestHandoffAsync(new RequestHandoffCommand(turno.SessionId, turno.Id, reason));
        switch (resultado.Outcome)
        {
            case RequestHandoffOutcome.Completed:
            case RequestHandoffOutcome.AlreadyApplied:
                return await ApplyAsync(
                    turno.Id,
                    lane,
                    t =>
                    {
                        if (failure)
                        {
                            t.Kill(_clock.GetUtcNow(), endReason);
                        }
                        else
                        {
                            t.Complete(_clock.GetUtcNow(), "handoff:" + endReason);
                        }

                        return null;
                    })
                    ? new TurnRunResult(failure ? TurnRunOutcome.Dead : TurnRunOutcome.HandedOff, endReason)
                    : Lost();

            case RequestHandoffOutcome.Pending:
                // A intenção ficou registrada e o Conversations ainda não confirmou: repetir com o MESMO identificador conclui. Espera limitada pela idade do turno.
                return _clock.GetUtcNow() - turno.CreatedAt > _options.MaxTurnAge
                    ? await KillAsync(turno, lane, "handoff-pending-timeout", concessao)
                    : await RetryAsync(turno, lane, _options.CapacityRetry, countAttempt: false, "handoff-pending");

            case RequestHandoffOutcome.SessionEnded:
                return await CancelAsync(turno, lane, "session-ended");

            default:
                // NotRoutable ou Refused. Transitória (fila pausada, fora do horário, atendente sem vaga, conversa que mudou): repete com o mesmo identificador, gastando tentativa. Definitiva: o
                // humano já está no comando, ou ninguém atende a conversa; o turno morre com o código (o atendimento fica na IA, e a próxima mensagem do cliente abre outro turno).
                if (resultado.Retryable)
                {
                    var tentativas = turno.Attempts + 1;
                    return tentativas >= _options.MaxTurnAttempts
                        ? await KillAsync(turno, lane, "handoff-refused-" + (resultado.Code ?? "retryable"), concessao)
                        : await RetryAsync(turno, lane, RetryDelay(tentativas, _options, null), countAttempt: true, "handoff-" + (resultado.Code ?? "retry"));
                }

                return resultado.Code == AlreadyHandledByHuman
                    ? await CancelAsync(turno, lane, "handled-by-human")
                    : await KillAsync(turno, lane, "handoff-refused-" + (resultado.Code ?? "definitive"), concessao);
        }
    }

    // Transições do turno (todas com a guarda do token de lane: um executor que perdeu o turno não grava nada)

    private async Task<TurnRunResult> CancelAsync(AiTurn turno, long lane, string reason) =>
        await ApplyAsync(turno.Id, lane, t => { t.Cancel(_clock.GetUtcNow(), reason); return null; })
            ? new TurnRunResult(TurnRunOutcome.Cancelled, reason)
            : Lost();

    private async Task<TurnRunResult> DiscardAsync(AiTurn turno, long lane, string reason) =>
        await ApplyAsync(turno.Id, lane, t => { t.Discard(_clock.GetUtcNow(), reason); return null; })
            ? new TurnRunResult(TurnRunOutcome.Discarded, reason)
            : Lost();

    private async Task<TurnRunResult> KillAsync(AiTurn turno, long lane, string reason, TurnGrant? concessao)
    {
        if (concessao is not null)
        {
            await EndConversationTurnAsync(turno, concessao, TurnEndReason.Dead);
        }

        return await ApplyAsync(turno.Id, lane, t => { t.Kill(_clock.GetUtcNow(), reason); return null; })
            ? new TurnRunResult(TurnRunOutcome.Dead, reason)
            : Lost();
    }

    private async Task<TurnRunResult> RetryAsync(AiTurn turno, long lane, TimeSpan delay, bool countAttempt, string code) =>
        await ApplyAsync(turno.Id, lane, t => { t.ReleaseForRetry(_clock.GetUtcNow(), delay, countAttempt); return null; })
            ? new TurnRunResult(TurnRunOutcome.Retrying, code)
            : Lost();

    /// <summary>
    /// Aplica a mudança ao turno e grava (com o sucessor, se a mudança devolver um) numa só transação, sob a guarda do token de lane. Conflito de versão com o handler dos eventos: relê e reaplica, desde que o
    /// turno continue em andamento e deste executor; senão devolve falso (outra operação o encerrou, e este executor sai sem gravar mais nada). Não é cancelável: o estado final nunca fica pela metade.
    /// </summary>
    private async Task<bool> ApplyAsync(Guid turnId, long lane, Func<AiTurn, AiTurn?> mutation)
    {
        for (var tentativa = 0; tentativa < 3; tentativa++)
        {
            var turno = await _store.FindAsync(turnId, CancellationToken.None);
            if (turno is null || turno.State != AiTurnState.Running || turno.LaneToken != lane)
            {
                return false;
            }

            var sucessor = mutation(turno);
            if (sucessor is not null)
            {
                _store.Add(sucessor);
            }

            try
            {
                await _store.SaveAsync(CancellationToken.None);
                return true;
            }
            catch (AiTurnConflictException)
            {
                // Relê e decide de novo: se a mensagem nova trocou o turno, o laço devolve falso na próxima volta.
            }
        }

        return false;
    }

    /// <summary>
    /// Libera o turno ativo no Conversations (<c>EndTurn</c>, idempotente). É higiene e otimização: o turno logicamente ativo é sempre decidido pelas cercas do Conversations, e o próximo <c>BeginTurn</c> assume o
    /// lugar de qualquer um. Falha de infraestrutura aqui não derruba o turno.
    /// </summary>
    private async Task EndConversationTurnAsync(AiTurn turno, TurnGrant concessao, TurnEndReason motivo)
    {
        try
        {
            await _turns.EndTurnAsync(new EndTurnCommand(turno.SessionId, turno.Id, concessao.Epoch, motivo), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Só higiene: o BeginTurn seguinte (ou a cerca da resposta) resolve.
        }
    }

    private static TurnRunResult Lost() => new(TurnRunOutcome.Lost, "state-changed");

    /// <summary>O recuo exponencial das tentativas: base dobrada a cada falha, limitada, e nunca menor que a espera que o provedor sugeriu (também limitada).</summary>
    internal static TimeSpan RetryDelay(int attempt, AgentsOptions options, TimeSpan? suggested)
    {
        var passo = Math.Max(1, attempt);
        var bruto = TimeSpan.FromTicks((long)Math.Min(options.RetryBackoff.Ticks * Math.Pow(2, Math.Min(passo - 1, 20)), options.MaxRetryBackoff.Ticks));
        if (suggested is { } sugerida && sugerida > bruto)
        {
            return sugerida < options.MaxRetryBackoff ? sugerida : options.MaxRetryBackoff;
        }

        return bruto;
    }

    private static string KindCode(AiErrorKind kind) => kind switch
    {
        AiErrorKind.Transient => "transient",
        AiErrorKind.InvalidCredentials => "invalid-credentials",
        AiErrorKind.QuotaOrBilling => "quota-or-billing",
        AiErrorKind.InvalidRequest => "invalid-request",
        AiErrorKind.ContextTooLong => "context-too-long",
        AiErrorKind.Refused => "refused",
        AiErrorKind.TenantLimitExceeded => "tenant-limit-exceeded",
        AiErrorKind.NotConfigured => "not-configured",
        _ => "unknown",
    };
}
