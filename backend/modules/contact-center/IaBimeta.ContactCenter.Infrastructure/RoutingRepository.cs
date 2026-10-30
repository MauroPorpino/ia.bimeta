using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// Regras de roteamento, horário de atendimento e presença dos atendentes. Toda consulta passa pelo filtro global do
/// <see cref="ContactCenterDbContext"/>: só enxerga o tenant do contexto e, sem contexto, falha fechada (TEN-2 e TEN-3).
/// </summary>
internal sealed class RoutingRepository : IRoutingRepository
{
    private readonly ContactCenterDbContext _db;

    public RoutingRepository(ContactCenterDbContext db)
    {
        _db = db;
    }

    public async Task AddRuleAsync(RoutingRule rule, CancellationToken cancellationToken)
    {
        _db.RoutingRules.Add(rule);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<RoutingRule?> FindRuleAsync(Guid id, CancellationToken cancellationToken) =>
        _db.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<RoutingRule?> FindRuleReadOnlyAsync(Guid id, CancellationToken cancellationToken) =>
        _db.RoutingRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void RemoveRule(RoutingRule rule) => _db.RoutingRules.Remove(rule);

    public async Task<IReadOnlyList<RoutingRule>> ListRulesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken)
    {
        var consulta = _db.RoutingRules.AsNoTracking();
        if (organizationIds is not null)
        {
            consulta = consulta.Where(r => organizationIds.Contains(r.OrganizationId));
        }

        return await consulta.OrderBy(r => r.OrganizationId).ThenBy(r => r.Order).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoutingRule>> ListEnabledRulesAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await _db.RoutingRules.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Enabled)
            .OrderBy(r => r.Order).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

    public async Task<QueuePolicySnapshot?> GetPolicySnapshotAsync(Guid queueId, CancellationToken cancellationToken)
    {
        // As três leituras vêm do mesmo instantâneo do banco (leitura repetível): a troca de horário grava a fila, as faixas e os dias
        // fechados em uma transação, e o instantâneo vê essa transação inteira ou nada dela. Se já há uma transação em curso, vale a dela.
        var propria = _db.Database.CurrentTransaction is null;
        await using var transacao = propria ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken) : null;

        var fila = await _db.Queues.AsNoTracking().FirstOrDefaultAsync(q => q.Id == queueId, cancellationToken);
        if (fila is null)
        {
            return null;
        }

        var faixas = await _db.ScheduleWindows.AsNoTracking().Where(w => w.QueueId == queueId).ToListAsync(cancellationToken);
        var fechados = await _db.ScheduleClosures.AsNoTracking().Where(c => c.QueueId == queueId).ToListAsync(cancellationToken);
        if (transacao is not null)
        {
            await transacao.CommitAsync(cancellationToken);
        }

        return new QueuePolicySnapshot(fila, faixas, fechados);
    }

    public async Task ReplaceScheduleAsync(
        ServiceQueue queue, IReadOnlyList<QueueScheduleWindow> windows, IReadOnlyList<QueueClosure> closures, CancellationToken cancellationToken)
    {
        // Tudo na mesma transação: ou o horário novo inteiro vale (e a versão da fila avança), ou o antigo continua.
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.ScheduleWindows.Where(w => w.QueueId == queue.Id).ExecuteDeleteAsync(cancellationToken);
        await _db.ScheduleClosures.Where(c => c.QueueId == queue.Id).ExecuteDeleteAsync(cancellationToken);
        _db.ScheduleWindows.AddRange(windows);
        _db.ScheduleClosures.AddRange(closures);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new ContactCenterConflictException();
        }

        await transacao.CommitAsync(cancellationToken);
    }

    public Task<AgentPresence?> FindPresenceAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Presences.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

    public Task<AgentPresence?> FindPresenceReadOnlyAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Presences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

    public async Task AddPresenceAsync(AgentPresence presence, CancellationToken cancellationToken)
    {
        _db.Presences.Add(presence);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.Entry(presence).State = EntityState.Detached;
            throw new ContactCenterConflictException();
        }
    }

    public async Task<int> CountAvailableAgentsAsync(Guid queueId, int? maxItemsPerAgent, CancellationToken cancellationToken)
    {
        var disponiveis = await (from m in _db.Memberships.AsNoTracking()
                                 where m.QueueId == queueId
                                 join p in _db.Presences.AsNoTracking() on m.UserId equals p.UserId
                                 where p.Status == AgentStatus.Available
                                 select m.UserId).ToListAsync(cancellationToken);
        if (maxItemsPerAgent is not { } maximo || disponiveis.Count == 0)
        {
            return disponiveis.Count;
        }

        // Quem já está no limite de conversas desta fila não conta como disponível. Ocupam o atendente as conversas atribuídas a ele e as
        // atribuições ou transferências para ele ainda pendentes (a mesma conta do limite no momento de assumir).
        var atribuidas = await _db.Items.AsNoTracking()
            .Where(i => i.QueueId == queueId && i.Status == QueueItemStatus.Assigned && i.AssignedUserId != null && disponiveis.Contains(i.AssignedUserId.Value))
            .GroupBy(i => i.AssignedUserId!.Value)
            .Select(g => new { Usuario = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);
        var pendentes = await _db.Items.AsNoTracking()
            .Where(i => i.PendingQueueId == queueId && (i.PendingKindValue == PendingKind.Assign || i.PendingKindValue == PendingKind.TransferQueue)
                && i.PendingUserId != null && disponiveis.Contains(i.PendingUserId.Value))
            .GroupBy(i => i.PendingUserId!.Value)
            .Select(g => new { Usuario = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);
        var ocupacao = atribuidas.Concat(pendentes).GroupBy(o => o.Usuario).Select(g => g.Sum(o => o.Total));
        return disponiveis.Count - ocupacao.Count(total => total >= maximo);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new ContactCenterConflictException();
        }
    }
}
