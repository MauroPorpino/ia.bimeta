using System.Globalization;
using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;
using Npgsql;
using NpgsqlTypes;

namespace IaBimeta.Knowledge.Infrastructure;

/// <summary>
/// O índice semântico no PostgreSQL com pgvector (ADR 0034). Uma linha por texto aprovado (pergunta ou alias) com o vetor. Todo comando que toca dado de um tenant filtra por <c>tenant_id</c>: não há consulta sem tenant,
/// exceto a fila de vetorização (visão de sistema, que só devolve o texto aprovado a vetorizar). A busca é EXATA dentro do tenant (poucas centenas de textos por empresa), ordenada pela distância de cosseno: sem índice
/// aproximado, não há perda de resultado por filtro de tenant. Parâmetros sempre; o vetor entra como literal de texto convertido por <c>::vector</c>.
/// </summary>
internal sealed class PostgresSemanticStore : ISemanticStore
{
    private const short Pending = 1;
    private const short Ready = 2;
    private const short Failed = 3;

    private readonly Func<string> _connectionString;

    public PostgresSemanticStore(Func<string> connectionString)
    {
        _connectionString = connectionString;
    }

    internal static string VectorLiteral(float[] vector) => "[" + string.Join(',', vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conexao = new NpgsqlConnection(_connectionString());
        await conexao.OpenAsync(cancellationToken);
        return conexao;
    }

