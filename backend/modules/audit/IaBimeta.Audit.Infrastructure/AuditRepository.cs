using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Application;
using IaBimeta.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IaBimeta.Audit.Infrastructure;

/// <summary>
/// Só inclui e consulta. Toda consulta passa pelo filtro global do <see cref="AuditDbContext"/>: só enxerga o tenant
/// do contexto e, sem contexto, falha fechada.
/// </summary>
internal sealed class AuditRepository : IAuditRepository
{
    private readonly AuditDbContext _db;

    public AuditRepository(AuditDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        _db.Events.Add(auditEvent);
        await SaveOnceAsync(auditEvent, cancellationToken);
    }

    public async Task AddForAuthenticationAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(auditEvent.TenantId);
        _db.Events.Add(auditEvent);
        await SaveOnceAsync(auditEvent, cancellationToken);
    }

    // O mesmo evento já gravado (a mesma chave de idempotência, entregue de novo por um outbox pelo menos uma vez) não é duplicado nem erro:
    // a chave primária (tenant, id) impede o segundo, e a repetição é aceita. Sem chave o identificador é novo a cada vez, e isso não acontece.
    private async Task SaveOnceAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.Entry(auditEvent).State = EntityState.Detached;
        }
        catch
        {
            // E1-R, sexta rodada: uma falha real de persistência não pode deixar o evento rastreado como Added no contexto reutilizado pelo lote de entrega: o SaveChanges da entrada SEGUINTE tentaria gravá-lo de novo
            // (com o ator e o recurso da entrada errada). A falha continua subindo ao chamador, que a registra e adia a linha.
            _db.Entry(auditEvent).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var eventos = _db.Events.AsNoTracking();

        if (!string.IsNullOrEmpty(query.Operation))
        {
            eventos = eventos.Where(e => e.Operation == query.Operation);
        }

        if (!string.IsNullOrEmpty(query.ResourceId))
        {
            eventos = eventos.Where(e => e.ResourceId == query.ResourceId);
        }

        return await eventos
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(query.Take)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<AuditEvent> Items, int Total)> ListPageAsync(AuditPageQuery query, CancellationToken cancellationToken)
    {
        var eventos = _db.Events.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.OperationContains))
        {
            var operacao = query.OperationContains.Trim().ToLowerInvariant();
            eventos = eventos.Where(e => e.Operation.Contains(operacao));
        }

        if (!string.IsNullOrWhiteSpace(query.ResourceContains))
        {
            var recurso = query.ResourceContains.Trim().ToLowerInvariant();
            eventos = eventos.Where(e => e.ResourceId != null && e.ResourceId.ToLower().Contains(recurso));
        }

        if (Enum.TryParse<AuditOutcome>(query.Outcome, ignoreCase: true, out var desfecho))
        {
            eventos = eventos.Where(e => e.Outcome == desfecho);
        }

        if (query.From is { } de)
        {
            eventos = eventos.Where(e => e.OccurredAt >= de);
        }

        if (query.To is { } ate)
        {
            eventos = eventos.Where(e => e.OccurredAt < ate);
        }

        var total = await eventos.CountAsync(cancellationToken);
        var itens = await eventos.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip(query.Skip).Take(query.Take).ToListAsync(cancellationToken);
        return (itens, total);
    }
}
