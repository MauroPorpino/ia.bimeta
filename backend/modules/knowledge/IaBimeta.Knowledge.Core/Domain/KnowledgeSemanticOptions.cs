namespace IaBimeta.Knowledge.Domain;

/// <summary>
/// A configuração do índice semântico (seção <c>Knowledge:Semantic</c>, ADR 0034). <b>Desligado por padrão</b>: é o interruptor GLOBAL (kill switch); desligado, nada é consultado nem vetorizado e o atendimento segue só com a
/// FAQ determinística e o modelo comercial. O modelo e a dimensão fazem parte do índice: trocar um deles deixa os vetores antigos fora das buscas até a nova vetorização (nunca se misturam modelos).
/// </summary>
public sealed class KnowledgeSemanticOptions
{
    public const string DefaultModelId = "qwen3-embedding:0.6b";

    public bool Enabled { get; set; }

    /// <summary>O identificador do modelo de embeddings (do executor local).</summary>
    public string ModelId { get; set; } = DefaultModelId;

    /// <summary>A dimensão do vetor. É a da coluna do banco (<c>vector(1024)</c>): mudar exige uma migration nova.</summary>
    public int Dimension { get; set; } = 1024;

    /// <summary>Quantos textos vão ao serviço de embeddings por chamada.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>De quanto em quanto tempo o Worker procura textos pendentes de vetorização.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxAttempts { get; set; } = 5;

    /// <summary>A espera depois da primeira falha; dobra a cada nova falha, até <see cref="MaxRetryBackoff"/>.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Por quanto tempo um lote reservado pertence a um Worker (se ele cair, outro retoma).</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>O maior texto (pergunta, alias ou consulta) enviado ao modelo; acima disso é cortado só para a consulta e recusado na indexação.</summary>
    public int MaxTextLength { get; set; } = 500;

    /// <summary>O piso de similaridade para um texto virar CANDIDATO (não é o limite de resposta direta, que é do tenant e é bem mais alto).</summary>
    public double MinCandidateScore { get; set; } = 0.35;

    public int MaxSearchLimit { get; set; } = 10;

    /// <exception cref="InvalidOperationException">Algum valor é inválido.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 100 || Dimension is < 8 or > 4096 || BatchSize is < 1 or > 128 || ReconcileInterval < TimeSpan.FromMilliseconds(200)
            || MaxAttempts is < 1 or > 50 || RetryBackoff <= TimeSpan.Zero || MaxRetryBackoff < RetryBackoff || LeaseDuration < TimeSpan.FromSeconds(10)
            || MaxTextLength is < 50 or > 4000 || MinCandidateScore is < 0 or > 1 || MaxSearchLimit is < 1 or > 50)
        {
            throw new InvalidOperationException("Knowledge:Semantic é inválido (modelo, dimensão, lote, intervalos, tentativas, tamanhos ou piso de similaridade).");
        }
    }
}