    public async Task<SemanticSyncResult> ReplaceAsync(TenantId tenant, SemanticSource source, string modelId, int dimension, Func<string, string> hash, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var transacao = await conexao.BeginTransactionAsync(cancellationToken);

        // Uma publicação por vez por tenant.
        await using (var trava = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('knowledge:' || @t))", conexao, transacao))
        {
            trava.Parameters.AddWithValue("t", tenant.Value.ToString("N"));
            await trava.ExecuteNonQueryAsync(cancellationToken);
        }

        var existentes = new Dictionary<(int, short, int), (string Hash, string Model, int Dim, short Status)>();
        await using (var leitura = new NpgsqlCommand("SELECT entry_ordinal, kind, alias_ordinal, text_hash, model_id, dimension, status FROM knowledge.semantic_texts WHERE tenant_id = @t", conexao, transacao))
        {
            leitura.Parameters.AddWithValue("t", tenant.Value);
            await using var linhas = await leitura.ExecuteReaderAsync(cancellationToken);
            while (await linhas.ReadAsync(cancellationToken))
            {
                existentes[(linhas.GetInt32(0), linhas.GetInt16(1), linhas.GetInt32(2))] = (linhas.GetString(3), linhas.GetString(4), linhas.GetInt32(5), linhas.GetInt16(6));
            }
        }

        var desejados = source.Texts.GroupBy(t => (t.EntryOrdinal, (short)t.Kind, t.AliasOrdinal)).Select(g => g.First()).ToList();
        var chaves = desejados.Select(t => (t.EntryOrdinal, (short)t.Kind, t.AliasOrdinal)).ToHashSet();
        var removidos = existentes.Keys.Count(k => !chaves.Contains(k));
        var iguais = desejados.Count(t => existentes.TryGetValue((t.EntryOrdinal, (short)t.Kind, t.AliasOrdinal), out var e)
            && e.Hash == hash(t.Text) && e.Model == modelId && e.Dim == dimension && e.Status != Failed);

        var agora = DateTimeOffset.UtcNow;

        // 1) o que saiu do conhecimento publicado sai do índice.
        await using (var apagar = new NpgsqlCommand(
            "DELETE FROM knowledge.semantic_texts s WHERE s.tenant_id = @t AND NOT EXISTS (SELECT 1 FROM unnest(@e::int[], @k::smallint[], @a::int[]) AS d(e, k, a) WHERE d.e = s.entry_ordinal AND d.k = s.kind AND d.a = s.alias_ordinal)",
            conexao, transacao))
        {
            apagar.Parameters.AddWithValue("t", tenant.Value);
            apagar.Parameters.AddWithValue("e", desejados.Select(d => d.EntryOrdinal).ToArray());
            apagar.Parameters.AddWithValue("k", desejados.Select(d => (short)d.Kind).ToArray());
            apagar.Parameters.AddWithValue("a", desejados.Select(d => d.AliasOrdinal).ToArray());
            await apagar.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) cada texto: se o resumo, o modelo e a dimensão são os mesmos e o texto não falhou, só atualiza os metadados; senão volta a pendente, reaproveitando o vetor de um texto IGUAL já vetorizado do tenant.
        foreach (var texto in desejados)
        {
            var resumo = hash(texto.Text);
            await using var upsert = new NpgsqlCommand(
                """
                WITH reuso AS (
                  SELECT embedding FROM knowledge.semantic_texts
                  WHERE tenant_id = @t AND text_hash = @h AND model_id = @m AND dimension = @d AND status = 2 LIMIT 1
                )
                INSERT INTO knowledge.semantic_texts AS s
                  (tenant_id, entry_ordinal, kind, alias_ordinal, definition_number, locale, auto_reply, text, text_hash, model_id, dimension, status, embedding, attempts, created_at, updated_at)
                SELECT @t, @e, @k, @a, @n, @l, @r, @x, @h, @m, @d,
                       CASE WHEN EXISTS (SELECT 1 FROM reuso) THEN 2 ELSE 1 END,
                       (SELECT embedding FROM reuso), 0, @now, @now
                ON CONFLICT (tenant_id, entry_ordinal, kind, alias_ordinal) DO UPDATE SET
                  definition_number = EXCLUDED.definition_number, locale = EXCLUDED.locale, auto_reply = EXCLUDED.auto_reply, updated_at = EXCLUDED.updated_at, text = EXCLUDED.text,
                  status = CASE WHEN s.text_hash = EXCLUDED.text_hash AND s.model_id = EXCLUDED.model_id AND s.dimension = EXCLUDED.dimension AND s.status <> 3 THEN s.status ELSE EXCLUDED.status END,
                  embedding = CASE WHEN s.text_hash = EXCLUDED.text_hash AND s.model_id = EXCLUDED.model_id AND s.dimension = EXCLUDED.dimension AND s.status <> 3 THEN s.embedding ELSE EXCLUDED.embedding END,
                  attempts = CASE WHEN s.text_hash = EXCLUDED.text_hash AND s.model_id = EXCLUDED.model_id AND s.dimension = EXCLUDED.dimension AND s.status <> 3 THEN s.attempts ELSE 0 END,
                  last_error = CASE WHEN s.text_hash = EXCLUDED.text_hash AND s.model_id = EXCLUDED.model_id AND s.dimension = EXCLUDED.dimension AND s.status <> 3 THEN s.last_error ELSE NULL END,
                  next_attempt_at = CASE WHEN s.text_hash = EXCLUDED.text_hash AND s.model_id = EXCLUDED.model_id AND s.dimension = EXCLUDED.dimension AND s.status <> 3 THEN s.next_attempt_at ELSE NULL END,
                  claimed_until = CASE WHEN s.text_hash = EXCLUDED.text_hash AND s.model_id = EXCLUDED.model_id AND s.dimension = EXCLUDED.dimension AND s.status <> 3 THEN s.claimed_until ELSE NULL END,
                  text_hash = EXCLUDED.text_hash, model_id = EXCLUDED.model_id, dimension = EXCLUDED.dimension
                """,
                conexao, transacao);
            upsert.Parameters.AddWithValue("t", tenant.Value);
            upsert.Parameters.AddWithValue("e", texto.EntryOrdinal);
            upsert.Parameters.AddWithValue("k", (short)texto.Kind);
            upsert.Parameters.AddWithValue("a", texto.AliasOrdinal);
            upsert.Parameters.AddWithValue("n", source.DefinitionNumber);
            upsert.Parameters.AddWithValue("l", source.Locale);
            upsert.Parameters.AddWithValue("r", texto.AutoReplyEligible);
            upsert.Parameters.AddWithValue("x", texto.Text);
            upsert.Parameters.AddWithValue("h", resumo);
            upsert.Parameters.AddWithValue("m", modelId);
            upsert.Parameters.AddWithValue("d", dimension);
            upsert.Parameters.AddWithValue("now", agora);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        int pendentes;
        await using (var contar = new NpgsqlCommand("SELECT count(*) FROM knowledge.semantic_texts WHERE tenant_id = @t AND status = 1", conexao, transacao))
        {
            contar.Parameters.AddWithValue("t", tenant.Value);
            pendentes = Convert.ToInt32(await contar.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await transacao.CommitAsync(cancellationToken);
        return new SemanticSyncResult(desejados.Count, iguais, pendentes, removidos);
    }

    public async Task DeleteAllAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand("DELETE FROM knowledge.semantic_texts WHERE tenant_id = @t", conexao);
        comando.Parameters.AddWithValue("t", tenant.Value);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SemanticIndexStatus> StatusAsync(TenantId tenant, string modelId, int dimension, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            """
            SELECT max(definition_number),
                   count(*) FILTER (WHERE status = 2 AND model_id = @m AND dimension = @d),
                   count(*) FILTER (WHERE status = 1 AND model_id = @m AND dimension = @d),
                   count(*) FILTER (WHERE status = 3 AND model_id = @m AND dimension = @d),
                   count(*) FILTER (WHERE model_id <> @m OR dimension <> @d)
            FROM knowledge.semantic_texts WHERE tenant_id = @t
            """,
            conexao);
        comando.Parameters.AddWithValue("t", tenant.Value);
        comando.Parameters.AddWithValue("m", modelId);
        comando.Parameters.AddWithValue("d", dimension);
        await using var linha = await comando.ExecuteReaderAsync(cancellationToken);
        await linha.ReadAsync(cancellationToken);
        return new SemanticIndexStatus(linha.IsDBNull(0) ? null : linha.GetInt32(0), modelId, dimension, (int)linha.GetInt64(1), (int)linha.GetInt64(2), (int)linha.GetInt64(3), (int)linha.GetInt64(4));
    }

    public async Task<int> RequeueOtherModelsAsync(int limit, string modelId, int dimension, Func<string, string> hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        var linhas = new List<(Guid Tenant, int E, short K, int A, string Text)>();
        await using (var leitura = new NpgsqlCommand(
            "SELECT tenant_id, entry_ordinal, kind, alias_ordinal, text FROM knowledge.semantic_texts WHERE (model_id <> @m OR dimension <> @d) ORDER BY updated_at LIMIT @n",
            conexao))
        {
            leitura.Parameters.AddWithValue("m", modelId);
            leitura.Parameters.AddWithValue("d", dimension);
            leitura.Parameters.AddWithValue("n", limit);
            await using var r = await leitura.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                linhas.Add((r.GetGuid(0), r.GetInt32(1), r.GetInt16(2), r.GetInt32(3), r.GetString(4)));
            }
        }

        foreach (var (tenant, e, k, a, texto) in linhas)
        {
            await using var atualizar = new NpgsqlCommand(
                "UPDATE knowledge.semantic_texts SET model_id = @m, dimension = @d, text_hash = @h, status = 1, embedding = NULL, attempts = 0, last_error = NULL, next_attempt_at = NULL, claimed_until = NULL, updated_at = @now " +
                "WHERE tenant_id = @t AND entry_ordinal = @e AND kind = @k AND alias_ordinal = @a AND (model_id <> @m OR dimension <> @d)",
                conexao);
            atualizar.Parameters.AddWithValue("m", modelId);
            atualizar.Parameters.AddWithValue("d", dimension);
            atualizar.Parameters.AddWithValue("h", hash(texto));
            atualizar.Parameters.AddWithValue("now", now);
            atualizar.Parameters.AddWithValue("t", tenant);
            atualizar.Parameters.AddWithValue("e", e);
            atualizar.Parameters.AddWithValue("k", k);
            atualizar.Parameters.AddWithValue("a", a);
            await atualizar.ExecuteNonQueryAsync(cancellationToken);
        }

        return linhas.Count;
    }

    public async Task<IReadOnlyList<PendingSemanticText>> ClaimPendingAsync(int batch, string modelId, int dimension, TimeSpan lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            """
            WITH c AS (
              SELECT tenant_id, entry_ordinal, kind, alias_ordinal FROM knowledge.semantic_texts
              WHERE status = 1 AND model_id = @m AND dimension = @d
                AND (next_attempt_at IS NULL OR next_attempt_at <= @now) AND (claimed_until IS NULL OR claimed_until <= @now)
              ORDER BY updated_at LIMIT @n FOR UPDATE SKIP LOCKED
            )
            UPDATE knowledge.semantic_texts s SET claimed_until = @ate
            FROM c WHERE s.tenant_id = c.tenant_id AND s.entry_ordinal = c.entry_ordinal AND s.kind = c.kind AND s.alias_ordinal = c.alias_ordinal
            RETURNING s.tenant_id, s.entry_ordinal, s.kind, s.alias_ordinal, s.text, s.text_hash, s.attempts
            """,
            conexao);
        comando.Parameters.AddWithValue("m", modelId);
        comando.Parameters.AddWithValue("d", dimension);
        comando.Parameters.AddWithValue("now", now);
        comando.Parameters.AddWithValue("n", batch);
        comando.Parameters.AddWithValue("ate", now + lease);
        var itens = new List<PendingSemanticText>();
        await using var linhas = await comando.ExecuteReaderAsync(cancellationToken);
        while (await linhas.ReadAsync(cancellationToken))
        {
            itens.Add(new PendingSemanticText(new TenantId(linhas.GetGuid(0)), linhas.GetInt32(1), (SemanticTextKind)linhas.GetInt16(2), linhas.GetInt32(3), linhas.GetString(4), linhas.GetString(5), linhas.GetInt32(6)));
        }

        return itens;
    }

