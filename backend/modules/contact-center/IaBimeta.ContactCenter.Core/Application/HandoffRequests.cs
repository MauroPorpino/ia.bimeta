using IaBimeta.ContactCenter.Contracts;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// A fachada de <see cref="IHandoffRequests"/> (ADR 0017, seção B): delega ao <see cref="QueueItemService.RequestHandoffAsync"/> que já existe e
/// só traduz o resultado e as recusas de negócio para o contrato. NÃO cria regra de roteamento, de fila nem de transferência: toda decisão (se, para
/// qual fila, com que prioridade) continua sendo do <see cref="QueueItemService"/>, com a mesma validação, autorização, auditoria e idempotência.
/// O tenant e o usuário vêm do contexto confiável; este tipo não os recebe.
/// <para>
/// Recusa nunca vira sucesso: uma regra que impede o encaminhamento é <see cref="RequestHandoffOutcome.NotRoutable"/>, e as demais recusas de negócio
/// (inexistente ou invisível, acesso negado, pedido inválido, conflito) são <see cref="RequestHandoffOutcome.Refused"/>. O que não é recusa de
/// negócio (falha de infraestrutura, contexto de tenant ausente, cancelamento) segue como exceção.
/// </para>
/// </summary>
public sealed class HandoffRequests : IHandoffRequests
{
    private readonly Func<Guid, Guid?, Guid, string, CancellationToken, Task<QueueItemOperationResult>> _handoff;

    /// <summary>Delega ao caso de uso que já existe. É o único construtor público (o que a injeção de dependência usa).</summary>
    public HandoffRequests(QueueItemService items)
        : this((items ?? throw new ArgumentNullException(nameof(items))).RequestHandoffAsync)
    {
    }

    /// <summary>A mesma fachada sobre uma função: permite provar, sem banco, a ordem e a origem dos argumentos entregues ao caso de uso.</summary>
    internal HandoffRequests(Func<Guid, Guid?, Guid, string, CancellationToken, Task<QueueItemOperationResult>> handoff)
    {
        _handoff = handoff;
    }

    public Task<RequestHandoffResult> RequestHandoffAsync(RequestHandoffCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return TranslateAsync(() => _handoff(command.SessionId, command.QueueId, command.RequestId, command.Reason, cancellationToken));
    }

