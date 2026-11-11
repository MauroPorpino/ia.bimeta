using System.Globalization;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Infrastructure;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Api.Operations;

/// <summary>Os turnos de UM tenant na janela: quantos o FAQ respondeu direto (sem modelo) e quantos o modelo respondeu.</summary>
internal sealed record FaqTurnCounts(Guid TenantId, int FaqHits, int ModelReplies, int SemanticHits = 0);

/// <summary>A média, no ledger do AiGateway, de uma chamada confirmada do tenant na janela: o que uma chamada evitada teria custado, em média.</summary>
internal sealed record ModelCallAverage(Guid TenantId, long Calls, long Tokens, string? Currency, long CostCalls, decimal Cost);

/// <summary>
/// A economia da resposta direta do FAQ (ADR 0029) no diagnóstico operacional, com a DISTINÇÃO que importa: o que é MEDIDO e o que é ESTIMADO. Medido e exato: a contagem de turnos que terminaram com
/// <see cref="AiTurnEndReasons.FaqHit"/> (cada um é uma chamada ao modelo que NÃO foi feita) e a dos que o modelo respondeu. Estimado: os tokens e o custo que essas chamadas teriam consumido, calculados como
/// acertos × a MÉDIA por chamada confirmada do mesmo tenant na janela (ledger do AiGateway; o custo vem da tabela de preços configurada, não da fatura). A pergunta que o FAQ respondeu pode ser mais curta ou mais
/// longa que a média, e por isso o texto rotula a segunda parte como estimativa e mostra a base. Sem chamada confirmada na janela não há média, e o texto diz que não há base, sem inventar número.
/// A leitura fica no host (a raiz de composição) para que nenhum módulo leia as tabelas do outro: só os DbContexts públicos de cada módulo, somente leitura, sem texto de conversa.
/// </summary>
internal static class FaqSavingsReport
{
    public const string Section = "FAQ direto";

    /// <summary>A janela de contagem. Fixa no MVP: o relatório é um retrato dos últimos dias, não uma série.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Lê as contagens por tenant e as médias do ledger. <paramref name="onlyTenant"/> restringe a uma empresa (e então ela aparece mesmo sem acertos).</summary>
    public static async Task<(IReadOnlyList<FaqTurnCounts> Counts, IReadOnlyList<ModelCallAverage> Averages)> LoadAsync(
        IServiceProvider root, DateTimeOffset now, Guid? onlyTenant, CancellationToken cancellationToken)
    {
        var desde = now - Window;
        await using var escopo = root.CreateAsyncScope();

        var agentes = escopo.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var turnos = agentes.Turns.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.State == AiTurnState.Completed && t.EndedAt >= desde && (t.EndReason == AiTurnEndReasons.FaqHit || t.EndReason == AiTurnEndReasons.SemanticHit || t.EndReason == AiTurnEndReasons.Replied));
        if (onlyTenant is { } uma)
        {
            var tenant = new TenantId(uma);
            turnos = turnos.Where(t => t.TenantId == tenant);
        }

        var contagens = (await turnos
                .GroupBy(t => t.TenantId)
                .Select(g => new
                {
                    Tenant = g.Key,
                    Hits = g.Count(t => t.EndReason == AiTurnEndReasons.FaqHit),
                    Replies = g.Count(t => t.EndReason == AiTurnEndReasons.Replied),
                    Semantic = g.Count(t => t.EndReason == AiTurnEndReasons.SemanticHit),
                })
                .ToListAsync(cancellationToken))
            .Select(c => new FaqTurnCounts(c.Tenant.Value, c.Hits, c.Replies, c.Semantic))
            .ToList();

