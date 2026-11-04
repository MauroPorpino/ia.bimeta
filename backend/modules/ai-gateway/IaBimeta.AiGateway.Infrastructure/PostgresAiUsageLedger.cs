using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace IaBimeta.AiGateway.Infrastructure;

/// <summary>
/// A contabilidade de uso persistente (ADR 0017, seção I): conta no PostgreSQL, então vale ENTRE INSTÂNCIAS (BOUNDARIES LIM-1). O desenho:
/// <list type="bullet">
/// <item><b>Serialização por tenant:</b> o <c>TryBegin</c> toma <c>pg_advisory_xact_lock</c> sobre o tenant e solta no commit. Só serializa as vagas do MESMO tenant (uma colisão de hash só
/// serializa outro tenant a mais, sem dano). Não há contador <c>in_flight</c> mutável: a simultaneidade é a CONTAGEM das linhas abertas, e uma vaga órfã (o processo caiu) expira pela data
/// dela e deixa de contar, então nada vaza.</item>
/// <item><b>Quatro situações do consumo:</b> estimado (antes da chamada), RESERVADO (<c>state=1</c>: retém tokens enquanto a vaga está aberta), CONFIRMADO (<c>state=2</c>: o provedor informou, ou
/// provou-se que nada foi cobrado) e INDETERMINADO (<c>state=3</c>: pode ter sido cobrado, e cobra a reserva). As transições são <c>1→2</c>, <c>1→3</c> e <c>3→2</c> (a reconciliação, uma vez).</item>
/// <item><b>Sem dupla contagem:</b> a linha da chamada guarda o que está APLICADO à janela (<c>applied_kind</c>, <c>applied_tokens</c>, <c>applied_overage</c>), e toda transição calcula o delta desses
/// valores GRAVADOS, nunca do que o chamador informa, na MESMA transação do contador. A fonte de verdade é <c>ai_requests</c>; <c>usage_windows</c> é um cache reconstruível. Um gatilho do banco
/// só permite as transições válidas.</item>
/// <item><b>Janela:</b> cada janela guarda início, fim e duração com que abriu. Mudar a duração do período do tenant vale só para a PRÓXIMA janela, que começa no fim da ativa (ou no início alinhado
/// em UTC, o que for mais tarde): nada reinicia o consumo já feito, e uma redução de limite abaixo do já usado só bloqueia novas chamadas (sem estorno).</item>
/// </list>
/// Falha de infraestrutura (conexão, tempo esgotado, deadlock) vira <see cref="AiLedgerUnavailableException"/>; uma violação de integridade NÃO é mascarada: é defeito.
/// </summary>
internal sealed class PostgresAiUsageLedger : IAiUsageLedger
{
    private const int CommandTimeoutSeconds = 15;

    private readonly AiGatewayDbContext _db;

    public PostgresAiUsageLedger(AiGatewayDbContext db)
    {
        _db = db;
    }

    public async Task<AiLeaseResult> TryBeginAsync(AiLeaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureContextTenant(request.TenantId);

        try
        {
            return await BeginAsync(request, cancellationToken);
        }
        catch (Exception falha) when (PostgresErrors.IsInfrastructureFailure(falha))
        {
            throw new AiLedgerUnavailableException("A contabilidade de uso não respondeu ao pedido de vaga.", falha);
        }
    }

    public async Task CompleteAsync(AiLease lease, AiRequestRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(record);
        EnsureContextTenant(lease.TenantId);
        if (record.TenantId != lease.TenantId || record.AiRequestId != lease.AiRequestId)
        {
            throw new ArgumentException("O registro de consumo não é da vaga informada.", nameof(record));
        }

        try
        {
            await FinishAsync(lease, record, cancellationToken);
        }
        catch (Exception falha) when (PostgresErrors.IsInfrastructureFailure(falha))
        {
            throw new AiLedgerUnavailableException("A contabilidade de uso não respondeu à conclusão da chamada.", falha);
        }
    }

    private void EnsureContextTenant(IaBimeta.SharedKernel.TenantId tenantId)
    {
        // O SQL cru não passa pelo filtro global: a contabilidade só age para o tenant do contexto confiável.
        if (tenantId != _db.CurrentTenant)
        {
            throw new CrossTenantAccessException("Operação bloqueada: a contabilidade de IA de outro tenant não pode ser alterada neste contexto.");
        }
    }

