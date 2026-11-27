using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O "servidor Ollama" dos testes ponta a ponta (Etapa 04): um <see cref="HttpMessageHandler"/> que responde a <c>POST /api/chat</c>. O Ollama não tem chave por tenant, então o roteiro é escolhido pelo CANÁRIO das
/// instruções do tenant, que o Agents coloca no texto do sistema (cada cenário tem o seu): é assim que o teste prova que o pedido de um tenant só leva o contexto dele. Nenhuma rede e nenhum modelo real.
/// </summary>
internal sealed class ServidorOllamaSimulado : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>>> _roteiros = new();
    private readonly ConcurrentQueue<CapturaHttp> _capturas = new();

    public IReadOnlyList<CapturaHttp> Capturas => _capturas.ToList();

    public IReadOnlyList<CapturaHttp> DoTenant(string canarioDasInstrucoes) => _capturas.Where(c => c.Corpo.Contains(canarioDasInstrucoes, StringComparison.Ordinal)).ToList();

    public void Definir(string canarioDasInstrucoes, Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> roteiro) => _roteiros[canarioDasInstrucoes] = roteiro;

    /// <summary>O roteiro das requisições SEM canário de tenant (a sondagem do servidor: GET /api/version e /api/tags, POST /api/show). Sem ele, 404.</summary>
    public Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>>? Padrao { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var corpo = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var captura = new CapturaHttp(request.Headers.Authorization?.Parameter ?? string.Empty, request.RequestUri!.ToString(), corpo, null);
        _capturas.Enqueue(captura);

        foreach (var (canario, roteiro) in _roteiros)
        {
            if (corpo.Contains(canario, StringComparison.Ordinal))
            {
                return await roteiro(captura, cancellationToken);
            }
        }

        if (Padrao is not null)
        {
            return await Padrao(captura, cancellationToken);
        }

        return Json(HttpStatusCode.NotFound, "{\"error\":\"model not found\"}");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string corpo, params (string Nome, string Valor)[] cabecalhos)
    {
        var resposta = new HttpResponseMessage(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        foreach (var (nome, valor) in cabecalhos)
        {
            resposta.Headers.TryAddWithoutValidation(nome, valor);
        }

        return resposta;
    }

    /// <summary>Uma resposta concluída com o texto (a decisão em JSON) e as contagens REAIS de tokens do Ollama (<c>prompt_eval_count</c> e <c>eval_count</c>).</summary>
    public static HttpResponseMessage Sucesso(string texto, int entrada = 120, int saida = 30, string motivo = "stop") =>
        Json(
            HttpStatusCode.OK,
            new JsonObject
            {
                ["model"] = "qwen3:4b",
                ["created_at"] = "2026-11-27T12:00:00Z",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = texto, ["thinking"] = "raciocinio interno que nunca vai ao cliente" },
                ["done"] = true,
                ["done_reason"] = motivo,
                ["prompt_eval_count"] = entrada,
                ["eval_count"] = saida,
            }.ToJsonString());

    public static HttpResponseMessage Erro(HttpStatusCode status, string mensagem) => Json(status, new JsonObject { ["error"] = mensagem }.ToJsonString());
}
