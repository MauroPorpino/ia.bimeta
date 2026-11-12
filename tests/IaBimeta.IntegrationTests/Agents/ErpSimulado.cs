using System.Collections.Concurrent;
using System.Text;
using IaBimeta.Capabilities.Infrastructure.Outbound;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O "ERP" dos testes ponta a ponta do Agents: ocupa o lugar do transporte de saida do Motor de Integracao (nenhuma rede), responde o que o teste roteirizar e registra cada chamada que SAIU (host, caminho, cabecalhos).
/// O resto do motor (catalogo, escopo, validacao, registro de execucao, auditoria) e o REAL, com PostgreSQL.
/// </summary>
public sealed class ErpSimulado : IOutboundHttp
{
    public ConcurrentQueue<OutboundRequest> Chamadas { get; } = new();

    /// <summary>A resposta por chamada. Padrao: preco e prazo fixos.</summary>
    public static readonly Func<OutboundRequest, OutboundResponse> RoteiroPadrao = _ => Json(200, """{"preco":42.5,"prazo":"2 dias"}""");

    public Func<OutboundRequest, OutboundResponse> Roteiro { get; set; } = RoteiroPadrao;

    public static OutboundResponse Json(int status, string corpo) => new(status, "application/json", Encoding.UTF8.GetBytes(corpo), null);

    public IReadOnlyList<OutboundRequest> De(string host) => Chamadas.Where(c => c.Uri.Host == host).ToList();

    public Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken cancellationToken)
    {
        Chamadas.Enqueue(request);
        return Task.FromResult(Roteiro(request));
    }
}