    private async Task<AiLeaseResult> BeginAsync(AiLeaseRequest r, CancellationToken ct)
    {
        await using var transacao = await _db.Database.BeginTransactionAsync(ct);
        var conexao = (NpgsqlConnection)_db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction)transacao.GetDbTransaction();
        var tenant = r.TenantId.Value;
        var agora = r.Now.ToUniversalTime();

        // 1. Serializa SÓ as vagas deste tenant, entre instâncias, até o commit.
        await ExecuteAsync(conexao, tx, "SELECT pg_advisory_xact_lock(hashtextextended(@chave, 0))", ct, new NpgsqlParameter("chave", $"ai_gateway.ledger|{tenant:N}"));

        // 2. Vagas expiradas deste tenant viram consumo INDETERMINADO (cobram a reserva), e o contador da janela acompanha, na mesma transação.
        await ExecuteAsync(
            conexao,
            tx,
            """
            WITH expiradas AS (
                UPDATE ai_gateway.ai_requests
                   SET state = 3, completed_at = @agora, applied_kind = 3, applied_tokens = reserved_tokens, applied_overage = 0, outcome = 4, usage_certainty = 3
                 WHERE tenant_id = @tenant AND state = 1 AND lease_expires_at < @agora
             RETURNING window_start, reserved_tokens)
            UPDATE ai_gateway.usage_windows w
               SET indeterminate_tokens = w.indeterminate_tokens + s.total
              FROM (SELECT window_start, sum(reserved_tokens) AS total FROM expiradas GROUP BY window_start) s
             WHERE w.tenant_id = @tenant AND w.window_start = s.window_start
            """,
            ct,
            new NpgsqlParameter("agora", agora),
            new NpgsqlParameter("tenant", tenant));

        // 3. Simultaneidade: a CONTAGEM das vagas abertas (não há contador mutável para vazar).
        var abertas = await ScalarLongAsync(conexao, tx, "SELECT count(*) FROM ai_gateway.ai_requests WHERE tenant_id = @tenant AND state = 1", ct, new NpgsqlParameter("tenant", tenant));
        if (abertas >= r.Limits.MaxConcurrentCalls)
        {
            await transacao.CommitAsync(ct);
            return new AiLeaseResult(null, AiLimitRefusal.ConcurrentCalls);
        }

        // 4. A janela ativa (a que contém agora), com a duração com que ela ABRIU; se não há, abre a próxima sem sobrepor a anterior.
        var janela = await FindActiveWindowAsync(conexao, tx, tenant, agora, ct) ?? await OpenWindowAsync(conexao, tx, tenant, agora, r.Limits.Period, ct);

        if (janela.Calls >= r.Limits.MaxCallsPerPeriod)
        {
            await transacao.CommitAsync(ct);
            return new AiLeaseResult(null, AiLimitRefusal.CallsPerPeriod);
        }

        // 5. Tokens: o confirmado, o indeterminado e as reservas ABERTAS da janela, mais a reserva desta chamada, têm de caber no teto. Teto duro com reserva.
        var reservadoAberto = await ScalarLongAsync(
            conexao,
            tx,
            "SELECT coalesce(sum(reserved_tokens), 0) FROM ai_gateway.ai_requests WHERE tenant_id = @tenant AND state = 1 AND window_start = @janela",
            ct,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("janela", janela.Start));
        if (janela.ConfirmedTokens + janela.IndeterminateTokens + reservadoAberto + r.ReservedTokens > r.Limits.MaxTokensPerPeriod)
        {
            await transacao.CommitAsync(ct);
            return new AiLeaseResult(null, AiLimitRefusal.TokensPerPeriod);
        }

