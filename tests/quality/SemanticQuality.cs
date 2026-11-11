using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IaBimeta.Agents.Domain;
using IaBimeta.Knowledge.Contracts;

namespace IaBimeta.Quality;

// FERRAMENTA DE AVALIACAO, nao armazenamento: este codigo roda so em teste, mede a qualidade da camada semantica contra um conjunto rotulado em pt-BR e recomenda limiares. O armazenamento e a busca de
// producao sao SEMPRE o PostgreSQL com pgvector (ADR 0034). O cosseno em memoria aqui existe so para comparar, no mesmo conjunto, o que o modelo de embeddings real distingue.

public sealed record QualityFaqEntry(string Question, string Answer, string[]? Aliases, bool AutoReply);

public sealed record QualityRetired(string Question, string Answer, string? Note);

public sealed record QualityCase(string Id, string Category, string Text, string Expected, int? Entry, int? ForcedEntry);

public sealed class QualityDataset
{
    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public List<QualityFaqEntry> Faq { get; set; } = [];

    public List<QualityRetired> RetiredEntries { get; set; } = [];

    public List<QualityCase> Cases { get; set; } = [];

    public static QualityDataset Load(string path)
    {
        var dataset = JsonSerializer.Deserialize<QualityDataset>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Conjunto vazio.");
        return dataset;
    }

    /// <summary>O conhecimento PUBLICADO do conjunto (as entradas aposentadas nao entram).</summary>
    public AgentDefinitionContent ToDefinition() =>
        new("Atenda com cortesia.", Faq.Select(f => new FaqEntry(f.Question, f.Answer, f.Aliases, f.AutoReply)).ToList(), null, null, "pt-BR");
}

public sealed record QualityResult(QualityCase Case, SemanticOutcome Outcome, int? AnsweredEntry, double? Score, bool Correct, bool FalsePositive, bool Missed);

public sealed record QualityReport(string Mode, double MinScore, double MinMargin, IReadOnlyList<QualityResult> Results, IReadOnlyList<long> LatenciesMilliseconds)
{
    public int Answered => Results.Count(r => r.Outcome == SemanticOutcome.Answered);

    public int CorrectAnswers => Results.Count(r => r.Outcome == SemanticOutcome.Answered && r.Correct);

    public int FalsePositives => Results.Count(r => r.FalsePositive);

    /// <summary>Casos em que uma resposta direta correta era possivel (expected answer).</summary>
    public int Answerable => Results.Count(r => r.Case.Expected == "answer");

    /// <summary>Respostas diretas corretas entre as respostas dadas (1 quando nao respondeu nada: nao errou).</summary>
    public double Precision => Answered == 0 ? 1 : (double)CorrectAnswers / Answered;

    /// <summary>Quantas das respostas possiveis foram dadas direto (o resto vai ao modelo, que custa mas nao erra por similaridade).</summary>
    public double Coverage => Answerable == 0 ? 0 : (double)Results.Count(r => r.Case.Expected == "answer" && r.Outcome == SemanticOutcome.Answered && r.Correct) / Answerable;

    public long? P95Latency => LatenciesMilliseconds.Count == 0 ? null : LatenciesMilliseconds.OrderBy(x => x).ElementAt(Math.Min(LatenciesMilliseconds.Count - 1, (int)Math.Ceiling(0.95 * LatenciesMilliseconds.Count) - 1));

    public string ToMarkdown()
    {
        var texto = new StringBuilder();
        texto.AppendLine($"## {Mode} - limiar {MinScore:0.00}, margem {MinMargin:0.00}");
        texto.AppendLine();
        texto.AppendLine($"- casos: {Results.Count}; respostas diretas: {Answered}; corretas: {CorrectAnswers}; **falsos positivos: {FalsePositives}**");
        texto.AppendLine($"- precisao: {Precision:P1}; cobertura das respostas possiveis: {Coverage:P1}");
        if (P95Latency is { } p95)
        {
            texto.AppendLine($"- latencia da consulta (vetorizar a pergunta): media {LatenciesMilliseconds.Average():0} ms, p95 {p95} ms");
        }

        texto.AppendLine();
        texto.AppendLine("| categoria | casos | respondeu | correto | falso positivo | perdido |");
        texto.AppendLine("|---|---|---|---|---|---|");
        foreach (var grupo in Results.GroupBy(r => r.Case.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            texto.AppendLine($"| {grupo.Key} | {grupo.Count()} | {grupo.Count(r => r.Outcome == SemanticOutcome.Answered)} | {grupo.Count(r => r.Outcome == SemanticOutcome.Answered && r.Correct)} | {grupo.Count(r => r.FalsePositive)} | {grupo.Count(r => r.Missed)} |");
        }

        var errados = Results.Where(r => r.FalsePositive).ToList();
        if (errados.Count > 0)
        {
            texto.AppendLine();
            texto.AppendLine("**Falsos positivos (resposta direta que nao devia ter saido):**");
            foreach (var r in errados)
            {
                texto.AppendLine($"- {r.Case.Id} [{r.Case.Category}] \"{r.Case.Text}\" -> entrada {r.AnsweredEntry} (similaridade {r.Score:0.000}); esperado: {r.Case.Expected}{(r.Case.Entry is { } e ? $" entrada {e}" : string.Empty)}");
            }
        }

        return texto.ToString();
    }
}

public static class SemanticQualityEvaluator
{
    /// <summary>
    /// O teste DETERMINISTICO das salvaguardas: para cada caso com <c>forcedEntry</c>, a entrada que um motor de similaridade ruim proporia e a similaridade imposta (alta). Prova que a politica e as
    /// salvaguardas seguram o que nao pode responder, sem precisar de modelo de embeddings, e mede quantas respostas legitimas as salvaguardas deixam passar.
    /// </summary>
    public static QualityReport EvaluateForcedCandidates(QualityDataset dataset, double forcedScore = 0.97, double minScore = SemanticAnswerSettings.DefaultMinScore, double minMargin = SemanticAnswerSettings.DefaultMinMargin)
    {
        var definicao = dataset.ToDefinition();
        var resultados = new List<QualityResult>();
        foreach (var c in dataset.Cases.Where(c => c.ForcedEntry is not null))
        {
            var busca = new SemanticSearchResult(
                SemanticSearchOutcome.Found, 1, [new SemanticCandidate(c.ForcedEntry!.Value, SemanticTextKind.Question, 0, forcedScore)], null, 0, 0);
            var decisao = SemanticAnswerPolicy.Decide(busca, definicao, 1, c.Text, minScore, minMargin, answer: true);
            resultados.Add(Judge(c, decisao));
        }

        return new QualityReport("salvaguardas (similaridade imposta)", minScore, minMargin, resultados, []);
    }

