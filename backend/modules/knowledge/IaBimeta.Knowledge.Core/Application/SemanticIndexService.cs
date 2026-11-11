using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.Knowledge.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Knowledge.Application;

/// <summary>
/// O índice semântico do conhecimento APROVADO de cada tenant (ADR 0034). A publicação só grava o que deve ser indexado (rápido e transacional); a vetorização acontece depois, no Worker (<see cref="SemanticIndexer"/>), com
/// repetição e recuperação. A busca vê só o tenant do contexto, só o idioma e só os textos já vetorizados pelo MODELO vigente. Qualquer falha do serviço de vetores vira "indisponível": nunca exceção para quem atende.
/// Só texto de conhecimento aprovado é indexado: nunca texto de cliente, e a consulta do cliente é vetorizada em memória e descartada.
/// </summary>
public sealed class SemanticIndexService : IKnowledgeIndexing, IKnowledgeSearch
{
    private readonly IRequestContextAccessor _context;
    private readonly ISemanticStore _store;
    private readonly IEmbeddingProvider _embeddings;
    private readonly KnowledgeSemanticOptions _options;

    public SemanticIndexService(IRequestContextAccessor context, ISemanticStore store, IEmbeddingProvider embeddings, KnowledgeSemanticOptions options)
    {
        _context = context;
        _store = store;
        _embeddings = embeddings;
        _options = options;
    }

    /// <summary>O resumo do texto no modelo vigente: igual só se o texto, o modelo e a dimensão forem iguais.</summary>
    public static string HashOf(string text, string modelId, int dimension) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{modelId}|{dimension}|{text}"))).ToLowerInvariant();

    public async Task<SemanticSyncResult> SyncAsync(SemanticSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_options.Enabled)
        {
            // Modo semântico desligado: a tabela pode nem existir (a migration só é aplicada quando ligado). Publicar o conhecimento não depende do índice.
            return new SemanticSyncResult(0, 0, 0, 0);
        }

        var tenant = _context.GetRequired().TenantId;
        if (!Locale.TryParse(source.Locale, out var idioma))
        {
            throw new ArgumentException("O idioma do conhecimento é inválido.", nameof(source));
        }

        // Só textos válidos: vazios e longos demais não entram (o conhecimento já os recusa; aqui é a defesa do índice).
        var textos = source.Texts
            .Where(t => !string.IsNullOrWhiteSpace(t.Text) && t.Text.Length <= _options.MaxTextLength)
            .Select(t => t with { Text = t.Text.Trim() })
            .ToList();
        var limpa = new SemanticSource(source.DefinitionNumber, idioma.ToString(), textos);
        return await _store.ReplaceAsync(tenant, limpa, _options.ModelId, _options.Dimension, texto => HashOf(texto, _options.ModelId, _options.Dimension), cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _options.Enabled ? _store.DeleteAllAsync(_context.GetRequired().TenantId, cancellationToken) : Task.CompletedTask;

    public Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _options.Enabled
            ? _store.StatusAsync(_context.GetRequired().TenantId, _options.ModelId, _options.Dimension, cancellationToken)
            : Task.FromResult(new SemanticIndexStatus(null, _options.ModelId, _options.Dimension, 0, 0, 0, 0));

    public async Task<SemanticSearchResult> SearchAsync(SemanticSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.Enabled)
        {
            return SemanticSearchResult.Of(SemanticSearchOutcome.Disabled);
        }

        var tenant = _context.GetRequired().TenantId;
        if (string.IsNullOrWhiteSpace(request.Query) || !Locale.TryParse(request.Locale, out var idioma))
        {
            return SemanticSearchResult.Of(SemanticSearchOutcome.NoCandidates);
        }

        var consulta = request.Query.Trim();
        if (consulta.Length > _options.MaxTextLength)
        {
            consulta = consulta[.._options.MaxTextLength];
        }

        var relogio = Stopwatch.StartNew();
        float[] vetor;
        try
        {
            var vetores = await _embeddings.EmbedAsync([consulta], EmbeddingPurpose.Query, cancellationToken);
            vetor = vetores.Count == 1 && vetores[0].Length == _options.Dimension ? vetores[0] : throw new EmbeddingUnavailableException("dimension-mismatch", "O vetor da consulta não tem a dimensão do índice.");
        }
        catch (EmbeddingUnavailableException ex)
        {
            return SemanticSearchResult.Of(SemanticSearchOutcome.Unavailable, ex.Code);
        }

        var vetorizacaoMs = relogio.ElapsedMilliseconds;
        relogio.Restart();
        IReadOnlyList<SemanticHit> acertos;
        try
        {
            var limite = Math.Clamp(request.Limit, 1, _options.MaxSearchLimit);

            // Busca mais textos que entradas: uma entrada tem vários textos (pergunta e aliases) e só o melhor de cada uma conta.
            acertos = await _store.SearchAsync(tenant, vetor, idioma.ToString(), request.OnlyAutoReplyEligible, _options.ModelId, _options.Dimension, limite * 4, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SemanticSearchResult.Of(SemanticSearchOutcome.Unavailable, "store-failed");
        }

        var buscaMs = relogio.ElapsedMilliseconds;
        if (acertos.Count == 0)
        {
            // Sem nenhum texto vetorizado do modelo vigente o índice não está pronto (ou está vazio): quem chama segue o fluxo normal.
            var estado = await SafeStatusAsync(tenant, cancellationToken);
            return estado is { Ready: > 0 }
                ? new SemanticSearchResult(SemanticSearchOutcome.NoCandidates, estado.DefinitionNumber, [], null, vetorizacaoMs, buscaMs)
                : new SemanticSearchResult(SemanticSearchOutcome.Unavailable, estado?.DefinitionNumber, [], "index-not-ready", vetorizacaoMs, buscaMs);
        }

        var versao = acertos[0].DefinitionNumber;
        var candidatos = acertos
            .Where(a => a.DefinitionNumber == versao && a.Score >= _options.MinCandidateScore)
            .GroupBy(a => a.EntryOrdinal)
            .Select(g => g.OrderByDescending(a => a.Score).First())
            .OrderByDescending(a => a.Score)
            .Take(Math.Clamp(request.Limit, 1, _options.MaxSearchLimit))
            .Select(a => new SemanticCandidate(a.EntryOrdinal, a.Kind, a.AliasOrdinal, Math.Round(a.Score, 4)))
            .ToList();
        return new SemanticSearchResult(candidatos.Count == 0 ? SemanticSearchOutcome.NoCandidates : SemanticSearchOutcome.Found, versao, candidatos, null, vetorizacaoMs, buscaMs);
    }

    private async Task<SemanticIndexStatus?> SafeStatusAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.StatusAsync(tenant, _options.ModelId, _options.Dimension, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
