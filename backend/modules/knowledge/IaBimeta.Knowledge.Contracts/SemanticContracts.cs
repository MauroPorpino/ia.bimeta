namespace IaBimeta.Knowledge.Contracts;

// O Knowledge Engine semântico (ADR 0034): independente de canal (texto, voz, integrações) e de fornecedor de IA. Estes são os contratos que os outros módulos (Agents) e os adaptadores usam.

/// <summary>De onde saiu o texto indexado: a pergunta da entrada ou um dos aliases dela.</summary>
public enum SemanticTextKind : short
{
    Question = 1,
    Alias = 2,
}

/// <summary>Um texto de conhecimento APROVADO a indexar (pergunta ou alias de uma entrada publicada). Nunca texto de cliente.</summary>
/// <param name="EntryOrdinal">A posição da entrada no conhecimento publicado (a mesma que quem consulta usa para achar a resposta aprovada).</param>
/// <param name="AliasOrdinal">A posição do alias (0 para a pergunta).</param>
/// <param name="AutoReplyEligible">A entrada está autorizada para resposta automática (só as elegíveis entram na busca de resposta direta).</param>
public sealed record SemanticSourceText(int EntryOrdinal, SemanticTextKind Kind, int AliasOrdinal, string Text, bool AutoReplyEligible);

/// <summary>O conhecimento publicado de um tenant, como a fonte da indexação. <paramref name="DefinitionNumber"/> identifica a versão publicada: uma busca só vale contra o índice da versão que está publicada.</summary>
public sealed record SemanticSource(int DefinitionNumber, string Locale, IReadOnlyList<SemanticSourceText> Texts);

public sealed record SemanticSyncResult(int Texts, int Unchanged, int Pending, int Removed);

/// <summary>O estado do índice do tenant para o modelo vigente.</summary>
public sealed record SemanticIndexStatus(int? DefinitionNumber, string ModelId, int Dimension, int Ready, int Pending, int Failed, int OtherModel);

/// <summary>A atualização do índice semântico a partir do conhecimento publicado. Idempotente; nunca chama o modelo no caminho da publicação (a vetorização é assíncrona e recuperável).</summary>
public interface IKnowledgeIndexing
{
    /// <summary>Torna o índice do tenant do contexto igual ao conhecimento publicado informado (substitui o anterior): o que não mudou é reaproveitado, o novo fica pendente de vetorização, o que saiu é removido.</summary>
    Task<SemanticSyncResult> SyncAsync(SemanticSource source, CancellationToken cancellationToken = default);

    /// <summary>Remove todo o índice do tenant do contexto (conhecimento despublicado).</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public enum SemanticSearchOutcome
{
    /// <summary>Há candidatos, ordenados do mais parecido para o menos.</summary>
    Found = 1,

    /// <summary>O índice está pronto e nada passou do piso de similaridade.</summary>
    NoCandidates = 2,

    /// <summary>A busca semântica está desligada (global ou do tenant): nada foi consultado.</summary>
    Disabled = 3,

    /// <summary>O serviço de vetores falhou ou o índice não está pronto: quem chama segue o fluxo normal (nunca interrompe o atendimento).</summary>
    Unavailable = 4,
}

/// <param name="Locale">O idioma do conhecimento: só entram candidatos desse idioma.</param>
/// <param name="OnlyAutoReplyEligible">Só entradas autorizadas para resposta automática.</param>
public sealed record SemanticSearchRequest(string Query, string Locale, bool OnlyAutoReplyEligible, int Limit = 5);

public sealed record SemanticCandidate(int EntryOrdinal, SemanticTextKind Kind, int AliasOrdinal, double Score);

/// <param name="DefinitionNumber">A versão do conhecimento a que o índice consultado corresponde.</param>
/// <param name="FailureCode">Código curto e fixo quando <see cref="SemanticSearchOutcome.Unavailable"/> (nunca texto do provedor nem da pergunta).</param>
public sealed record SemanticSearchResult(
    SemanticSearchOutcome Outcome, int? DefinitionNumber, IReadOnlyList<SemanticCandidate> Candidates, string? FailureCode, long EmbeddingMilliseconds, long SearchMilliseconds)
{
    public static SemanticSearchResult Of(SemanticSearchOutcome outcome, string? failureCode = null) => new(outcome, null, Array.Empty<SemanticCandidate>(), failureCode, 0, 0);
}

/// <summary>A busca semântica no conhecimento do tenant do contexto (e SÓ dele).</summary>
public interface IKnowledgeSearch
{
    Task<SemanticSearchResult> SearchAsync(SemanticSearchRequest request, CancellationToken cancellationToken = default);
}

// Provedores locais (desacoplados de qualquer executor: Ollama é só o primeiro adaptador).

public enum EmbeddingPurpose
{
    /// <summary>Um texto de conhecimento a indexar.</summary>
    Document = 1,

    /// <summary>A pergunta de quem consulta (alguns modelos pedem uma instrução própria para consulta).</summary>
    Query = 2,
}

/// <summary>O modelo de embeddings vigente: o identificador e a dimensão do vetor (parte do índice: trocar um deles invalida os vetores antigos).</summary>
public sealed record EmbeddingModelInfo(string ModelId, int Dimension);

/// <summary>O serviço de embeddings falhou (fora do ar, tempo esgotado, resposta inválida). A mensagem nunca traz texto indexado nem credencial.</summary>
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>Código curto e fixo (por exemplo, <c>timeout</c>, <c>unreachable</c>, <c>invalid-response</c>, <c>dimension-mismatch</c>).</summary>
    public string Code { get; }
}

public interface IEmbeddingProvider
{
    EmbeddingModelInfo Model { get; }

    /// <summary>Um vetor por texto, na ordem, todos com <c>Model.Dimension</c> posições e normalizados.</summary>
    /// <exception cref="EmbeddingUnavailableException">O serviço falhou.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default);
}

/// <summary>Um pedido de geração local com saída estruturada (JSON). O texto enviado já passou pela minimização de dados de quem pede.</summary>
public sealed record LocalGenerationRequest(string SystemInstruction, string UserContent, string JsonSchema, int MaxOutputTokens = 800);

public sealed record LocalGenerationResult(string Json, long Milliseconds);

public sealed class LocalGenerationUnavailableException : Exception
{
    public LocalGenerationUnavailableException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>A geração local (classificação, agrupamento e sugestões). Só PROPÕE: nada gerado aqui é publicado sem revisão humana.</summary>
public interface ILocalGenerationProvider
{
    string ModelId { get; }

    /// <exception cref="LocalGenerationUnavailableException">O serviço falhou ou a saída não é JSON.</exception>
    Task<LocalGenerationResult> GenerateJsonAsync(LocalGenerationRequest request, CancellationToken cancellationToken = default);
}
