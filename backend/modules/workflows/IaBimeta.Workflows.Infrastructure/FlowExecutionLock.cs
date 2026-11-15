using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Workflows.Infrastructure;

/// <summary>O runtime e a drenagem compartilham a mesma trava por empresa e instância. O runtime mantém a conexão aberta enquanto realiza efeitos.</summary>
internal sealed class FlowExecutionLock(WorkflowsDbContext db, string key) : IAsyncDisposable
{
    private static string Prefix(TenantId tenant) => $"workflows.execution:{tenant.Value:N}:";

    public static async Task<IAsyncDisposable?> TryAcquireAsync(WorkflowsDbContext db, Guid instanceId, CancellationToken cancellationToken)
    {
        var key = Prefix(db.TenantAtual) + instanceId.ToString("N");
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtextextended({key}, 0)) AS \"Value\"").SingleAsync(cancellationToken))
            {
                return new FlowExecutionLock(db, key);
            }
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }

        await db.Database.CloseConnectionAsync();
        return null;
    }

    public static async Task<bool> TryAcquireForDrainAsync(WorkflowsDbContext db, TenantId tenant, Guid[] ids, CancellationToken cancellationToken)
    {
        var prefix = Prefix(tenant);
        var acquired = await db.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock(hashtextextended({prefix} || replace(id::text, '-', ''), 0)) AS \"Value\" FROM unnest({ids}) AS instances(id)")
            .ToListAsync(cancellationToken);
        return acquired.All(locked => locked);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtextextended({key}, 0))");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
