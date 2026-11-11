using System.Data;
using System.Data.Common;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>A configuracao semantica do tenant do contexto (filtro global por tenant, ADR 0034).</summary>
internal sealed class SemanticAnswerSettingsStore : ISemanticAnswerSettingsStore
{
    private readonly AgentsDbContext _db;

    public SemanticAnswerSettingsStore(AgentsDbContext db)
    {
        _db = db;
    }

    public Task<SemanticAnswerSettings?> FindAsync(CancellationToken cancellationToken) =>
        _db.SemanticSettings.FirstOrDefaultAsync(cancellationToken);

    public void Add(SemanticAnswerSettings settings) => _db.SemanticSettings.Add(settings);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            throw new AgentDefinitionConflictException();
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Dois administradores criaram a primeira configuracao ao mesmo tempo: o segundo relê.
            _db.ChangeTracker.Clear();
            throw new AgentDefinitionConflictException();
        }
    }
}

/// <summary>As metricas do tenant do contexto (filtro global por tenant): contagens por desfecho, turnos terminados por motivo e as latencias, sem nenhum texto.</summary>
internal sealed class SemanticMetricsStore : ISemanticMetricsStore
{
    private const int MaxLatencyRows = 50_000;

    private readonly AgentsDbContext _db;

    public SemanticMetricsStore(AgentsDbContext db)
    {
        _db = db;
    }

    public async Task<SemanticMetricsRaw> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var porDesfecho = await _db.SemanticDecisions.AsNoTracking().Where(d => d.DecidedAt >= since)
            .GroupBy(d => d.Outcome).Select(g => new { Outcome = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);

        var turnos = await _db.Turns.AsNoTracking().Where(t => t.EndedAt >= since && t.EndReason != null)
            .GroupBy(t => t.EndReason!).Select(g => new { Reason = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);

        // Latencia da etapa semantica (vetorizar a pergunta + buscar), so das decisoes com busca de fato.
        var latencias = await _db.SemanticDecisions.AsNoTracking().Where(d => d.DecidedAt >= since && d.Outcome != SemanticOutcome.Unavailable)
            .OrderByDescending(d => d.DecidedAt).Take(MaxLatencyRows).Select(d => d.EmbeddingMilliseconds + d.SearchMilliseconds).ToListAsync(cancellationToken);

        int Soma(Func<string, bool> filtro) => turnos.Where(t => filtro(t.Reason)).Sum(t => t.Count);
        return new SemanticMetricsRaw(
            porDesfecho.ToDictionary(x => x.Outcome, x => x.Count),
            Soma(r => r == AiTurnEndReasons.FaqHit),
            Soma(r => r == AiTurnEndReasons.SemanticHit),
            Soma(r => r == AiTurnEndReasons.Replied),
            Soma(r => r.StartsWith("handoff-", StringComparison.Ordinal)),
            latencias);
    }
}

/// <summary>
/// Grava a decisao semantica de um turno por UM comando SQL (ON CONFLICT DO NOTHING: o turno repetido conta uma vez). So numeros e codigos: nunca o texto do cliente. O tenant vem do contexto confiavel e
/// e conferido contra o do registro (SQL escrito a mao nao passa pelo filtro global do EF).
/// </summary>
internal sealed class SemanticDecisionRecorder : ISemanticDecisionRecorder
{
    private const string Sql = """
        INSERT INTO agents.semantic_decisions
            (tenant_id, turn_id, decided_at, mode, outcome, entry_ordinal, score, margin, definition_number, embedding_ms, search_ms, failure_code)
        VALUES (@tenant, @turn, @at, @mode, @outcome, @entry, @score, @margin, @definition, @embedding, @search, @failure)
        ON CONFLICT (tenant_id, turn_id) DO NOTHING
        """;

    private readonly AgentsDbContext _db;

    public SemanticDecisionRecorder(AgentsDbContext db)
    {
        _db = db;
    }

    public async Task RecordAsync(SemanticDecisionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Tenant.Value != _db.CurrentTenantId)
        {
            throw new CrossTenantAccessException("Operacao bloqueada: a decisao semantica e de outro tenant que o do contexto.");
        }

        var conexao = _db.Database.GetDbConnection();
        var abriu = conexao.State != ConnectionState.Open;
        if (abriu)
        {
            await conexao.OpenAsync(cancellationToken);
        }

        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = Sql;
            comando.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            Add(comando, "tenant", record.Tenant.Value);
            Add(comando, "turn", record.TurnId);
            Add(comando, "at", record.At.ToUniversalTime());
            Add(comando, "mode", (short)record.Mode);
            Add(comando, "outcome", (short)record.Outcome);
            Add(comando, "entry", record.EntryOrdinal);
            Add(comando, "score", record.Score);
            Add(comando, "margin", record.Margin);
            Add(comando, "definition", record.DefinitionNumber);
            Add(comando, "embedding", Math.Max(0, record.EmbeddingMilliseconds));
            Add(comando, "search", Math.Max(0, record.SearchMilliseconds));
            Add(comando, "failure", record.FailureCode is { Length: > 60 } c ? c[..60] : record.FailureCode);
            await comando.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (abriu)
            {
                await conexao.CloseAsync();
            }
        }
    }

    private static void Add(DbCommand comando, string nome, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.ParameterName = nome;
        parametro.Value = valor ?? DBNull.Value;
        comando.Parameters.Add(parametro);
    }
}
