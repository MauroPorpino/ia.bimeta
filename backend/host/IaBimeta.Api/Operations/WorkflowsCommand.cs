using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;
using IaBimeta.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Operations;

/// <summary>
/// O comando <c>workflows</c> do host (ADR 0038): a drenagem segura do chatbot visual. <c>workflows status</c> (somente leitura) mostra, por empresa e situação, quantas instâncias de fluxo estão ativas e há quanto tempo a
/// mais antiga espera. <c>workflows drain --confirm [--tenant slug]</c> cancela as instâncias selecionadas, descarta efeitos pendentes e agenda a retomada pelo Agents no outbox.
/// Procedimento completo em <c>docs/operations/CHATBOT-VISUAL.md</c> (§5). Sai com 0, 1 (falha) ou 2 (uso inválido ou drain sem --confirm).
/// </summary>
internal static class WorkflowsCommand
{
    public const string Verb = "workflows";

    internal const string Usage = "Uso: workflows status | workflows drain --confirm [--tenant <slug>]";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is not ("status" or "drain"))
        {
            await output.WriteLineAsync(Usage);
            return 2;
        }

        var drain = args[0] == "drain";
        var confirmado = false;
        string? slug = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--confirm" && drain)
            {
                confirmado = true;
            }
            else if (args[i] == "--tenant" && drain && i + 1 < args.Length)
            {
                slug = args[++i];
            }
            else
            {
                await output.WriteLineAsync($"Argumento desconhecido: {args[i]}");
                await output.WriteLineAsync(Usage);
                return 2;
            }
        }

        if (drain && !confirmado)
        {
            await output.WriteLineAsync("A drenagem só roda com --confirm. Rode antes 'workflows status' e confira o que será encerrado.");
            return 2;
        }

        await using var app = ApiHost.Build(Array.Empty<string>(), builder => OperationsLogging.Quiet(builder.Logging));
        return await ExecuteAsync(app.Services, drain, slug, output);
    }

    internal static async Task<int> ExecuteAsync(IServiceProvider root, bool drain, string? tenantSlug, TextWriter output, CancellationToken cancellationToken = default)
    {
        await using var escopo = root.CreateAsyncScope();
        var operacao = escopo.ServiceProvider.GetRequiredService<FlowDrain>();

        TenantId? tenant = null;
        if (tenantSlug is not null)
        {
            var info = await escopo.ServiceProvider.GetRequiredService<ITenantDirectory>().FindBySlugAsync(tenantSlug, cancellationToken);
            if (info is null)
            {
                await output.WriteLineAsync("Empresa não encontrada.");
                return 1;
            }

            tenant = info.TenantId;
        }

        var linhas = await operacao.Candidates(tenant).GroupBy(i => new { i.TenantId, i.Status }).Select(g => new { g.Key.TenantId, g.Key.Status, Quantidade = g.Count(), Mais = g.Min(i => i.CreatedAt) }).ToListAsync(cancellationToken);
        await output.WriteLineAsync("Instâncias ativas ou pendentes de finalização (somente leitura):");
        if (linhas.Count == 0)
        {
            await output.WriteLineAsync("  nenhuma");
        }

        foreach (var l in linhas.OrderBy(l => l.TenantId.Value).ThenBy(l => l.Status))
        {
            await output.WriteLineAsync($"  empresa {l.TenantId.Value:N} · {l.Status,-15} · {l.Quantidade,5} · a mais antiga de {l.Mais:yyyy-MM-dd HH:mm}Z");
        }

        if (!drain)
        {
            return 0;
        }

        var resultado = await operacao.ExecuteAsync(tenant, cancellationToken);
        if (resultado.BlockedBy is { } impedimento)
        {
            await output.WriteLineAsync($"Drenagem não aplicada: {impedimento}.");
            return 1;
        }

        await output.WriteLineAsync($"Drenagem concluída: {resultado.Instances} instância(s) encerrada(s), {resultado.Decisions} conversa(s) devolvida(s) ao agente, {resultado.Superseded} efeito(s) descartado(s).");
        if (resultado.Unknown > 0)
        {
            await output.WriteLineAsync($"Preservado o histórico de {resultado.Unknown} efeito(s) com resultado incerto. Confira o histórico operacional.");
        }

        return 0;
    }
}