        var gateway = escopo.ServiceProvider.GetRequiredService<AiGatewayDbContext>();
        var chamadas = gateway.Requests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.State == AiRequestState.Confirmed && r.Outcome == AiRequestOutcome.Succeeded && r.CompletedAt >= desde);
        if (onlyTenant is { } outra)
        {
            var tenant = new TenantId(outra);
            chamadas = chamadas.Where(r => r.TenantId == tenant);
        }

        // A agregação é no banco: uma linha por tenant, e o texto só usa as dos tenants com acerto.
        var tokens = await chamadas
            .GroupBy(r => r.TenantId)
            .Select(g => new { Tenant = g.Key, Calls = g.Count(), Tokens = g.Sum(r => (long)r.InputTokens + r.OutputTokens) })
            .ToListAsync(cancellationToken);

        var custos = await chamadas
            .Where(r => r.CostCurrency != null)
            .GroupBy(r => new { r.TenantId, r.CostCurrency })
            .Select(g => new { g.Key.TenantId, g.Key.CostCurrency, Calls = g.Count(), Cost = g.Sum(r => r.EstimatedCost) })
            .ToListAsync(cancellationToken);

        var medias = new List<ModelCallAverage>();
        foreach (var t in tokens)
        {
            // Com mais de uma moeda no período (troca de modelo ou de tabela), vale a que mais chamadas tem; a outra só some do custo, nunca é somada à primeira.
            var moeda = custos.Where(c => c.TenantId == t.Tenant).OrderByDescending(c => c.Calls).FirstOrDefault();
            medias.Add(new ModelCallAverage(t.Tenant.Value, t.Calls, t.Tokens, moeda?.CostCurrency, moeda?.Calls ?? 0, moeda?.Cost ?? 0m));
        }

        return (contagens, medias);
    }

    /// <summary>As lacunas do FAQ ABERTAS (esperando a revisão do administrador, ADR 0030), por tenant. Só lê a situação e conta: nenhum texto, nenhuma referência.</summary>
    public static async Task<IReadOnlyDictionary<Guid, int>> LoadOpenGapsAsync(IServiceProvider root, Guid? onlyTenant, CancellationToken cancellationToken)
    {
        await using var escopo = root.CreateAsyncScope();
        var agentes = escopo.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var lacunas = agentes.KnowledgeGaps.IgnoreQueryFilters().AsNoTracking().Where(g => g.Status == KnowledgeGapStatus.Open);
        if (onlyTenant is { } uma)
        {
            var tenant = new TenantId(uma);
            lacunas = lacunas.Where(g => g.TenantId == tenant);
        }

        var linhas = await lacunas.GroupBy(g => g.TenantId).Select(g => new { Tenant = g.Key, Abertas = g.Count() }).ToListAsync(cancellationToken);
        return linhas.ToDictionary(l => l.Tenant.Value, l => l.Abertas);
    }

    /// <summary>Uma linha por empresa com lacuna aberta: o que há para revisar. Informativa (nunca atenção): é trabalho do administrador, não defeito do ambiente.</summary>
    public static IReadOnlyList<(Guid? Tenant, string Message)> DescribeOpenGaps(IReadOnlyDictionary<Guid, int> open) =>
        open.Where(p => p.Value > 0).OrderBy(p => p.Key)
            .Select(p => ((Guid?)p.Key, $"{p.Value.ToString("N0", Pt)} lacuna(s) do FAQ aberta(s) esperando revisão do administrador (perguntas de clientes que o FAQ não respondeu direto: GET /agents/knowledge-gaps ou a área Conhecimento da interface)."))
            .ToList();

    /// <summary>O texto do relatório: duas linhas por empresa, a MEDIDA e a ESTIMADA. Puro (sem banco), para o teste provar os números e os rótulos.</summary>
    public static IReadOnlyList<(Guid? Tenant, string Message)> Describe(IReadOnlyList<FaqTurnCounts> counts, IReadOnlyList<ModelCallAverage> averages, IReadOnlyList<Guid> mandatory)
    {
        var linhas = new List<(Guid?, string)>();
        var janela = HealthText.Age(Window);
        var tenants = counts.Where(c => c.FaqHits > 0 || c.SemanticHits > 0).Select(c => c.TenantId).Union(mandatory).Distinct().ToList();

        foreach (var tenant in tenants)
        {
            var c = counts.FirstOrDefault(x => x.TenantId == tenant) ?? new FaqTurnCounts(tenant, 0, 0);
            var respondidos = c.FaqHits + c.ModelReplies;
            var taxa = respondidos == 0 ? 0 : 100.0 * c.FaqHits / respondidos;
            linhas.Add((tenant,
                $"nos últimos {janela}: {c.FaqHits.ToString("N0", Pt)} turno(s) respondido(s) direto pelo FAQ, SEM chamar o modelo (chamadas evitadas; contagem exata); " +
                $"o modelo respondeu {c.ModelReplies.ToString("N0", Pt)} turno(s); {taxa.ToString("F1", Pt)}% dos turnos respondidos pela IA saíram do FAQ."));

            // ADR 0034: o turno respondido direto pela BUSCA SEMÂNTICA também é uma chamada ao modelo que não foi feita (contagem exata, rótulo próprio: não é o FAQ determinístico).
            if (c.SemanticHits > 0)
            {
                var comSemantica = c.FaqHits + c.SemanticHits + c.ModelReplies;
                linhas.Add((tenant,
                    $"nos últimos {janela}: {c.SemanticHits.ToString("N0", Pt)} turno(s) respondido(s) direto pela BUSCA SEMÂNTICA no conhecimento aprovado, SEM chamar o modelo (chamadas evitadas; contagem exata); " +
                    $"{(100.0 * c.SemanticHits / comSemantica).ToString("F1", Pt)}% dos turnos respondidos pela IA."));
            }

            var evitadas = c.FaqHits + c.SemanticHits;
            if (evitadas == 0)
            {
                continue;
            }

            var media =averages.FirstOrDefault(m => m.TenantId == tenant);
            if (media is null || media.Calls == 0)
            {
                linhas.Add((tenant, "estimativa de tokens e custo: sem chamada confirmada ao modelo deste tenant na janela, não há média para estimar (nenhum número foi inventado)."));
                continue;
            }

            var mediaDeTokens = (double)media.Tokens / media.Calls;
            var tokensEvitados = evitadas * mediaDeTokens;
            var custo = media.Currency is not null && media.CostCalls > 0
                ? $" e ~{(evitadas * media.Cost / media.CostCalls).ToString("0.0000", Pt)} {media.Currency} (custo estimado pela tabela de preços configurada, não é fatura)"
                : " (sem custo estimado: a tabela de preços não informou custo para as chamadas da janela)";
            var origem = c.SemanticHits > 0 ? $" (FAQ {c.FaqHits.ToString("N0", Pt)} + busca semântica {c.SemanticHits.ToString("N0", Pt)}; o custo da IA local é infraestrutura própria e NÃO está descontado)" : string.Empty;
            linhas.Add((tenant,
                $"ESTIMATIVA (não medida): ~{tokensEvitados.ToString("N0", Pt)} tokens{custo} que as {evitadas.ToString("N0", Pt)} chamada(s) evitada(s){origem} teriam consumido = acertos × a média de " +
                $"{mediaDeTokens.ToString("N0", Pt)} tokens por chamada confirmada deste tenant ({media.Calls.ToString("N0", Pt)} chamada(s) na janela). A pergunta do FAQ pode ser mais curta ou mais longa que a média."));
        }

        return linhas;
    }
}
