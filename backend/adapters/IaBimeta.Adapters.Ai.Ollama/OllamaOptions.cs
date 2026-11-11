namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// A configuração do adaptador do Ollama (seção <c>Knowledge:Providers:Ollama</c>, ADR 0034). O serviço pode rodar em OUTRA máquina ou contêiner: <see cref="BaseUrl"/> aponta para ele e, se houver proxy
/// autenticado na frente, <see cref="BearerToken"/> (segredo do ambiente, nunca no repositório) é enviado em cada chamada.
/// </summary>
public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://ollama:11434";

    public string? BearerToken { get; set; }

    public string EmbeddingModelId { get; set; } = "qwen3-embedding:0.6b";

    public int EmbeddingDimension { get; set; } = 1024;

    /// <summary>O modelo generativo local (classificação e sugestões). Vazio = geração local desligada.</summary>
    public string? GenerationModelId { get; set; }

    /// <summary>A instrução que o Qwen3-Embedding espera antes da CONSULTA (os documentos vão sem instrução). Vazio = sem instrução.</summary>
    public string QueryInstruction { get; set; } = "Dada uma pergunta de um cliente, recupere a pergunta frequente que tem a mesma intenção";

    public TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(EmbeddingModelId) || EmbeddingDimension is < 8 or > 4096
            || EmbeddingTimeout <= TimeSpan.Zero || GenerationTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Knowledge:Providers:Ollama é inválido (endereço, modelo, dimensão ou tempos).");
        }
    }
}
