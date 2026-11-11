using System.Collections.Concurrent;
using IaBimeta.Knowledge.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// A busca semantica SIMULADA dos testes (nao ha pgvector nem Ollama no Windows): cada tenant tem o seu roteiro e o seu registro de pedidos, escolhidos pelo contexto confiavel da operacao. Serve a provar o
/// FLUXO (acerto, fallback, avaliacao, falha, isolamento) com o resto real: PostgreSQL, Conversations, AiGateway e a politica da producao. NAO prova a qualidade da similaridade: isso e da homologacao.
/// </summary>
public sealed class RegistroDaBuscaSemantica
{
    private readonly ConcurrentDictionary<Guid, Func<SemanticSearchRequest, SemanticSearchResult>> _roteiros = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<SemanticSearchRequest>> _pedidos = new();

    public void Roteirizar(TenantId tenant, Func<SemanticSearchRequest, SemanticSearchResult> roteiro) => _roteiros[tenant.Value] = roteiro;

    public IReadOnlyList<SemanticSearchRequest> PedidosDe(TenantId tenant) => _pedidos.TryGetValue(tenant.Value, out var fila) ? fila.ToList() : [];

    internal SemanticSearchResult Buscar(TenantId tenant, SemanticSearchRequest pedido)
    {
        _pedidos.GetOrAdd(tenant.Value, _ => new ConcurrentQueue<SemanticSearchRequest>()).Enqueue(pedido);
        return _roteiros.TryGetValue(tenant.Value, out var roteiro) ? roteiro(pedido) : SemanticSearchResult.Of(SemanticSearchOutcome.Disabled);
    }
}

internal sealed class BuscaSemanticaSimulada(IRequestContextAccessor contexto, RegistroDaBuscaSemantica registro) : IKnowledgeSearch
{
    public Task<SemanticSearchResult> SearchAsync(SemanticSearchRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(registro.Buscar(contexto.GetRequired().TenantId, request));
}

/// <summary>A IA local simulada dos testes (sem Ollama): devolve o JSON roteirizado e registra o que recebeu, para provar o que vai ao modelo (texto redigido, como dado) e que nada e aplicado sem revisao humana.</summary>
public sealed class IaLocalSimulada : ILocalGenerationProvider
{
    public string ModelId => "ia-local-simulada";

    public List<LocalGenerationRequest> Pedidos { get; } = [];

    public Func<LocalGenerationRequest, string> Resposta { get; set; } = _ => "{}";

    public LocalGenerationUnavailableException? Falha { get; set; }

    public Task<LocalGenerationResult> GenerateJsonAsync(LocalGenerationRequest request, CancellationToken cancellationToken = default)
    {
        lock (Pedidos)
        {
            Pedidos.Add(request);
        }

        return Falha is null ? Task.FromResult(new LocalGenerationResult(Resposta(request), 1)) : throw Falha;
    }
}
