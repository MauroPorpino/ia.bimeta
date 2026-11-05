using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>
/// O armazenamento dos turnos do tenant do contexto. Toda leitura vem do banco AGORA (um turno já rastreado neste escopo é descartado e relido): o handler dos eventos e o executor disputam o mesmo
/// turno, e quem decide sobre um estado velho decide errado. A concorrência é otimista (<see cref="AiTurn.Version"/>) e o índice único parcial garante um só turno aberto por sessão; os dois
/// viram <see cref="AiTurnConflictException"/>, sem gravar nada.
/// </summary>
internal sealed class AiTurnStore : IAiTurnStore
{
    private readonly AgentsDbContext _db;
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;

    public AiTurnStore(AgentsDbContext db, AgentsOptions options, TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task<AiTurn?> FindAsync(Guid turnId, CancellationToken cancellationToken)
    {
        Detach(e => e.Id == turnId);
        return await _db.Turns.FirstOrDefaultAsync(t => t.Id == turnId, cancellationToken);
    }

    public async Task<AiTurn?> FindOpenBySessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        Detach(e => e.SessionId == sessionId);
        return await _db.Turns.FirstOrDefaultAsync(
            t => t.SessionId == sessionId && (t.State == AiTurnState.Scheduled || t.State == AiTurnState.Ready || t.State == AiTurnState.Running),
            cancellationToken);
    }

    public void Add(AiTurn turn) => _db.Turns.Add(turn);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new AiTurnConflictException();
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            throw new AiTurnConflictException();
        }
    }

    public async Task<IReadOnlyList<AiTurn>> ClaimDueAsync(string owner, int max, CancellationToken cancellationToken)
    {
        var tenant = _db.CurrentTenantId;
        var agora = _clock.GetUtcNow();

        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        // O teto de turnos simultâneos do tenant é contado no banco, entre todos os processos. A trava consultiva da transação serializa as reivindicações DO MESMO tenant (a contagem e a escolha
        // não se atropelam); tenants diferentes não se bloqueiam. Ela cai sozinha no fim da transação.
        var chaveDaTrava = $"agents-claim:{tenant}";
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chaveDaTrava}, 0))", cancellationToken);

        var emAndamento = await _db.Turns.CountAsync(t => t.State == AiTurnState.Running && t.LeaseExpiresAt > agora, cancellationToken);
        var vagas = Math.Min(max, _options.MaxConcurrentTurnsPerTenant - emAndamento);
        if (vagas <= 0)
        {
            await transacao.CommitAsync(cancellationToken);
            return Array.Empty<AiTurn>();
        }

        // Os turnos devidos, travados para este executor (os que outro executor já travou são pulados). O predicado de tenant é explícito: o SQL escrito à mão não passa pelo filtro global, e o
        // SaveChanges ainda confere o tenant de cada entidade gravada.
        var devidos = await _db.Turns
            .FromSqlInterpolated($@"
                SELECT * FROM agents.ai_turns
                WHERE tenant_id = {tenant}
                  AND ((state = 1 AND fire_at <= {agora})
                    OR (state = 2 AND COALESCE(next_attempt_at, fire_at) <= {agora})
                    OR (state = 3 AND lease_expires_at <= {agora}))
                ORDER BY COALESCE(next_attempt_at, fire_at)
                LIMIT {vagas}
                FOR UPDATE SKIP LOCKED")
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        foreach (var turno in devidos)
        {
            var lane = await _db.Database.SqlQuery<long>($"SELECT nextval('agents.lane_token_seq') AS \"Value\"").SingleAsync(cancellationToken);
            turno.Claim(lane, owner, agora, _options.TurnMaxDuration);
        }

        if (devidos.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        await transacao.CommitAsync(cancellationToken);
        return devidos;
    }

    private void Detach(Func<AiTurn, bool> predicado)
    {
        foreach (var entrada in _db.ChangeTracker.Entries<AiTurn>().Where(e => predicado(e.Entity)).ToList())
        {
            entrada.State = EntityState.Detached;
        }
    }
}

/// <summary>
/// A única leitura entre tenants do módulo (BOUNDARIES.md §2, TEN-2): só os identificadores de tenant, para o Worker abrir um contexto de sistema por tenant. Ignora o filtro de tenant de propósito e
/// não devolve dado de turno.
/// </summary>
internal sealed class PendingTurnSource : IPendingTurnSource
{
    private readonly AgentsDbContext _db;

    public PendingTurnSource(AgentsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithDueTurnsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        var tenants = await _db.Turns.IgnoreQueryFilters().AsNoTracking()
            .Where(t =>
                (t.State == AiTurnState.Scheduled && t.FireAt <= now)
                || (t.State == AiTurnState.Ready && (t.NextAttemptAt ?? t.FireAt) <= now)
                || (t.State == AiTurnState.Running && t.LeaseExpiresAt <= now))
            .GroupBy(t => t.TenantId)
            .OrderBy(g => g.Min(t => t.NextAttemptAt ?? t.FireAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithTurnsAsync(int take, CancellationToken cancellationToken)
    {
        var tenants = await _db.Turns.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(t => t.TenantId)
            .OrderByDescending(g => g.Max(t => t.CreatedAt))
            .Select(g => g.Key)
            .Take(take)
            .ToListAsync(cancellationToken);
        return tenants.Select(t => t.Value).ToList();
    }
}