        // 6. Concede: conta a chamada na janela e abre a vaga com a reserva e a validade.
        var expira = agora + r.LeaseDuration;
        await ExecuteAsync(
            conexao,
            tx,
            "UPDATE ai_gateway.usage_windows SET calls = calls + 1 WHERE tenant_id = @tenant AND window_start = @janela",
            ct,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("janela", janela.Start));
        await ExecuteAsync(
            conexao,
            tx,
            """
            INSERT INTO ai_gateway.ai_requests
                (tenant_id, ai_request_id, state, correlation_id, provider_id, model_id, reserved_tokens, estimated_input_tokens, window_start, started_at, lease_expires_at, applied_kind)
            VALUES (@tenant, @id, 1, @correlacao, @provedor, @modelo, @reservado, @estimado, @janela, @agora, @expira, 0)
            """,
            ct,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("id", r.AiRequestId),
            new NpgsqlParameter("correlacao", r.CorrelationId),
            new NpgsqlParameter("provedor", r.ProviderId),
            new NpgsqlParameter("modelo", r.ModelId),
            new NpgsqlParameter("reservado", r.ReservedTokens),
            new NpgsqlParameter("estimado", r.EstimatedInputTokens),
            new NpgsqlParameter("janela", janela.Start),
            new NpgsqlParameter("agora", agora),
            new NpgsqlParameter("expira", expira));