    /// <summary>
    /// A avaliacao com o modelo de embeddings REAL: vetoriza os textos aprovados e cada pergunta do conjunto, busca por cosseno (so para esta medicao), aplica a MESMA politica da producao e mede.
    /// </summary>
    public static async Task<QualityReport> EvaluateWithEmbeddingsAsync(
        QualityDataset dataset, IEmbeddingProvider provider, double minScore, double minMargin, IReadOnlyList<(int Entry, SemanticTextKind Kind, int Alias, float[] Vector)>? indexed = null, CancellationToken cancellationToken = default)
    {
        var definicao = dataset.ToDefinition();
        indexed ??= await IndexAsync(definicao, provider, cancellationToken);
        const double piso = 0.35; // o MinCandidateScore padrao do Knowledge (KnowledgeSemanticOptions)

        var resultados = new List<QualityResult>();
        var latencias = new List<long>();
        foreach (var c in dataset.Cases)
        {
            var relogio = Stopwatch.StartNew();
            var vetor = (await provider.EmbedAsync([c.Text], EmbeddingPurpose.Query, cancellationToken))[0];
            latencias.Add(relogio.ElapsedMilliseconds);

            var candidatos = indexed
                .Select(i => new { i.Entry, i.Kind, i.Alias, Score = Dot(vetor, i.Vector) })
                .Where(x => x.Score >= piso)
                .GroupBy(x => x.Entry)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score)
                .Take(5)
                .Select(x => new SemanticCandidate(x.Entry, x.Kind, x.Alias, Math.Round(x.Score, 4)))
                .ToList();
            var busca = candidatos.Count == 0
                ? SemanticSearchResult.Of(SemanticSearchOutcome.NoCandidates)
                : new SemanticSearchResult(SemanticSearchOutcome.Found, 1, candidatos, null, 0, 0);
            resultados.Add(Judge(c, SemanticAnswerPolicy.Decide(busca, definicao, 1, c.Text, minScore, minMargin, answer: true)));
        }

        return new QualityReport("modelo de embeddings real", minScore, minMargin, resultados, latencias);
    }

    public static async Task<IReadOnlyList<(int Entry, SemanticTextKind Kind, int Alias, float[] Vector)>> IndexAsync(AgentDefinitionContent definicao, IEmbeddingProvider provider, CancellationToken cancellationToken = default)
    {
        var textos = new List<(int Entry, SemanticTextKind Kind, int Alias, string Text)>();
        for (var i = 0; i < definicao.Faq.Count; i++)
        {
            textos.Add((i, SemanticTextKind.Question, 0, definicao.Faq[i].Question));
            var aliases = definicao.Faq[i].GetCleanAliases();
            for (var a = 0; a < aliases.Count; a++)
            {
                textos.Add((i, SemanticTextKind.Alias, a + 1, aliases[a]));
            }
        }

        var vetores = await provider.EmbedAsync(textos.Select(t => t.Text).ToList(), EmbeddingPurpose.Document, cancellationToken);
        return textos.Select((t, i) => (t.Entry, t.Kind, t.Alias, vetores[i])).ToList();
    }

    private static double Dot(float[] a, float[] b)
    {
        double soma = 0;
        for (var i = 0; i < a.Length; i++)
        {
            soma += a[i] * b[i];
        }

        return soma; // os vetores do provedor vem normalizados: o produto escalar e o cosseno
    }

    private static QualityResult Judge(QualityCase c, SemanticPolicyDecision decisao)
    {
        var respondeu = decisao.Outcome == SemanticOutcome.Answered;
        var entrada = respondeu ? decisao.EntryOrdinal : null;
        var certo = respondeu && c.Entry is { } esperada && entrada == esperada && (c.Expected == "answer" || c.Expected == "safe");
        var falsoPositivo = respondeu && (c.Expected == "none" || (c.Entry is { } e && entrada != e));
        var perdido = !respondeu && c.Expected == "answer";
        return new QualityResult(c, decisao.Outcome, entrada, decisao.Score, certo, falsoPositivo, perdido);
    }
}
