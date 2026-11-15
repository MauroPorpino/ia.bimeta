using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Workflows.Infrastructure;

public sealed record FlowDrainResult(int Instances = 0, int Decisions = 0, int Superseded = 0, int Unknown = 0, string? BlockedBy = null);

/// <summary>Drena um conjunto fixado no início da transação. Todas as correlações entre tabelas incluem o tenant.</summary>
public sealed class FlowDrain(WorkflowsDbContext db, IOutboxWriter outbox, TimeProvider clock)
{
    internal const string Reason = "operator-drain";

    public IQueryable<FlowInstance> Candidates(TenantId? tenant) =>
        db.Instances.IgnoreQueryFilters().Where(i => (tenant == null || i.TenantId == tenant) &&
            (i.Status < FlowStatus.Completed || i.NextRunAt != null ||
                (i.Status == FlowStatus.Failed && db.Decisions.IgnoreQueryFilters()
                    .Any(d => d.TenantId == i.TenantId && d.SessionId == i.SessionId && d.Owner == AutomationOwner.Flow))));

    public async Task<FlowDrainResult> ExecuteAsync(TenantId? tenant, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var selected = await Candidates(tenant).Select(i => new { i.TenantId, i.Id }).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var cancelled = 0;
        var decisions = 0;
        var superseded = 0;
        var unknown = 0;
        var staged = new List<EventEnvelope>();
        foreach (var group in selected.GroupBy(i => i.TenantId))
        {
            var scope = group.Key;
            var ids = group.Select(i => i.Id).ToArray();
            if (!await FlowExecutionLock.TryAcquireForDrainAsync(db, scope, ids, cancellationToken))
            {
                return new FlowDrainResult(BlockedBy: $"empresa {scope.Value:N} com execução ativa. Aguarde o runtime e repita");
            }

            // O runtime pode ter concluído uma instância entre a seleção inicial e a trava. Só reavaliamos as chaves já selecionadas.
            var targets = await Candidates(scope).Where(i => ids.Contains(i.Id))
                .Select(i => new { i.Id, i.SessionId, i.Status, i.OnFailure }).ToListAsync(cancellationToken);
            if (targets.Count == 0)
            {
                continue;
            }

            var instances = targets.Select(i => i.Id).ToArray();
            var sessions = targets.Select(i => i.SessionId).Distinct().ToArray();
            var effects = db.Effects.IgnoreQueryFilters().Where(e => e.TenantId == scope && instances.Contains(e.InstanceId));
            if (await effects.AnyAsync(e => e.State == EffectState.InFlight, cancellationToken))
            {
                return new FlowDrainResult(BlockedBy: $"empresa {scope.Value:N} com efeito iniciado sem resultado confirmado. Deixe o runtime recuperar o efeito e repita");
            }

            // A política de falha pode estar transferindo ao atendente sem um registro de efeito separado.
            var failedHandoffs = targets.Where(i => i.Status == FlowStatus.Failed && i.OnFailure == OnFailurePolicy.Handoff)
                .Select(i => i.SessionId).ToArray();
            var flowDecisions = db.Decisions.IgnoreQueryFilters().Where(d => d.TenantId == scope && d.Owner == AutomationOwner.Flow);
            if (failedHandoffs.Length != 0 && await flowDecisions.AnyAsync(d => failedHandoffs.Contains(d.SessionId), cancellationToken))
            {
                return new FlowDrainResult(BlockedBy: $"empresa {scope.Value:N} com transferência por falha sem finalização. Reconcilie o atendimento antes de repetir");
            }

            var affected = await db.Instances.IgnoreQueryFilters().Where(i => i.TenantId == scope && instances.Contains(i.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, FlowStatus.Cancelled).SetProperty(i => i.FailureCode, Reason)
                    .SetProperty(i => i.NextRunAt, (DateTimeOffset?)null).SetProperty(i => i.WaitUntil, (DateTimeOffset?)null)
                    .SetProperty(i => i.PendingEffectKey, (string?)null).SetProperty(i => i.LeaseOwner, (string?)null)
                    .SetProperty(i => i.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(i => i.VariablesCipher, string.Empty)
                    .SetProperty(i => i.VariablesKeyId, string.Empty).SetProperty(i => i.EndedAt, i => i.EndedAt ?? now)
                    .SetProperty(i => i.UpdatedAt, now).SetProperty(i => i.Revision, i => i.Revision + 1), cancellationToken);
            if (affected != instances.Length)
            {
                return new FlowDrainResult(BlockedBy: "instância removida durante a drenagem. Confira o status e repita");
            }

            cancelled += affected;
            unknown += await effects.CountAsync(e => e.State == EffectState.Unknown, cancellationToken);
            superseded += await effects.Where(e => e.State == EffectState.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EffectState.Superseded)
                    .SetProperty(e => e.ResultCode, Reason).SetProperty(e => e.CompletedAt, now), cancellationToken);
            await effects.Where(e => e.PayloadCipher != string.Empty || e.PayloadKeyId != string.Empty)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.PayloadCipher, string.Empty).SetProperty(e => e.PayloadKeyId, string.Empty), cancellationToken);
            decisions += await flowDecisions.Where(d => sessions.Contains(d.SessionId))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Owner, AutomationOwner.Agent)
                    .SetProperty(d => d.UpdatedAt, now).SetProperty(d => d.Revision, d => d.Revision + 1), cancellationToken);

            staged.AddRange(sessions.Select(id => new EventEnvelope(Guid.NewGuid(), FlowDrainedToAgentV1.EventType, 1,
                scope, id, null, id.ToString("N"), now, new FlowDrainedToAgentV1(id))));
        }

        foreach (var group in staged.GroupBy(e => e.TenantId))
        {
            outbox.Stage(db, group.ToArray(), group.Key, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FlowDrainResult(cancelled, decisions, superseded, unknown);
    }
}