        await transacao.CommitAsync(ct);
        return new AiLeaseResult(new AiLease(r.TenantId, r.AiRequestId, agora, r.ReservedTokens, expira), null);
    }

    private async Task FinishAsync(AiLease lease, AiRequestRecord record, CancellationToken ct)
    {
        await using var transacao = await _db.Database.BeginTransactionAsync(ct);
        var conexao = (NpgsqlConnection)_db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction)transacao.GetDbTransaction();
        var tenant = lease.TenantId.Value;

        // Trava a linha da chamada: o estado que se lê aqui é o ATUAL (uma expiração concorrente já commitada é vista), e ninguém o muda até o fim da transação.
        long reservado, aplicadoTokens, aplicadoExcedente;
        int estado, aplicadoTipo;
        DateTimeOffset janela;
        await using (var leitura = Command(
            conexao,
            tx,
            "SELECT state, applied_kind, applied_tokens, applied_overage, reserved_tokens, window_start FROM ai_gateway.ai_requests WHERE tenant_id = @tenant AND ai_request_id = @id FOR UPDATE",
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("id", lease.AiRequestId)))
        await using (var linhas = await leitura.ExecuteReaderAsync(ct))
        {
            if (!await linhas.ReadAsync(ct))
            {
                throw new InvalidOperationException("A vaga nunca foi concedida a este tenant.");
            }

            estado = linhas.GetInt16(0);
            aplicadoTipo = linhas.GetInt16(1);
            aplicadoTokens = linhas.GetInt64(2);
            aplicadoExcedente = linhas.GetInt64(3);
            reservado = linhas.GetInt64(4);
            janela = linhas.GetFieldValue<DateTimeOffset>(5);
        }

        // Idempotente: uma chamada já CONFIRMADA nunca é contada de novo, nem com outro valor.
        if (estado == (int)AiRequestState.Confirmed)
        {
            await transacao.CommitAsync(ct);
            return;
        }

        int novoEstado, novoTipo;
        long novosTokens, novoExcedente;
        if (record.UsageCertainty == AiUsageCertainty.Unknown)
        {
            // Indeterminado: cobra a reserva. Se já era indeterminado (a vaga expirou), nada muda: não se conta duas vezes.
            if (estado == (int)AiRequestState.Indeterminate)
            {
                await transacao.CommitAsync(ct);
                return;
            }

            novoEstado = (int)AiRequestState.Indeterminate;
            novoTipo = (int)AiRequestState.Indeterminate;
            novosTokens = reservado;
            novoExcedente = 0;
        }
        else
        {
            // Confirmado: o consumo informado (zero, com evidência de que nada foi cobrado). Estando indeterminado, o indeterminado sai e o real entra (reconciliação, uma vez).
            var real = record.UsageCertainty == AiUsageCertainty.NoneIncurred ? 0L : (long)record.Usage.InputTokens + record.Usage.OutputTokens;
            novoEstado = (int)AiRequestState.Confirmed;
            novoTipo = (int)AiRequestState.Confirmed;
            novosTokens = real;
            novoExcedente = Math.Max(0, real - reservado);
        }

        var uso = record.Usage;
        await ExecuteAsync(
            conexao,
            tx,
            """
            UPDATE ai_gateway.ai_requests
               SET state = @estado, completed_at = @agora, input_tokens = @entrada, cached_input_tokens = @cache, cache_write_tokens = @escrita, output_tokens = @saida, reasoning_tokens = @raciocinio,
                   applied_kind = @tipo, applied_tokens = @tokens, applied_overage = @excedente, estimated_cost = @custo, cost_currency = @moeda, latency_ms = @latencia,
                   outcome = @resultado, stop_reason = @parada, error_kind = @erro, error_code = @codigo, provider_request_id = @provedor, usage_certainty = @certeza
             WHERE tenant_id = @tenant AND ai_request_id = @id AND state IN (1, 3)
            """,
            ct,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("id", lease.AiRequestId),
            new NpgsqlParameter("estado", (short)novoEstado),
            new NpgsqlParameter("agora", record.OccurredAt.ToUniversalTime()),
            new NpgsqlParameter("entrada", uso.InputTokens),
            new NpgsqlParameter("cache", uso.CachedInputTokens),
            new NpgsqlParameter("escrita", uso.CacheWriteTokens),
            new NpgsqlParameter("saida", uso.OutputTokens),
            Nullable("raciocinio", NpgsqlDbType.Integer, uso.ReasoningTokens),
            new NpgsqlParameter("tipo", (short)novoTipo),
            new NpgsqlParameter("tokens", novosTokens),
            new NpgsqlParameter("excedente", novoExcedente),
            new NpgsqlParameter("custo", record.EstimatedCost.Amount),
            new NpgsqlParameter("moeda", record.EstimatedCost.Currency),
            new NpgsqlParameter("latencia", (int)Math.Min(int.MaxValue, Math.Max(0, record.Latency.TotalMilliseconds))),
            new NpgsqlParameter("resultado", (short)record.Outcome),
            Nullable("parada", NpgsqlDbType.Smallint, record.StopReason is { } p ? (short?)p : null),
            Nullable("erro", NpgsqlDbType.Smallint, record.ErrorKind is { } e ? (short?)e : null),
            Nullable("codigo", NpgsqlDbType.Varchar, record.ErrorCode),
            Nullable("provedor", NpgsqlDbType.Varchar, record.ProviderRequestId),
            new NpgsqlParameter("certeza", (short)record.UsageCertainty));

        // O contador da janela recebe só a DIFERENÇA entre o que estava aplicado (lido da linha travada) e o que passa a valer.
        var deltaConfirmado = (novoTipo == (int)AiRequestState.Confirmed ? novosTokens : 0) - (aplicadoTipo == (int)AiRequestState.Confirmed ? aplicadoTokens : 0);
        var deltaIndeterminado = (novoTipo == (int)AiRequestState.Indeterminate ? novosTokens : 0) - (aplicadoTipo == (int)AiRequestState.Indeterminate ? aplicadoTokens : 0);
        var deltaExcedente = (novoTipo == (int)AiRequestState.Confirmed ? novoExcedente : 0) - (aplicadoTipo == (int)AiRequestState.Confirmed ? aplicadoExcedente : 0);
        await ExecuteAsync(
            conexao,
            tx,
            """
            UPDATE ai_gateway.usage_windows
               SET confirmed_tokens = confirmed_tokens + @confirmado, indeterminate_tokens = indeterminate_tokens + @indeterminado, overage_tokens = overage_tokens + @excedente
             WHERE tenant_id = @tenant AND window_start = @janela
            """,
            ct,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("janela", janela),
            new NpgsqlParameter("confirmado", deltaConfirmado),
            new NpgsqlParameter("indeterminado", deltaIndeterminado),
            new NpgsqlParameter("excedente", deltaExcedente));

        await transacao.CommitAsync(ct);
    }

    private readonly record struct WindowSnapshot(DateTimeOffset Start, DateTimeOffset End, int Calls, long ConfirmedTokens, long IndeterminateTokens);

    private static async Task<WindowSnapshot?> FindActiveWindowAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, Guid tenant, DateTimeOffset agora, CancellationToken ct)
    {
        await using var comando = Command(
            conexao,
            tx,
            """
            SELECT window_start, window_end, calls, confirmed_tokens, indeterminate_tokens
              FROM ai_gateway.usage_windows
             WHERE tenant_id = @tenant AND window_start <= @agora AND window_end > @agora
             ORDER BY window_start DESC LIMIT 1
            """,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("agora", agora));
        await using var linhas = await comando.ExecuteReaderAsync(ct);
        return await linhas.ReadAsync(ct)
            ? new WindowSnapshot(linhas.GetFieldValue<DateTimeOffset>(0), linhas.GetFieldValue<DateTimeOffset>(1), linhas.GetInt32(2), linhas.GetInt64(3), linhas.GetInt64(4))
            : null;
    }

    private static async Task<WindowSnapshot> OpenWindowAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, Guid tenant, DateTimeOffset agora, TimeSpan periodo, CancellationToken ct)
    {
        var segundos = (long)Math.Max(1, Math.Ceiling(periodo.TotalSeconds));

        // O início alinhado ao relógio UTC, mas nunca antes do fim da janela anterior: a duração nova só vale a partir da próxima janela, sem sobrepor a anterior.
        var alinhado = DateTimeOffset.FromUnixTimeSeconds(agora.ToUnixTimeSeconds() / segundos * segundos);
        DateTimeOffset? fimAnterior = null;
        await using (var comando = Command(conexao, tx, "SELECT max(window_end) FROM ai_gateway.usage_windows WHERE tenant_id = @tenant", new NpgsqlParameter("tenant", tenant)))
        {
            var valor = await comando.ExecuteScalarAsync(ct);
            if (valor is not null and not DBNull)
            {
                // O Npgsql devolve o timestamptz como DateTime (UTC) em ExecuteScalar.
                fimAnterior = valor is DateTime data ? new DateTimeOffset(data.ToUniversalTime()) : (DateTimeOffset)valor;
            }
        }

        var inicio = fimAnterior is { } fim && fim > alinhado ? fim : alinhado;
        var termino = inicio + TimeSpan.FromSeconds(segundos);
        await ExecuteAsync(
            conexao,
            tx,
            "INSERT INTO ai_gateway.usage_windows (tenant_id, window_start, window_end, period_seconds) VALUES (@tenant, @inicio, @termino, @segundos) ON CONFLICT (tenant_id, window_start) DO NOTHING",
            ct,
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("inicio", inicio),
            new NpgsqlParameter("termino", termino),
            new NpgsqlParameter("segundos", (int)segundos));

        // Relê (a linha pode ter sido criada por quem veio antes, ou já ter contadores).
        return await FindWindowAsync(conexao, tx, tenant, inicio, ct);
    }

    private static async Task<WindowSnapshot> FindWindowAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, Guid tenant, DateTimeOffset inicio, CancellationToken ct)
    {
        await using var comando = Command(
            conexao,
            tx,
            "SELECT window_start, window_end, calls, confirmed_tokens, indeterminate_tokens FROM ai_gateway.usage_windows WHERE tenant_id = @tenant AND window_start = @inicio",
            new NpgsqlParameter("tenant", tenant),
            new NpgsqlParameter("inicio", inicio));
        await using var linhas = await comando.ExecuteReaderAsync(ct);
        if (!await linhas.ReadAsync(ct))
        {
            throw new InvalidOperationException("A janela de uso acabou de ser criada e não foi encontrada.");
        }

        return new WindowSnapshot(linhas.GetFieldValue<DateTimeOffset>(0), linhas.GetFieldValue<DateTimeOffset>(1), linhas.GetInt32(2), linhas.GetInt64(3), linhas.GetInt64(4));
    }

    private static NpgsqlCommand Command(NpgsqlConnection conexao, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] parametros)
    {
        var comando = new NpgsqlCommand(sql, conexao, tx) { CommandTimeout = CommandTimeoutSeconds };
        comando.Parameters.AddRange(parametros);
        return comando;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] parametros)
    {
        await using var comando = Command(conexao, tx, sql, parametros);
        await comando.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] parametros)
    {
        await using var comando = Command(conexao, tx, sql, parametros);
        return Convert.ToInt64(await comando.ExecuteScalarAsync(ct) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static NpgsqlParameter Nullable(string nome, NpgsqlDbType tipo, object? valor) =>
        new(nome, tipo) { Value = valor ?? DBNull.Value };
}