    /// <summary>Executa a operação e traduz o resultado e as recusas de negócio. É a única lógica desta fachada, e é testável sem banco.</summary>
    internal static async Task<RequestHandoffResult> TranslateAsync(Func<Task<QueueItemOperationResult>> operation)
    {
        try
        {
            return FromOperation(await operation());
        }
        catch (ContactCenterRuleException regra)
        {
            return FromRule(regra.Rule);
        }
        catch (QueueItemConflictException conflito)
        {
            return FromConflict(conflito.Conflict);
        }

        // A conversa inexistente e a que o chamador não enxerga são indistinguíveis de propósito (TEN-2): o mesmo resultado e o mesmo código.
        catch (Exception falha) when (falha is ContactCenterResourceNotFoundException or AccessDeniedException)
        {
            return Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.SessionUnavailable);
        }
        catch (ContactCenterValidationException)
        {
            return Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.InvalidRequest);
        }

        // Outra operação gravou primeiro: repetir com o MESMO identificador relê o recibo (repetição do mesmo pedido, ou reuso indevido do identificador).
        catch (Exception falha) when (falha is ContactCenterConflictException or OperationIdInUseException)
        {
            return Refuse(RequestHandoffOutcome.Refused, retryable: true, falha is OperationIdInUseException ? Codes.RequestIdInUse : Codes.ConcurrentUpdate);
        }
    }

    /// <summary>Os códigos estáveis da causa da recusa (<see cref="RequestHandoffResult.Code"/>). Só diagnóstico: o consumidor decide por desfecho e por <c>Retryable</c>.</summary>
    internal static class Codes
    {
        public const string SessionUnavailable = "session-unavailable";
        public const string InvalidRequest = "invalid-request";
        public const string RequestIdInUse = "request-id-in-use";
        public const string ConcurrentUpdate = "concurrent-update";
        public const string Refused = "refused";
        public const string SessionClosed = "session-closed";
        public const string QueueDoesNotCoverSession = "queue-does-not-cover-session";
        public const string QueuePaused = "queue-paused";
        public const string QueueClosed = "queue-closed";
        public const string TargetNotQueueMember = "target-not-queue-member";
        public const string UnitNotAuthorizedForChannel = "unit-not-authorized-for-channel";
        public const string TargetUnitNotAllowed = "target-unit-not-allowed";
        public const string NoRoute = "no-route";
        public const string AgentAtCapacity = "agent-at-capacity";
        public const string AlreadyHandledByHuman = "already-handled-by-human";
        public const string ConversationChanged = "conversation-changed";
        public const string ConcurrentChange = "concurrent-change";
        public const string OperationInProgress = "operation-in-progress";
        public const string AlreadyAssigned = "already-assigned";
        public const string ItemClosed = "item-closed";
        public const string AlreadyInAnotherQueue = "already-in-another-queue";
        public const string NothingToChange = "nothing-to-change";
        public const string RequestIdReused = "request-id-reused";
        public const string Conflict = "conflict";
    }

    private static RequestHandoffResult Refuse(RequestHandoffOutcome outcome, bool retryable, string code) =>
        new(outcome, QueueItemId: null, QueueId: null, retryable, code);

    internal static RequestHandoffResult FromOperation(QueueItemOperationResult resultado)
    {
        var desfecho = resultado.Outcome switch
        {
            OperationOutcome.Completed => RequestHandoffOutcome.Completed,
            OperationOutcome.AlreadyApplied => RequestHandoffOutcome.AlreadyApplied,
            OperationOutcome.Pending => RequestHandoffOutcome.Pending,
            OperationOutcome.SessionEnded => RequestHandoffOutcome.SessionEnded,

            // Falha fechada: um desfecho que esta fachada não conhece nunca é apresentado como sucesso.
            _ => throw new InvalidOperationException($"O resultado '{resultado.Outcome}' do pedido de atendimento humano não é conhecido."),
        };

        return new RequestHandoffResult(desfecho, resultado.Item.Id, resultado.Item.QueueId);
    }

    /// <summary>
    /// Traduz uma regra de negócio do ContactCenter. Transitórias (repetir depois pode dar certo, sem ninguém mudar configuração): a fila pausada, a fila fora do
    /// horário, o atendente sem vaga e a conversa que mudou durante o pedido. As demais dependem de configuração ou de estado que não muda por si só.
    /// </summary>
    internal static RequestHandoffResult FromRule(ContactCenterRule regra) => regra switch
    {
        // A conversa já terminou: não há o que transferir.
        ContactCenterRule.SessionClosed => Refuse(RequestHandoffOutcome.SessionEnded, retryable: false, Codes.SessionClosed),

        // Nenhuma fila pode receber a conversa: transitório (a pausa acaba, o horário abre, abre vaga) ou definitivo (cobertura, rota, autorização).
        ContactCenterRule.QueuePaused => Refuse(RequestHandoffOutcome.NotRoutable, retryable: true, Codes.QueuePaused),
        ContactCenterRule.QueueClosed => Refuse(RequestHandoffOutcome.NotRoutable, retryable: true, Codes.QueueClosed),
        ContactCenterRule.AgentAtCapacity => Refuse(RequestHandoffOutcome.NotRoutable, retryable: true, Codes.AgentAtCapacity),
        ContactCenterRule.QueueDoesNotCoverSession => Refuse(RequestHandoffOutcome.NotRoutable, retryable: false, Codes.QueueDoesNotCoverSession),
        ContactCenterRule.TargetNotQueueMember => Refuse(RequestHandoffOutcome.NotRoutable, retryable: false, Codes.TargetNotQueueMember),
        ContactCenterRule.UnitNotAuthorizedForChannel => Refuse(RequestHandoffOutcome.NotRoutable, retryable: false, Codes.UnitNotAuthorizedForChannel),
        ContactCenterRule.TargetUnitNotAllowed => Refuse(RequestHandoffOutcome.NotRoutable, retryable: false, Codes.TargetUnitNotAllowed),
        ContactCenterRule.NoRoute => Refuse(RequestHandoffOutcome.NotRoutable, retryable: false, Codes.NoRoute),

        // A conversa mudou durante o pedido: o ContactCenter manda reler e pedir de novo.
        ContactCenterRule.ConversationChanged => Refuse(RequestHandoffOutcome.Refused, retryable: true, Codes.ConversationChanged),

        // Já tem atendente humano definido fora do ContactCenter: definitivo.
        ContactCenterRule.ConversationAlreadyHandledByHuman => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.AlreadyHandledByHuman),

        // Qualquer regra futura que esta fachada ainda não conheça: recusa DEFINITIVA, nunca sucesso e nunca repetição às cegas.
        _ => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.Refused),
    };

    /// <summary>
    /// Traduz um conflito de estado do item de fila. Transitórios (o próprio ContactCenter manda releia e repita): a operação em andamento e a mudança
    /// concorrente do item. Os demais descrevem um estado que repetir não muda.
    /// </summary>
    internal static RequestHandoffResult FromConflict(QueueItemConflict conflito) => conflito switch
    {
        QueueItemConflict.ConcurrentChange => Refuse(RequestHandoffOutcome.Refused, retryable: true, Codes.ConcurrentChange),
        QueueItemConflict.OperationInProgress => Refuse(RequestHandoffOutcome.Refused, retryable: true, Codes.OperationInProgress),
        QueueItemConflict.AlreadyAssigned => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.AlreadyAssigned),
        QueueItemConflict.ItemClosed => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.ItemClosed),
        QueueItemConflict.ActiveItemExists => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.AlreadyInAnotherQueue),
        QueueItemConflict.NothingToChange => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.NothingToChange),
        QueueItemConflict.RequestIdReused => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.RequestIdReused),

        // Um conflito futuro que esta fachada ainda não conheça: definitivo.
        _ => Refuse(RequestHandoffOutcome.Refused, retryable: false, Codes.Conflict),
    };
}
