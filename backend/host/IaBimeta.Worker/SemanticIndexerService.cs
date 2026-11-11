using IaBimeta.Knowledge.Application;
using IaBimeta.Knowledge.Domain;

namespace IaBimeta.Worker;

/// <summary>
/// O laço da vetorização do conhecimento publicado (ADR 0034, ver <see cref="SemanticIndexer"/>). Com <c>Knowledge:Semantic:Enabled</c> falso o serviço não faz nada (não toca o banco do módulo). O serviço de
/// vetores fora do ar nunca derruba o Worker: o indexador registra a falha por texto, com espera crescente; só o tipo do erro vai ao log (nunca texto de conhecimento).
/// </summary>
internal sealed class SemanticIndexerService : BackgroundService
{
    private readonly SemanticIndexer _indexer;
    private readonly KnowledgeSemanticOptions _options;
    private readonly ILogger<SemanticIndexerService> _logger;

    public SemanticIndexerService(SemanticIndexer indexer, KnowledgeSemanticOptions options, ILogger<SemanticIndexerService> logger)
    {
        _indexer = indexer;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Índice semântico desligado (Knowledge:Semantic:Enabled = false): a vetorização não roda.");
            return;
        }

        using var relogio = new PeriodicTimer(_options.ReconcileInterval);
        try
        {
            do
            {
                try
                {
                    // Esvazia a fila a cada ciclo: continua enquanto houver lote cheio.
                    SemanticIndexerRun passo;
                    do
                    {
                        passo = await _indexer.RunOnceAsync(stoppingToken);
                        if (passo.Claimed > 0)
                        {
                            _logger.LogInformation("Vetorização: {Claimed} reservados, {Embedded} gravados, {Retried} para nova tentativa, {Failed} definitivamente falhos.", passo.Claimed, passo.Embedded, passo.Retried, passo.Failed);
                        }
                    }
                    while (passo.Embedded >= _options.BatchSize && !stoppingToken.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError("A vetorização falhou neste ciclo: {ErrorType}.", ex.GetType().Name);
                }
            }
            while (await relogio.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada normal do host.
        }
    }
}
