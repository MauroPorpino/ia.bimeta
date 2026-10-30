using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure.Configurations;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>
/// Itens da fila e o histórico deles. Toda consulta passa pelo filtro global do <see cref="ContactCenterDbContext"/>: só enxerga
/// o tenant do contexto e, sem contexto, falha fechada (TEN-2 e TEN-3). O item e o registro do histórico de uma operação são
/// gravados na MESMA transação do <c>SaveChanges</c>: ou os dois existem, ou nenhum.
/// </summary>
internal sealed class QueueItemRepository : IQueueItemRepository
{
    private readonly ContactCenterDbContext _db;

    public QueueItemRepository(ContactCenterDbContext db)
    {
        _db = db;
    }

    public Task<QueueItem?> FindItemAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Items.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<QueueItem?> FindItemReadOnlyAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<QueueItem?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Items.FirstOrDefaultAsync(i => i.SessionId == sessionId && i.Status != QueueItemStatus.Closed, cancellationToken);

    public async Task<QueueItem> ReloadItemAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var entrada = _db.Entry(item);
        if (entrada.State == EntityState.Detached)
        {
            // O rastreamento foi descartado (um conflito anterior limpou o contexto): lê de novo, sem objeto rastreado para defasar.
            return await _db.Items.FirstOrDefaultAsync(i => i.Id == item.Id, cancellationToken)
                ?? throw new ContactCenterResourceNotFoundException("Conversa na fila");
        }

