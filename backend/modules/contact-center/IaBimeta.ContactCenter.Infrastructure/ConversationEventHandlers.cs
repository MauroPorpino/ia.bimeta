using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// O consumidor do ContactCenter para os eventos de estado de atendimento do Conversations (BOUNDARIES.md §12.12, etapa 4). A
/// entrega é pelo menos uma vez, então cada handler passa pelo inbox do módulo: o registro de que o evento foi tratado e o efeito
/// na projeção acontecem na MESMA transação, e uma segunda entrega do mesmo <c>EventId</c> não repete o efeito. O que é
/// transitório (item alterado ao mesmo tempo, banco indisponível) volta como nova tentativa e o que é permanente vai para os mortos.
/// </summary>
internal static class ProjectionHandling
{
    /// <summary>O nome estável deste consumidor no inbox do módulo.</summary>
    public const string Consumer = "contact-center.conversation-projection";

    private const int FlushBatch = 50;

    public static async Task<HandlerResult> RunAsync(
        InboxGate<ContactCenterDbContext> inbox, IDeferredAudit audit, EventEnvelope envelope, Func<CancellationToken, Task<ProjectionOutcome>> projetar,
        CancellationToken cancellationToken)
    {
        try
        {
            var resultado = await inbox.RunOnceAsync(
                envelope.TenantId, Consumer, envelope.EventId,
                async token =>
                {
                    await projetar(token);
                    return HandlerResult.Success;
                },
                cancellationToken);

            // A auditoria que a projeção guardou só vai à trilha agora, depois da confirmação do inbox. Se a entrega falhar (a trilha
            // fora do ar, o processo caindo), as entradas continuam guardadas e a reconciliação do Worker as entrega: a falha NÃO
            // desfaz o que já foi confirmado nem pede nova entrega do evento.
            try
            {
                await audit.FlushAsync(FlushBatch, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fica para a reconciliação do Worker (o mesmo que acontece se o processo cair aqui).
            }

            return resultado;
        }
        catch (ContactCenterConflictException)
        {
            // Outra operação alterou o item ao mesmo tempo: a próxima entrega relê e decide de novo.
            return HandlerResult.Retry(reason: nameof(ContactCenterConflictException));
        }
        catch (DbUpdateConcurrencyException)
        {
            return HandlerResult.Retry(reason: nameof(DbUpdateConcurrencyException));
        }
    }
}

/// <summary>O modo de atendimento ou o responsável da sessão mudou (<c>HandlingModeChangedV1</c>).</summary>
internal sealed class HandlingModeChangedHandler : EventHandlerBase<HandlingModeChangedV1>
{
    private readonly InboxGate<ContactCenterDbContext> _inbox;
    private readonly ConversationProjectionService _projection;
    private readonly IDeferredAudit _audit;

    public HandlingModeChangedHandler(InboxGate<ContactCenterDbContext> inbox, ConversationProjectionService projection, IDeferredAudit audit)
    {
        _inbox = inbox;
        _projection = projection;
        _audit = audit;
    }

    public override string EventType => ConversationEventTypes.HandlingModeChanged;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, HandlingModeChangedV1 payload, CancellationToken cancellationToken) =>
        ProjectionHandling.RunAsync(_inbox, _audit, envelope, token => _projection.ApplyHandlingModeChangedAsync(payload, envelope.EventId, token), cancellationToken);
}

/// <summary>A sessão foi encerrada (<c>SessionClosedV1</c>): o item ativo dela é encerrado.</summary>
internal sealed class SessionClosedHandler : EventHandlerBase<SessionClosedV1>
{
    private readonly InboxGate<ContactCenterDbContext> _inbox;
    private readonly ConversationProjectionService _projection;
    private readonly IDeferredAudit _audit;

    public SessionClosedHandler(InboxGate<ContactCenterDbContext> inbox, ConversationProjectionService projection, IDeferredAudit audit)
    {
        _inbox = inbox;
        _projection = projection;
        _audit = audit;
    }

    public override string EventType => ConversationEventTypes.SessionClosed;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, SessionClosedV1 payload, CancellationToken cancellationToken) =>
        ProjectionHandling.RunAsync(_inbox, _audit, envelope, token => _projection.ApplySessionClosedAsync(payload, envelope.EventId, token), cancellationToken);
}

/// <summary>
/// A sessão foi encaminhada para outra unidade ou setor (<c>SessionTransferredV1</c>): o item que ficou para trás limita o histórico que expõe
/// ao que a conversa tinha naquele instante.
/// </summary>
internal sealed class SessionTransferredHandler : EventHandlerBase<SessionTransferredV1>
{
    private readonly InboxGate<ContactCenterDbContext> _inbox;
    private readonly ConversationProjectionService _projection;
    private readonly IDeferredAudit _audit;

    public SessionTransferredHandler(InboxGate<ContactCenterDbContext> inbox, ConversationProjectionService projection, IDeferredAudit audit)
    {
        _inbox = inbox;
        _projection = projection;
        _audit = audit;
    }

    public override string EventType => ConversationEventTypes.SessionTransferred;

    public override int EventVersion => 1;

    protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, SessionTransferredV1 payload, CancellationToken cancellationToken) =>
        ProjectionHandling.RunAsync(_inbox, _audit, envelope, token => _projection.ApplySessionTransferredAsync(payload, envelope.EventId, token), cancellationToken);
}

/// <summary>
/// A única leitura entre tenants do módulo (BOUNDARIES.md §2, TEN-2): só os identificadores de tenant que têm operação pendente há
/// tempo, ou item atrás do Conversations (projeção adiada), para o Worker abrir um contexto de sistema por tenant. Ignora o filtro de tenant de
/// propósito e não devolve dado do item.
/// </summary>
internal sealed class PendingWorkSource : IPendingWorkSource
{
    private readonly ContactCenterDbContext _db;

    public PendingWorkSource(ContactCenterDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(DateTimeOffset startedBefore, DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        var tenants = await _db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(i =>
                (i.PendingKindValue != null && i.PendingStartedAt < startedBefore && (i.PendingNextAttemptAt == null || i.PendingNextAttemptAt <= now))
                || (i.Status != QueueItemStatus.Closed && i.PendingKindValue == null && i.ResyncStateVersion != null)) // obrigação aberta (QueueItem.NeedsResync)
            .GroupBy(i => i.TenantId)
            .OrderBy(g => g.Min(i => i.PendingNextAttemptAt ?? i.PendingStartedAt ?? i.UpdatedAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithPendingAuditAsync(int take, CancellationToken cancellationToken)
    {
        var tenants = await _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(a => a.TenantId)
            .OrderBy(g => g.Min(a => a.CreatedAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }
}
