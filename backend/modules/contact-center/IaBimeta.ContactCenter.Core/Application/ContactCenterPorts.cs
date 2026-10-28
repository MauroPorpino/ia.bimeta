using IaBimeta.ContactCenter.Domain;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// Persistência do módulo. Toda consulta só enxerga o tenant do contexto (TEN-2 e TEN-3) e falha fechada sem contexto. As
/// entidades devolvidas por <c>Find</c> sem o sufixo <c>ReadOnly</c> são rastreadas: o caso de uso as altera e chama
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IQueueRepository
{
    /// <exception cref="QueueNameInUseException">Já existe uma fila com este nome na organização.</exception>
    Task AddQueueAsync(ServiceQueue queue, CancellationToken cancellationToken);

    /// <summary>A fila rastreada para alteração.</summary>
    Task<ServiceQueue?> FindQueueAsync(Guid id, CancellationToken cancellationToken);

    Task<ServiceQueue?> FindQueueReadOnlyAsync(Guid id, CancellationToken cancellationToken);

    /// <param name="organizationIds">As organizações visíveis, ou <c>null</c> para todas as do tenant.</param>
    Task<IReadOnlyList<ServiceQueue>> ListQueuesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken);

    /// <summary>O atendente da fila rastreado para alteração.</summary>
    Task<QueueMembership?> FindMembershipAsync(Guid queueId, Guid userId, CancellationToken cancellationToken);

    /// <exception cref="ContactCenterConflictException">Outra operação colocou o mesmo atendente na fila primeiro.</exception>
    Task AddMembershipAsync(QueueMembership membership, CancellationToken cancellationToken);

    Task RemoveMembershipAsync(QueueMembership membership, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueueMembership>> ListMembershipsAsync(Guid queueId, CancellationToken cancellationToken);

    /// <summary>Grava as alterações das entidades rastreadas.</summary>
    /// <exception cref="ContactCenterConflictException">Outra operação alterou o mesmo recurso antes.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