        await entrada.ReloadAsync(cancellationToken);
        return entrada.State == EntityState.Detached ? throw new ContactCenterResourceNotFoundException("Conversa na fila") : item;
    }

    public async Task<IAsyncDisposable?> TryLockItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // A trava consultiva vale para a CONEXÃO: a conexão fica aberta (e o EF a reutiliza nos SaveChanges) até a trava ser solta.
        var chave = $"cc-item:{itemId:N}";
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            for (var tentativa = 0; tentativa < LockAttempts; tentativa++)
            {
                var obtida = await _db.Database
                    .SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtextextended({chave}, 0)) AS \"Value\"")
                    .SingleAsync(cancellationToken);
                if (obtida)
                {
                    return new ItemLock(_db, chave);
                }

                await Task.Delay(LockPollInterval, cancellationToken);
            }
        }
        catch
        {
            await _db.Database.CloseConnectionAsync();
            throw;
        }

        await _db.Database.CloseConnectionAsync();
        return null;
    }

    public async Task<long?> AcquireFenceTokenAsync(Guid itemId, Guid operationId, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantId;

        // Um passo só, sob a trava da linha do item: o UPDATE confere que a operação AINDA é a pendente (a cláusula é reavaliada depois de
        // esperar quem a alterava antes) e, no mesmo comando, tira o próximo valor da sequência e avança a versão. Assim o token nunca é
        // dado a quem perdeu a intenção, e a ordem dos tokens é a ordem em que a linha foi tomada. Dentro de uma transação já aberta (o
        // consumo dos eventos) o passo faz parte dela; fora, tem a sua, para ler de volta o token ainda sob a mesma trava.
        var propria = _db.Database.CurrentTransaction is null;
        await using var transacao = propria ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;

        var afetadas = await _db.Database.ExecuteSqlAsync(
            $"UPDATE contact_center.queue_items SET executor_fence_token = nextval('contact_center.executor_fence_seq'), version = version + 1 WHERE tenant_id = {tenant} AND id = {itemId} AND pending_operation_id = {operationId}",
            cancellationToken);
        if (afetadas == 0)
        {
            if (transacao is not null)
            {
                await transacao.RollbackAsync(cancellationToken);
            }

            return null;
        }

        var token = await _db.Database
            .SqlQuery<long>($"SELECT executor_fence_token AS \"Value\" FROM contact_center.queue_items WHERE tenant_id = {tenant} AND id = {itemId}")
            .SingleAsync(cancellationToken);
        if (transacao is not null)
        {
            await transacao.CommitAsync(cancellationToken);
        }

        return token;
    }

    // Até cerca de 2,5 s esperando outra tentativa que está concluindo a mesma operação.
    private const int LockAttempts = 50;
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(50);

    private sealed class ItemLock : IAsyncDisposable
    {
        private readonly ContactCenterDbContext _db;
        private readonly string _key;

        public ItemLock(ContactCenterDbContext db, string key)
        {
            _db = db;
            _key = key;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtextextended({_key}, 0))");
            }
            catch (Exception)
            {
                // Conexão perdida: o banco solta a trava ao fechar a sessão, e o pool reinicia a sessão ao devolver a conexão.
            }
            finally
            {
                await _db.Database.CloseConnectionAsync();
            }
        }
    }

    public Task<QueueOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        _db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);

    public Task<QueueOperation?> FindOperationReadOnlyAsync(Guid operationId, CancellationToken cancellationToken) =>
        _db.Operations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);

    public void AddOperation(QueueOperation operation) => _db.Operations.Add(operation);

    public void RemoveOperation(QueueOperation operation) => _db.Operations.Remove(operation);

    public async Task<IReadOnlyList<QueueItem>> ListWaitingAsync(Guid queueId, int take, CancellationToken cancellationToken) =>
        await _db.Items.AsNoTracking()
            .Where(i => i.QueueId == queueId && i.Status == QueueItemStatus.Waiting && i.PendingKindValue == null)
            .OrderByDescending(i => i.Priority).ThenBy(i => i.EnteredAt).ThenBy(i => i.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<int> CountOccupiedAsync(Guid queueId, Guid userId, CancellationToken cancellationToken) =>
        _db.Items.AsNoTracking().CountAsync(
            i => (i.Status == QueueItemStatus.Assigned && i.QueueId == queueId && i.AssignedUserId == userId)
                || ((i.PendingKindValue == PendingKind.Assign || i.PendingKindValue == PendingKind.TransferQueue) && i.PendingQueueId == queueId && i.PendingUserId == userId),
            cancellationToken);

    public async Task<bool> SaveWithinCapacityAsync(Guid queueId, Guid userId, int maximum, Guid operationId, CancellationToken cancellationToken)
    {
        // A trava vale até o fim da transação: a contagem e a gravação da intenção formam um passo só para o par (fila, atendente).
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        var chave = $"cc-capacity:{queueId:N}:{userId:N}";
        await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", cancellationToken);

        // O mesmo pedido, repetido ao mesmo tempo, já gravou o recibo (e a vaga que ocupa conta na contagem abaixo): não é "sem vaga", é
        // repetição. A conferência acontece sob a trava, depois de o primeiro pedido ter confirmado, então enxerga o recibo dele.
        if (await _db.Operations.AsNoTracking().AnyAsync(o => o.Id == operationId, cancellationToken))
        {
            await transacao.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            throw new OperationIdInUseException();
        }

        if (await CountOccupiedAsync(queueId, userId, cancellationToken) >= maximum)
        {
            await transacao.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return false;
        }

        await SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        return true;
    }

    public Task<int> CountWaitingAsync(Guid queueId, CancellationToken cancellationToken) =>
        _db.Items.AsNoTracking().CountAsync(i => i.QueueId == queueId && i.Status == QueueItemStatus.Waiting, cancellationToken);

    public Task<bool> OperationAppliedAsync(Guid itemId, Guid operationId, CancellationToken cancellationToken) =>
        _db.History.AsNoTracking().AnyAsync(h => h.ItemId == itemId && h.OperationId == operationId, cancellationToken);

    public async Task AddItemAsync(QueueItem item, QueueOperation operation, CancellationToken cancellationToken)
    {
        _db.Items.Add(item);
        _db.Operations.Add(operation);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, QueueItemConfiguration.ActiveSessionIndex))
        {
            _db.Entry(operation).State = EntityState.Detached;
            _db.Entry(item).State = EntityState.Detached;
            throw new QueueItemConflictException(QueueItemConflict.ActiveItemExists, "A conversa já está em atendimento na fila.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, QueueOperationConfiguration.PrimaryKey))
        {
            _db.Entry(operation).State = EntityState.Detached;
            _db.Entry(item).State = EntityState.Detached;
            throw new OperationIdInUseException();
        }
    }

    public void AddHistory(QueueItemHistory history) => _db.History.Add(history);

    public async Task<IReadOnlyList<QueueItem>> ListItemsAsync(QueueItemScope scope, QueueItemFilter filter, CancellationToken cancellationToken)
    {
        var consulta = _db.Items.AsNoTracking();

        // Sem restrição por organização (sistema ou concessão de tenant): vale só o filtro do tenant. Com restrição, o item
        // aparece se a organização dele é lida pelo usuário ou se a fila dele é uma de que o usuário participa.
        if (scope.ReadableOrganizationIds is { } organizacoes)
        {
            var filas = scope.MemberQueueIds;
            consulta = consulta.Where(i => organizacoes.Contains(i.OrganizationId) || filas.Contains(i.QueueId));
        }

        if (filter.QueueId is { } fila)
        {
            consulta = consulta.Where(i => i.QueueId == fila);
        }

        consulta = filter.Status is { } situacao
            ? consulta.Where(i => i.Status == situacao)
            : consulta.Where(i => i.Status != QueueItemStatus.Closed);

        if (filter.AssignedToMe)
        {
            if (scope.UserId is not { } eu)
            {
                return Array.Empty<QueueItem>();
            }

            consulta = consulta.Where(i => i.AssignedUserId == eu);
        }

        // Do mais urgente para o menos e, na mesma prioridade, do mais antigo (a ordem em que a fila é atendida).
        return await consulta.OrderByDescending(i => i.Priority).ThenBy(i => i.EnteredAt).ThenBy(i => i.Id).Take(filter.Take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QueueItemHistory>> ListHistoryAsync(Guid itemId, CancellationToken cancellationToken) =>
        await _db.History.AsNoTracking().Where(h => h.ItemId == itemId).OrderBy(h => h.OccurredAt).ThenBy(h => h.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<QueueItem>> ListPendingAsync(DateTimeOffset startedBefore, DateTimeOffset now, int take, CancellationToken cancellationToken) =>
        await _db.Items.AsNoTracking()
            .Where(i => i.PendingKindValue != null && i.PendingStartedAt < startedBefore && (i.PendingNextAttemptAt == null || i.PendingNextAttemptAt <= now))
            .OrderBy(i => i.PendingNextAttemptAt ?? i.PendingStartedAt).ThenBy(i => i.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<QueueItem>> ListStaleProjectionsAsync(int take, CancellationToken cancellationToken) =>
        await _db.Items.AsNoTracking()
            // A obrigação de ressincronização está ABERTA enquanto a marca existe (QueueItem.NeedsResync): a versão observada não a liquida.
            .Where(i => i.Status != QueueItemStatus.Closed && i.PendingKindValue == null && i.ResyncStateVersion != null)
            .OrderBy(i => i.UpdatedAt).ThenBy(i => i.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Outra operação mudou o item antes. Descarta o que foi rastreado: o chamador relê antes de decidir.
            _db.ChangeTracker.Clear();
            throw new ContactCenterConflictException();
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, QueueItemHistoryConfiguration.OperationIndex))
        {
            // Outra tentativa já concluiu esta mesma operação (o histórico tem um registro por operação).
            _db.ChangeTracker.Clear();
            throw new ContactCenterConflictException();
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, QueueOperationConfiguration.PrimaryKey))
        {
            // Outro pedido gravou o recibo deste identificador primeiro: nada do que esta tentativa tinha a gravar vale.
            _db.ChangeTracker.Clear();
            throw new OperationIdInUseException();
        }
    }
}
