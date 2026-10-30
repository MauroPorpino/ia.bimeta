using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// A auditoria guardada na mesma transação do que o módulo grava (<c>contact_center.audit_outbox</c>) e entregue à trilha depois da
/// confirmação. Cada entrega tem a sua transação: a linha é trancada (<c>FOR UPDATE SKIP LOCKED</c>, então duas entregas simultâneas
/// nunca pegam a mesma), entregue à trilha e apagada; se o processo cair no meio, a linha volta e é entregue de novo. A entrega é pelo
/// menos uma vez, e a trilha a torna idempotente: o identificador da linha é a chave de idempotência do evento, então a repetição não
/// duplica. A autoria e o instante ORIGINAIS (ator, origem e quando) são guardados com a linha e gravados na entrega
/// (<see cref="IAuditRecoveryWriter"/>), de modo que o evento entregue pelo job de reconciliação diz o que o autor fez, e não o que o job é.
/// </summary>
internal sealed class DeferredAudit : IDeferredAudit
{
    private const int FlushBatch = 50;

    private readonly ContactCenterDbContext _db;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditRecoveryWriter _audit;
    private readonly TimeProvider _clock;

    public DeferredAudit(ContactCenterDbContext db, IRequestContextAccessor context, IAuditRecoveryWriter audit, TimeProvider clock)
    {
        _db = db;
        _context = context;
        _audit = audit;
        _clock = clock;
    }

    public void Defer(AuditEntry entry)
    {
        var contexto = _context.GetRequired();
        _db.AuditOutbox.Add(PendingAudit.Create(contexto.TenantId, entry, contexto.Principal?.UserId, contexto.Origin, _clock.GetUtcNow()));
    }

    public void Defer(AuditEntry entry, AuditProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        // O tenant é sempre o do contexto confiável; só o ator, a origem e o instante vêm da operação original.
        var contexto = _context.GetRequired();
        _db.AuditOutbox.Add(PendingAudit.Create(contexto.TenantId, entry, provenance.ActorUserId, provenance.ActorOrigin, provenance.OccurredAt));
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Defer(entry);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await FlushAsync(FlushBatch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A entrada já está guardada: a reconciliação a entrega, e a recusa que motivou isto continua sendo a resposta.
        }
    }

    public Task<int> PendingCountAsync(CancellationToken cancellationToken) =>
        _db.AuditOutbox.AsNoTracking().CountAsync(cancellationToken);

    public async Task<int> FlushAsync(int take, CancellationToken cancellationToken)
    {
        // De dentro de uma transação aberta a transação ainda pode ser desfeita: entregar agora seria dizer à trilha o que pode não acontecer.
        if (_db.Database.CurrentTransaction is not null)
        {
            return 0;
        }

        var tenant = _context.GetRequired().TenantId.Value;
        var entregues = 0;
        while (entregues < take)
        {
            await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

            var id = await _db.Database
                .SqlQuery<Guid>(
                    $"SELECT id AS \"Value\" FROM contact_center.audit_outbox WHERE tenant_id = {tenant} ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
            if (id.Count == 0)
            {
                return entregues;
            }

            var linha = await _db.AuditOutbox.AsNoTracking().FirstAsync(a => a.Id == id[0], cancellationToken);
            try
            {
                await _audit.WriteRecoveredAsync(linha.ToEntry(), linha.ToProvenance(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A trilha não aceitou agora: a linha fica (a transação é desfeita) e a próxima entrega tenta de novo.
                return entregues;
            }

            await _db.AuditOutbox.Where(a => a.Id == linha.Id).ExecuteDeleteAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            entregues++;
        }

        return entregues;
    }
}
