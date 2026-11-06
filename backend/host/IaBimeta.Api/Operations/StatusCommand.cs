using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Operations;

/// <summary>
/// O comando <c>status</c> do host: <c>dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- status [--tenant &lt;empresa&gt;] [--api-url &lt;url&gt;] [--skip api,worker]</c> (o script
/// <c>tools/ops/Status-Ambiente.ps1</c> o chama e acrescenta o que é do sistema operacional: backup, disco, tarefas agendadas). Mostra o estado geral do ambiente (PostgreSQL, migrations, Api, Worker, filas e contas) e, para
/// cada falha, o que fazer. Somente leitura, sem rota HTTP. Sai com 0 (tudo certo), 1 (há atenção) ou 2 (há falha que precisa de intervenção); 64 é uso inválido.
/// </summary>
internal static class StatusCommand
{
    public const string Verb = "status";

    internal const string Usage = "Uso: status [--tenant <empresa>] [--api-url <url>] [--skip api,worker]";

    public const int UsageExitCode = 64;

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? tenant = null;
        string? apiUrl = null;
        var verificarApi = true;
        var verificarWorker = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tenant" when i + 1 < args.Length:
                    tenant = args[++i];
                    break;
                case "--api-url" when i + 1 < args.Length:
                    apiUrl = args[++i];
                    break;
                case "--skip" when i + 1 < args.Length:
                    foreach (var parte in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        switch (parte.ToLowerInvariant())
                        {
                            case "api":
                                verificarApi = false;
                                break;
                            case "worker":
                                verificarWorker = false;
                                break;
                            default:
                                await output.WriteLineAsync($"--skip aceita api e worker: '{parte}' não existe.");
                                return UsageExitCode;
                        }
                    }

                    break;
                default:
                    await output.WriteLineAsync($"Argumento desconhecido: {args[i]}");
                    await output.WriteLineAsync(Usage);
                    return UsageExitCode;
            }
        }

        // A mesma composição do Api, sem abrir porta nem iniciar serviço algum, com só avisos e erros no console.
        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        apiUrl ??= app.Configuration["Health:ApiUrl"];
        return await ExecuteAsync(app.Services, new StatusOptions(tenant, apiUrl, verificarApi, verificarWorker), output);
    }

    internal static async Task<int> ExecuteAsync(IServiceProvider root, StatusOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var relatorio = await StatusCollector.CollectAsync(root, options, cancellationToken);
        await PrintAsync(relatorio, options, output);
        return (int)relatorio.Overall;
    }

    internal static async Task PrintAsync(StatusReport relatorio, StatusOptions options, TextWriter output)
    {
        await output.WriteLineAsync($"ia.bimeta - estado do ambiente ({relatorio.At:yyyy-MM-dd HH:mm:ss} UTC){(options.TenantSlug is null ? string.Empty : $", empresa '{options.TenantSlug}'")}");

        foreach (var secao in relatorio.Lines.GroupBy(l => l.Section, StringComparer.Ordinal))
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync(secao.Key.ToUpperInvariant());
            foreach (var linha in secao)
            {
                var marca = linha.Severity switch
                {
                    HealthSeverity.Critical => "[CRITICO]",
                    HealthSeverity.Warning => "[ATENCAO]",
                    _ => "[OK]     ",
                };
                var empresa = linha.Tenant is null ? string.Empty : $"(empresa {linha.Tenant}) ";
                await output.WriteLineAsync($"  {marca} {empresa}{linha.Message}");
                if (linha.Action is not null && linha.Severity != HealthSeverity.Ok)
                {
                    await output.WriteLineAsync($"            -> {linha.Action}");
                }
            }
        }

        var criticas = relatorio.Lines.Count(l => l.Severity == HealthSeverity.Critical);
        var atencoes = relatorio.Lines.Count(l => l.Severity == HealthSeverity.Warning);
        await output.WriteLineAsync();
        await output.WriteLineAsync(criticas > 0
            ? $"RESULTADO: {criticas} falha(s) que precisam de intervenção e {atencoes} ponto(s) de atenção."
            : atencoes > 0
                ? $"RESULTADO: sem falha crítica; {atencoes} ponto(s) de atenção."
                : "RESULTADO: tudo em ordem.");
    }
}
