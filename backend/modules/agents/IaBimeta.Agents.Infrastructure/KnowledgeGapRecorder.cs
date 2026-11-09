using System.Data;
using System.Data.Common;
using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IaBimeta.Agents.Infrastructure;

/// <summary>O hasher do ambiente (nulo sem chave: o registro de lacunas fica desligado). Singleton: a chave é lida uma vez, na partida.</summary>
internal sealed class KnowledgeGapHasherHolder
{
    public KnowledgeGapHasherHolder(KnowledgeGapHasher? hasher)
    {
        Hasher = hasher;
    }

    public KnowledgeGapHasher? Hasher { get; }
}

/// <summary>
/// Registra a ocorrência de uma lacuna do FAQ (ADR 0030) por UM comando SQL atômico: a lacuna é criada ou atualizada pelo hash do tenant (<c>ON CONFLICT</c>, então dois turnos da mesma pergunta ao mesmo tempo
/// caem na MESMA lacuna) e a ocorrência é gravada pela chave <c>(tenant, turno)</c> (<c>DO NOTHING</c>, então o turno repetido depois de uma queda conta uma vez). Atingido o teto de lacunas do tenant, uma pergunta
/// NOVA não abre lacuna (as que existem continuam contando). Só grava o hash e as referências, nunca o texto. O tenant vem do contexto confiável e é conferido contra o do candidato: SQL escrito à mão não passa
/// pelo filtro global do EF. Sem chave de hash ou com o registro desligado, não faz nada. Quem o chama (o <c>TurnRunner</c>) trata a falha como melhor esforço.
/// </summary>
internal sealed class KnowledgeGapRecorder : IKnowledgeGapRecorder
{
    private const string Sql = """
        WITH g AS (
            INSERT INTO agents.knowledge_gaps (tenant_id, id, question_hash, kind, status, first_seen_at, last_seen_at, revision)
            SELECT @tenant, @gap, @hash, @kind, 1, @at, @at, 1
            WHERE EXISTS (SELECT 1 FROM agents.knowledge_gaps WHERE tenant_id = @tenant AND question_hash = @hash)
               OR (SELECT count(*) FROM agents.knowledge_gaps WHERE tenant_id = @tenant) < @cap
            ON CONFLICT (tenant_id, question_hash) DO UPDATE
                SET last_seen_at = GREATEST(agents.knowledge_gaps.last_seen_at, EXCLUDED.last_seen_at), kind = EXCLUDED.kind
            RETURNING id
        )
        INSERT INTO agents.knowledge_gap_occurrences (tenant_id, turn_id, gap_id, session_id, conversation_id, message_id, occurred_at)
        SELECT @tenant, @turn, g.id, @session, @conversation, @message, @at FROM g
        ON CONFLICT (tenant_id, turn_id) DO NOTHING
        """;

    private readonly AgentsDbContext _db;
    private readonly AgentsOptions _options;
    private readonly KnowledgeGapHasherHolder _hasher;

    public KnowledgeGapRecorder(AgentsDbContext db, AgentsOptions options, KnowledgeGapHasherHolder hasher)
    {
        _db = db;
        _options = options;
        _hasher = hasher;
    }

    public async Task RecordAsync(KnowledgeGapCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!_options.KnowledgeGapsEnabled || _hasher.Hasher is not { } hasher || candidate.NormalizedQuestion.Length == 0)
        {
            return;
        }

        if (candidate.Tenant.Value != _db.CurrentTenantId)
        {
            throw new CrossTenantAccessException("Operação bloqueada: a lacuna é de outro tenant que o do contexto.");
        }

        var hash = hasher.Hash(candidate.Tenant, candidate.NormalizedQuestion);
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
            Add(comando, "tenant", candidate.Tenant.Value);
            Add(comando, "gap", Guid.NewGuid());
            Add(comando, "hash", hash);
            Add(comando, "kind", (short)candidate.Kind);
            Add(comando, "at", candidate.At.ToUniversalTime());
            Add(comando, "cap", _options.KnowledgeGapMaxPerTenant);
            Add(comando, "turn", candidate.TurnId);
            Add(comando, "session", candidate.SessionId);
            Add(comando, "conversation", candidate.ConversationId);
            Add(comando, "message", candidate.MessageId);
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

    private static void Add(DbCommand comando, string nome, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.ParameterName = nome;
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }
}
