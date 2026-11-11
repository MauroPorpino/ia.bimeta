using System.Globalization;
using System.Text;
using IaBimeta.Adapters.Ai.Ollama;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.Quality;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Knowledge;

/// <summary>
/// Marca um teste que EXIGE um servico Ollama REAL com o modelo de embeddings (ADR 0034). Ignorado quando <c>IABIMETA_TEST_OLLAMA_URL</c> nao esta definida (o Windows de desenvolvimento nao tem Ollama). Variaveis:
/// <c>IABIMETA_TEST_OLLAMA_URL</c> (obrigatoria), <c>IABIMETA_TEST_OLLAMA_TOKEN</c> (se o proxy exige), <c>IABIMETA_TEST_OLLAMA_EMBEDDING_MODEL</c> (padrao qwen3-embedding:0.6b) e
/// <c>IABIMETA_TEST_REPORT_DIR</c> (onde gravar o relatorio). O modelo precisa ter sido baixado antes (<c>deploy/scripts/ia-local.sh pull</c>).
/// </summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    public const string UrlVariable = "IABIMETA_TEST_OLLAMA_URL";

    public OllamaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable)))
        {
            Skip = $"Exige o Ollama real: defina {UrlVariable}. Pendente para a homologacao na VM.";
        }
    }
}

/// <summary>
/// A suite de qualidade em pt-BR contra o modelo de embeddings REAL (ADR 0034): mede precisao, falsos positivos, cobertura e latencia no conjunto rotulado, varre os limiares e grava um relatorio. O criterio
/// que bloqueia a ativacao do modo semantico em empresa real e ZERO falsos positivos no limiar padrao. Cobertura e latencia sao INFORMATIVAS para calibrar; nenhum limiar e aceito sem este relatorio.
/// </summary>
public sealed class QualidadeSemanticaOllamaTests
{
    private static IEmbeddingProvider CriarProvedor()
    {
        var servicos = new ServiceCollection();
        servicos.AddOllamaProvider(o =>
        {
            o.BaseUrl = Environment.GetEnvironmentVariable(OllamaFactAttribute.UrlVariable)!;
            o.BearerToken = Environment.GetEnvironmentVariable("IABIMETA_TEST_OLLAMA_TOKEN");
            o.EmbeddingModelId = Environment.GetEnvironmentVariable("IABIMETA_TEST_OLLAMA_EMBEDDING_MODEL") ?? o.EmbeddingModelId;
            o.EmbeddingTimeout = TimeSpan.FromSeconds(60);
        });
        return servicos.BuildServiceProvider().GetRequiredService<IEmbeddingProvider>();
    }

    private static QualityDataset Carregar() => QualityDataset.Load(Path.Combine(AppContext.BaseDirectory, "Quality", "dataset-ptbr.json"));

    [OllamaFact]
    public async Task O_modelo_devolve_vetores_normalizados_na_dimensao_1024_e_de_forma_repetivel()
    {
        var provedor = CriarProvedor();
        var a = (await provedor.EmbedAsync(["Preciso de jejum para o exame de glicose?"], EmbeddingPurpose.Document))[0];
        var b = (await provedor.EmbedAsync(["Preciso de jejum para o exame de glicose?"], EmbeddingPurpose.Document))[0];

        Assert.Equal(1024, a.Length); // a coluna do indice e vector(1024)
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => (double)x * x)), 3);
        var cosseno = a.Zip(b, (x, y) => (double)x * y).Sum();
        Assert.True(cosseno > 0.999, $"O mesmo texto deve dar o mesmo vetor (cosseno {cosseno:0.0000}).");
    }

    [OllamaFact]
    public async Task Precisao_no_limiar_padrao_tem_zero_falsos_positivos_e_o_relatorio_recomenda_o_limiar()
    {
        var dataset = Carregar();
        var provedor = CriarProvedor();
        var indice = await SemanticQualityEvaluator.IndexAsync(dataset.ToDefinition(), provedor);

        var relatorios = new List<QualityReport>();
        for (var limiar = 0.80; limiar <= 0.981; limiar += 0.02)
        {
            relatorios.Add(await SemanticQualityEvaluator.EvaluateWithEmbeddingsAsync(dataset, provedor, Math.Round(limiar, 2), 0.05, indice));
        }

        var padrao = relatorios.Single(r => Math.Abs(r.MinScore - 0.90) < 0.001);
        var recomendado = relatorios.Where(r => r.FalsePositives == 0).OrderBy(r => r.MinScore).FirstOrDefault();
        var saida = new StringBuilder();
        saida.AppendLine(CultureInfo.InvariantCulture, $"# Qualidade semantica em pt-BR ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC)");
        saida.AppendLine();
        saida.AppendLine($"Modelo: {Environment.GetEnvironmentVariable("IABIMETA_TEST_OLLAMA_EMBEDDING_MODEL") ?? "qwen3-embedding:0.6b"}. Conjunto: {dataset.Name} v{dataset.Version} ({dataset.Cases.Count} casos).");
        saida.AppendLine(recomendado is null
            ? "**Nenhum limiar entre 0,80 e 0,98 tem zero falsos positivos: NAO ativar o modo semantico com este modelo.**"
            : $"**Menor limiar com zero falsos positivos: {recomendado.MinScore:0.00}** (cobertura {recomendado.Coverage:P1}). Use um valor igual ou acima dele.");
        saida.AppendLine();
        foreach (var r in relatorios)
        {
            saida.AppendLine(r.ToMarkdown());
        }

        var pasta = Environment.GetEnvironmentVariable("IABIMETA_TEST_REPORT_DIR") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(pasta);
        File.WriteAllText(Path.Combine(pasta, "qualidade-semantica-ptbr.md"), saida.ToString());

        Assert.True(padrao.FalsePositives == 0, padrao.ToMarkdown());
        Assert.Equal(1.0, padrao.Precision);
    }

    [OllamaFact]
    public async Task A_latencia_da_consulta_em_CPU_fica_dentro_do_orcamento_da_resposta_direta()
    {
        var dataset = Carregar();
        var provedor = CriarProvedor();
        await provedor.EmbedAsync(["aquecimento"], EmbeddingPurpose.Query); // carrega o modelo
        var relatorio = await SemanticQualityEvaluator.EvaluateWithEmbeddingsAsync(dataset, provedor, 0.90, 0.05);

        Assert.NotNull(relatorio.P95Latency);
        // Orcamento generoso para CPU sem GPU (V1): a resposta direta so compensa se for bem mais rapida que o modelo comercial. O valor medido vai no relatorio.
        Assert.True(relatorio.P95Latency < 5000, $"p95 da consulta: {relatorio.P95Latency} ms");
    }
}
