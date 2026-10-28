using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// Toda consulta passa pelo filtro global do <see cref="ContactCenterDbContext"/>: só enxerga o tenant do contexto e, sem
/// contexto, falha fechada (TEN-2 e TEN-3).
/// </summary>
internal sealed class QueueRepository : IQueueRepository
{
    private readonly ContactCenterDbContext _db;

    public QueueRepository(ContactCenterDbContext db)
    {
        _db = db;
    }

    public async Task AddQueueAsync(ServiceQueue queue, CancellationToken cancellationToken)
    {
        _db.Queues.Add(queue);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, ServiceQueueConfiguration.UniqueNameIndex))
        {
            _db.Entry(queue).State = EntityState.Detached;
            throw new QueueNameInUseException();
        }
    }

    public Task<ServiceQueue?> FindQueueAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Queues.FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

    public Task<ServiceQueue?> FindQueueReadOnlyAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Queues.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ServiceQueue>> ListQueuesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken)
    {
        var consulta = _db.Queues.AsNoTracking();
        if (organizationIds is not null)
        {
            consulta = consulta.Where(q => organizationIds.Contains(q.OrganizationId));
        }

        return await consulta.OrderBy(q => q.Name).ThenBy(q => q.Id).ToListAsync(cancellationToken);
    }

    public Task<QueueMembership?> FindMembershipAsync(Guid queueId, Guid userId, CancellationToken cancellationToken) =>
        _db.Memberships.FirstOrDefaultAsync(m => m.QueueId == queueId && m.UserId == userId, cancellationToken);

    public async Task AddMembershipAsync(QueueMembership membership, CancellationToken cancellationToken)
    {
        _db.Memberships.Add(membership);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Outra requisição colocou o mesmo atendente na fila primeiro: quem repetir muda o papel do que já existe.
            _db.Entry(membership).State = EntityState.Detached;
            throw new ContactCenterConflictException();
        }
    }

    public async Task RemoveMembershipAsync(QueueMembership membership, CancellationToken cancellationToken)
    {
        _db.Memberships.Remove(membership);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueueMembership>> ListMembershipsAsync(Guid queueId, CancellationToken cancellationToken) =>
        await _db.Memberships.AsNoTracking().Where(m => m.QueueId == queueId).OrderBy(m => m.AddedAt).ThenBy(m => m.UserId).ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Descarta o que foi rastreado: o chamador relê o recurso antes de tentar de novo.
            _db.ChangeTracker.Clear();
            throw new ContactCenterConflictException();
        }
    }
}
