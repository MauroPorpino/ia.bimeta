using IaBimeta.Agents.Domain;
using IaBimeta.Quality;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A suite de qualidade em pt-BR (ADR 0034), parte DETERMINISTICA: sem modelo de embeddings, impoe a cada caso a entrada que um motor de similaridade ruim proporia (com similaridade alta) e prova que a politica e as
/// salvaguardas seguram o que nao pode responder, sem bloquear demais as respostas legitimas. A medicao com o modelo REAL (precisao, falsos positivos, cobertura e latencia) e outra: roda na homologacao.
/// </summary>
public sealed class SemanticQualityDatasetTests
{
    private static readonly string[] CategoriasExigidas =
    [
        "paraphrase", "synonym", "typo", "negation", "number", "multi-question", "ambiguous", "similar-different-answers", "unanswered", "not-auto-reply", "outdated", "manipulation",
    ];

    private static QualityDataset Carregar() => QualityDataset.Load(Path.Combine(AppContext.BaseDirectory, "Quality", "dataset-ptbr.json"));

    [Fact]
    public void O_conjunto_cobre_todas_as_categorias_pedidas_e_e_consistente()
    {
        var d = Carregar();
        Assert.Equal(CategoriasExigidas.Order(), d.Cases.Select(c => c.Category).Distinct().Order().ToArray());
        Assert.Equal(d.Cases.Count, d.Cases.Select(c => c.Id).Distinct().Count());
        Assert.True(d.Cases.Count >= 55);
        Assert.All(d.Cases, c => Assert.Contains(c.Expected, new[] { "answer", "none", "safe" }));
        Assert.All(d.Cases.Where(c => c.Expected is "answer" or "safe"), c => Assert.InRange(c.Entry ?? -1, 0, d.Faq.Count - 1));
        Assert.All(d.Cases.Where(c => c.ForcedEntry is not null), c => Assert.InRange(c.ForcedEntry!.Value, 0, d.Faq.Count - 1));
        d.ToDefinition().Validate(); // o conhecimento do conjunto respeita os limites reais do FAQ
    }

    [Fact]
    public void As_entradas_aposentadas_nao_estao_no_conhecimento_publicado_e_nenhum_caso_as_responde()
    {
        var d = Carregar();
        Assert.NotEmpty(d.RetiredEntries);
        Assert.DoesNotContain(d.Faq, f => d.RetiredEntries.Any(r => r.Question == f.Question));
        Assert.All(d.Cases.Where(c => c.Category == "outdated"), c => Assert.Equal("none", c.Expected));
    }

    [Fact]
    public void Com_similaridade_imposta_alta_as_salvaguardas_nunca_deixam_passar_o_que_nao_pode_responder()
    {
        var relatorio = SemanticQualityEvaluator.EvaluateForcedCandidates(Carregar());

        // O criterio central do ADR 0034: ZERO respostas erradas, mesmo com o motor de similaridade confiante e errado.
        Assert.True(relatorio.FalsePositives == 0, relatorio.ToMarkdown());
        Assert.All(
            relatorio.Results.Where(r => r.Case.Category is "negation" or "number" or "multi-question" or "not-auto-reply"),
            r => Assert.NotEqual(SemanticOutcome.Answered, r.Outcome));
    }

    [Fact]
    public void As_salvaguardas_nao_bloqueiam_as_respostas_legitimas_de_parafrase_sinonimo_e_digitacao()
    {
        var relatorio = SemanticQualityEvaluator.EvaluateForcedCandidates(Carregar());
        var legitimos = relatorio.Results.Where(r => r.Case.Category is "paraphrase" or "synonym" or "typo").ToList();

        Assert.NotEmpty(legitimos);
        Assert.All(legitimos, r => Assert.True(r.Outcome == SemanticOutcome.Answered && r.Correct, $"{r.Case.Id} \"{r.Case.Text}\" foi bloqueado: {r.Outcome}"));
    }

    [Fact]
    public void Texto_de_cliente_que_manipula_o_sistema_nunca_gera_resposta_diferente_do_texto_aprovado()
    {
        var d = Carregar();
        var definicao = d.ToDefinition();
        var relatorio = SemanticQualityEvaluator.EvaluateForcedCandidates(d);
        foreach (var r in relatorio.Results.Where(r => r.Case.Category == "manipulation" && r.Outcome == SemanticOutcome.Answered))
        {
            // Se respondeu, so pode ser a entrada certa, e a resposta e o texto aprovado dela (a politica nunca usa texto do cliente na resposta).
            Assert.Equal(r.Case.Entry, r.AnsweredEntry);
            Assert.Equal(definicao.Faq[r.AnsweredEntry!.Value].Answer, definicao.Faq[r.Case.Entry!.Value].Answer);
        }

        Assert.Equal(0, relatorio.Results.Count(r => r.Case.Category == "manipulation" && r.FalsePositive));
    }

    [Fact]
    public void Similaridade_baixa_nunca_responde_mesmo_com_a_entrada_certa()
    {
        var relatorio = SemanticQualityEvaluator.EvaluateForcedCandidates(Carregar(), forcedScore: 0.80);
        Assert.Equal(0, relatorio.Answered);
        Assert.Equal(1.0, relatorio.Precision);
    }

    [Fact]
    public void O_relatorio_resume_precisao_cobertura_e_falsos_positivos_por_categoria()
    {
        var texto = SemanticQualityEvaluator.EvaluateForcedCandidates(Carregar()).ToMarkdown();
        Assert.Contains("falsos positivos: 0", texto);
        Assert.Contains("| negation |", texto);
        Assert.Contains("precisao: 100,0%", texto.Replace("100.0%", "100,0%"));
    }
}
