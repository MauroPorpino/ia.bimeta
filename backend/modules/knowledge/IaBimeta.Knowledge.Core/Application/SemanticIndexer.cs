using IaBimeta.Knowledge.Contracts;
using IaBimeta.Knowledge.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Knowledge.Application;

public sealed record SemanticIndexerRun(int Claimed, int Embedded, int Retried, int Failed, int Deferred = 0);

/// <summary>
/// Um passo da vetorização assíncrona (roda no Worker, em laço): reserva textos pendentes, pede os vetores em lote ao provedor e grava. Idempotente e recuperável: se o Worker cair, a reserva vence e outro retoma; se o
/// serviço de vetores estiver fora do ar, os textos voltam à fila com espera crescente (nunca perdem a vez) até <see cref="KnowledgeSemanticOptions.MaxAttempts"/>, e só então ficam <c>Failed</c> (a publicação seguinte
/// os recoloca na fila). O serviço fora do ar NUNCA afeta o atendimento: o índice só deixa de crescer.
/// </summary>
public sealed class SemanticIndexer
{
    private readonly ISemanticStore _store;
    private readonly IEmbeddingProvider _embeddings;
    private readonly KnowledgeSemanticOptions _options;
    private readonly TimeProvider _clock;
    private readonly IIndexingTenantGate? _gate;

    public SemanticIndexer(ISemanticStore store, IEmbeddingProvider embeddings, KnowledgeSemanticOptions options, TimeProvider clock, IIndexingTenantGate? gate = null)
    {
        _store = store;
        _embeddings = embeddings;
        _options = options;
        _clock = clock;
        _gate = gate;
    }

    public async Task<SemanticIndexerRun> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new SemanticIndexerRun(0, 0, 0, 0);
        }

        var agora = _clock.GetUtcNow();

        // Troca de modelo ou de dimensão: os vetores antigos nunca se misturam com os novos; os textos voltam à fila e são vetorizados de novo.
        await _store.RequeueOtherModelsAsync(_options.BatchSize * 4, _options.ModelId, _options.Dimension, t => SemanticIndexService.HashOf(t, _options.ModelId, _options.Dimension), agora, cancellationToken);
        var lote = await _store.ClaimPendingAsync(_options.BatchSize, _options.ModelId, _options.Dimension, _options.LeaseDuration, agora, cancellationToken);
        if (lote.Count == 0)
        {
            return new SemanticIndexerRun(0, 0, 0, 0);
        }

        // Empresa suspensa (ou com a situação ilegível: falha fechada) não vai ao provedor de vetores. Os textos dela voltam à fila adiados, sem gastar tentativa e sem perder nada; as outras empresas seguem.
        var adiados = 0;
        if (_gate is not null)
        {
            var liberados = new List<PendingSemanticText>(lote.Count);
            var decisao = new Dictionary<TenantId, bool>();
            foreach (var item in lote)
            {
                if (!decisao.TryGetValue(item.Tenant, out var pode))
                {
                    try
                    {
                        pode = await _gate.CanIndexAsync(item.Tenant, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        pode = false;
                    }

                    decisao[item.Tenant] = pode;
                }

                if (pode)
                {
                    liberados.Add(item);
                }
                else
                {
                    await _store.DeferAsync(item, agora + _options.SuspendedDeferral, agora, cancellationToken);
                    adiados++;
                }
            }

            if (liberados.Count == 0)
            {
                return new SemanticIndexerRun(lote.Count, 0, 0, 0, adiados);
            }

            lote = liberados;
        }

        IReadOnlyList<float[]> vetores;
        try
        {
            vetores = await _embeddings.EmbedAsync(lote.Select(l => l.Text).ToList(), EmbeddingPurpose.Document, cancellationToken);
            if (vetores.Count != lote.Count || vetores.Any(v => v.Length != _options.Dimension))
            {
                throw new EmbeddingUnavailableException("dimension-mismatch", "O serviço devolveu vetores que não batem com o pedido ou com a dimensão do índice.");
            }
        }
        catch (EmbeddingUnavailableException ex)
        {
            var (tentativas, falhas) = (0, 0);
            foreach (var item in lote)
            {
                var proxima = item.Attempts + 1 >= _options.MaxAttempts ? (DateTimeOffset?)null : agora + BackoffAfter(item.Attempts + 1);
                await _store.FailAsync(item, ex.Code, proxima, agora, cancellationToken);
                if (proxima is null)
                {
                    falhas++;
                }
                else
                {
                    tentativas++;
                }
            }

            return new SemanticIndexerRun(lote.Count + adiados, 0, tentativas, falhas, adiados);
        }

        var gravados = 0;
        for (var i = 0; i < lote.Count; i++)
        {
            if (await _store.CompleteAsync(lote[i], vetores[i], _options.ModelId, _options.Dimension, _clock.GetUtcNow(), cancellationToken))
            {
                gravados++;
            }
        }

        return new SemanticIndexerRun(lote.Count + adiados, gravados, 0, 0, adiados);
    }

    internal TimeSpan BackoffAfter(int attempts)
    {
        var fator = Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromTicks((long)Math.Min(_options.RetryBackoff.Ticks * fator, _options.MaxRetryBackoff.Ticks));
    }
}
