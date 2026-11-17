using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// A entrega da auditoria do Embedded guardada na transação do consumo do ticket (<c>identity.audit_outbox</c>), no mesmo padrão do <c>DeferredAudit</c> do ContactCenter (ADR 0010, decisão C). Cada entrega tem a sua
/// transação: a linha é travada (<c>FOR UPDATE SKIP LOCKED</c>, então dois Workers nunca pegam a mesma), entregue à trilha e SÓ DEPOIS apagada; se a trilha recusa, a transação é desfeita e a linha fica para o próximo ciclo; se
/// o processo cai no meio, a linha volta. A entrega é pelo menos uma vez, e a trilha a torna idempotente: o identificador da linha é a chave de idempotência do evento. Ator, origem e instante ORIGINAIS são gravados na
/// entrega (<see cref="IAuditRecoveryWriter"/>). O tenant é o do contexto confiável criado pelo Worker para o tenant da linha: nunca vem de um parâmetro.
/// </summary>
internal sealed class IdentityAuditOutbox : IIdentityAuditOutbox
{
    private readonly IdentityDbContext _db;
    private readonly IRequestContextAccessor _context;
    private readonly IAuditRecoveryWriter _audit;

    public IdentityAuditOutbox(IdentityDbContext db, IRequestContextAccessor context, IAuditRecoveryWriter audit)
    {
        _db = db;
        _context = context;
        _audit = audit;
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(int take, CancellationToken cancellationToken)
    {
        var tenants = await _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(a => a.TenantId)
            .OrderBy(g => g.Min(a => a.CreatedAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }

    public Task<int> PendingCountAsync(CancellationToken cancellationToken) =>
        _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking().CountAsync(cancellationToken);

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
                    $"SELECT id AS \"Value\" FROM identity.audit_outbox WHERE tenant_id = {tenant} ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
            if (id.Count == 0)
            {
                return entregues;
            }

            var linha = await _db.AuditOutbox.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.TenantId == _context.GetRequired().TenantId && a.Id == id[0], cancellationToken);
            try
            {
                await _audit.WriteRecoveredAsync(linha.ToEntry(), linha.ToProvenance(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A trilha não aceitou agora: a linha fica (a transação é desfeita) e a próxima entrega tenta de novo.
                return entregues;
            }

            await _db.AuditOutbox.IgnoreQueryFilters().Where(a => a.TenantId == linha.TenantId && a.Id == linha.Id).ExecuteDeleteAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            entregues++;
        }

        return entregues;
    }
}