    public async Task<bool> CompleteAsync(PendingSemanticText item, float[] embedding, string modelId, int dimension, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            "UPDATE knowledge.semantic_texts SET status = 2, embedding = @v::vector, attempts = 0, last_error = NULL, next_attempt_at = NULL, claimed_until = NULL, updated_at = @now " +
            "WHERE tenant_id = @t AND entry_ordinal = @e AND kind = @k AND alias_ordinal = @a AND text_hash = @h AND status = 1 AND model_id = @m AND dimension = @d",
            conexao);
        AddKey(comando, item);
        comando.Parameters.AddWithValue("v", PostgresSemanticStore.VectorLiteral(embedding));
        comando.Parameters.AddWithValue("h", item.TextHash);
        comando.Parameters.AddWithValue("m", modelId);
        comando.Parameters.AddWithValue("d", dimension);
        comando.Parameters.AddWithValue("now", now);
        return await comando.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task FailAsync(PendingSemanticText item, string code, DateTimeOffset? nextAttempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            "UPDATE knowledge.semantic_texts SET attempts = attempts + 1, last_error = @c, claimed_until = NULL, next_attempt_at = @p, status = CASE WHEN @p IS NULL THEN 3 ELSE 1 END, updated_at = @now " +
            "WHERE tenant_id = @t AND entry_ordinal = @e AND kind = @k AND alias_ordinal = @a AND text_hash = @h AND status = 1",
            conexao);
        AddKey(comando, item);
        comando.Parameters.AddWithValue("h", item.TextHash);
        comando.Parameters.AddWithValue("c", code.Length > 60 ? code[..60] : code);
        comando.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.TimestampTz) { Value = nextAttempt is null ? DBNull.Value : nextAttempt.Value });
        comando.Parameters.AddWithValue("now", now);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(TenantId tenant, float[] query, string locale, bool onlyAutoReply, string modelId, int dimension, int limit, CancellationToken cancellationToken)
    {
        await using var conexao = await OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            """
            SELECT definition_number, entry_ordinal, kind, alias_ordinal, 1 - (embedding <=> @q::vector) AS score
            FROM knowledge.semantic_texts
            WHERE tenant_id = @t AND status = 2 AND model_id = @m AND dimension = @d AND locale = @l AND (@auto = false OR auto_reply)
            ORDER BY embedding <=> @q::vector LIMIT @n
            """,
            conexao);
        comando.Parameters.AddWithValue("t", tenant.Value);
        comando.Parameters.AddWithValue("q", VectorLiteral(query));
        comando.Parameters.AddWithValue("m", modelId);
        comando.Parameters.AddWithValue("d", dimension);
        comando.Parameters.AddWithValue("l", locale);
        comando.Parameters.AddWithValue("auto", onlyAutoReply);
        comando.Parameters.AddWithValue("n", limit);
        var acertos = new List<SemanticHit>();
        await using var linhas = await comando.ExecuteReaderAsync(cancellationToken);
        while (await linhas.ReadAsync(cancellationToken))
        {
            acertos.Add(new SemanticHit(linhas.GetInt32(0), linhas.GetInt32(1), (SemanticTextKind)linhas.GetInt16(2), linhas.GetInt32(3), linhas.GetDouble(4)));
        }

        return acertos;
    }

    private static void AddKey(NpgsqlCommand comando, PendingSemanticText item)
    {
        comando.Parameters.AddWithValue("t", item.Tenant.Value);
        comando.Parameters.AddWithValue("e", item.EntryOrdinal);
        comando.Parameters.AddWithValue("k", (short)item.Kind);
        comando.Parameters.AddWithValue("a", item.AliasOrdinal);
    }
}
