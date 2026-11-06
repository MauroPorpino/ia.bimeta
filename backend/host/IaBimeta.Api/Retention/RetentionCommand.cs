using IaBimeta.SharedKernel;
using Npgsql;

namespace IaBimeta.Api.Retention;

/// <summary>
/// O comando <c>retention</c> do host (ADR 0025): <c>retention report</c> (somente leitura: o que o expurgo removeria e o inventário das tabelas) e <c>retention purge --confirm</c> (executa o expurgo TÉCNICO). Roda na
/// máquina de quem opera, com acesso ao banco: não existe rota HTTP para isto. NUNCA toca dado pessoal nem registro de referência legal: mensagens, contatos, atendimentos, trilha de auditoria e ledger de IA só aparecem
/// no relatório. Sai com 0 (concluído), 1 (falha, inclusive configuração de empresa inexistente) ou 2 (uso inválido ou purge sem --confirm).
/// </summary>
internal static class RetentionCommand
{
    public const string Verb = "retention";

    internal const string Usage = "Uso: retention report | retention purge --confirm";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        var purge = false;
        var confirmed = false;
        if (args.Length == 0 || args[0] is not ("report" or "purge"))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        purge = args[0] == "purge";
        foreach (var argumento in args.Skip(1))
        {
            if (argumento == "--confirm" && purge)
            {
                confirmed = true;
                continue;
            }

            await output.WriteLineAsync($"Argumento desconhecido: {argumento}");
            await output.WriteLineAsync(Usage);
            return 2;
        }

        if (purge && !confirmed)
        {
            await output.WriteLineAsync("O expurgo só roda com --confirm. Rode antes 'retention report' e confira o que será removido.");
            return 2;
        }

        // A mesma composição do Api, sem abrir porta nem iniciar serviço algum, com só avisos e erros no console.
        await using var app = ApiHost.Build(Array.Empty<string>(), builder => builder.Logging.SetMinimumLevel(LogLevel.Warning));
        return await ExecuteAsync(app.Services, purge, output);
    }

    internal static async Task<int> ExecuteAsync(IServiceProvider root, bool purge, TextWriter output, CancellationToken cancellationToken = default)
    {
        RetentionRunReport relatorio;
        try
        {
            relatorio = await RetentionRunner.RunAsync(root, dryRun: !purge, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"Retenção não executada: {ex.Message}");
            return 1;
        }

        await output.WriteLineAsync(purge ? "Expurgo técnico executado." : "Relatório de retenção (somente leitura: nada foi alterado).");
        await output.WriteLineAsync();
        await output.WriteLineAsync("Categorias técnicas:");
        foreach (var r in relatorio.Results)
        {
            var estado = r.Skipped is not null ? r.Skipped : purge ? $"{r.Deleted} removido(s) de {r.Eligible} elegível(is)" : $"{r.Eligible} elegível(is)";
            await output.WriteLineAsync($"  {r.Task} [{r.Pass}, {r.Days} d]: {estado}");
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync("Eventos mortos (nunca expurgados por esta rotina; reenfileirar ou descartar com autorização):");
        foreach (var m in relatorio.DeadLetters)
        {
            await output.WriteLineAsync($"  {m.Outbox}: {m.Count}" + (m.Oldest is { } data ? $", o mais antigo de {data:yyyy-MM-dd}" : string.Empty));
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync("Inventário (dados pessoais e registros de referência NÃO têm expurgo automático: dependem de decisão jurídica):");
        try
        {
            foreach (var linha in await ReadInventoryAsync(root, cancellationToken))
            {
                await output.WriteLineAsync($"  {linha.Table,-44} {RetentionInventory.Describe(linha.Class),-32} ~{linha.EstimatedRows,9} linhas  {linha.Bytes / 1024.0 / 1024.0,8:F1} MB");
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            await output.WriteLineAsync($"  (inventário indisponível: {ex.GetType().Name})");
        }

        return 0;
    }

    internal sealed record InventoryRow(string Table, DataClass Class, long EstimatedRows, long Bytes);

    /// <summary>
    /// Lê as tabelas do banco pelo catálogo do PostgreSQL (nome, linhas estimadas e tamanho): nenhum dado de módulo é lido. Tabela sem classificação aparece como pessoal (a hipótese mais protetora) e o teste de
    /// inventário falha até ela ser classificada.
    /// </summary>
    internal static async Task<IReadOnlyList<InventoryRow>> ReadInventoryAsync(IServiceProvider root, CancellationToken cancellationToken)
    {
        var configuracao = root.GetRequiredService<IConfiguration>();
        var conexao = configuracao.GetConnectionString("Default") ?? configuracao.GetConnectionString("Tenancy")
            ?? throw new InvalidOperationException("Sem string de conexão para o inventário.");

        var linhas = new List<InventoryRow>();
        await using var origem = new NpgsqlConnection(conexao);
        await origem.OpenAsync(cancellationToken);
        await using var comando = new NpgsqlCommand(
            "SELECT schemaname || '.' || relname, n_live_tup, pg_total_relation_size(relid) FROM pg_stat_user_tables ORDER BY 1", origem);
        await using var leitor = await comando.ExecuteReaderAsync(cancellationToken);
        while (await leitor.ReadAsync(cancellationToken))
        {
            var tabela = leitor.GetString(0);
            var classe = RetentionInventory.Tables.TryGetValue(tabela, out var entrada) ? entrada.Class : DataClass.Personal;
            linhas.Add(new InventoryRow(RetentionInventory.Tables.ContainsKey(tabela) ? tabela : tabela + " (NÃO CLASSIFICADA)", classe, leitor.GetInt64(1), leitor.GetInt64(2)));
        }

        return linhas;
    }
}
