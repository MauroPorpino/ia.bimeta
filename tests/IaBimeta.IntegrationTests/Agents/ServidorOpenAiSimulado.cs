using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>O que o "servidor da OpenAI" recebeu: a credencial do cabeçalho, o endereço, o corpo e o identificador da chamada.</summary>
internal sealed record CapturaHttp(string Credencial, string Url, string Corpo, string? ClientRequestId)
{
    public JsonNode Json => JsonNode.Parse(Corpo)!;
}

/// <summary>
/// O "servidor da OpenAI" dos testes ponta a ponta: um <see cref="HttpMessageHandler"/> que responde pelo roteiro do TENANT cuja credencial chegou no cabeçalho <c>Authorization</c>. Nenhuma rede e nenhuma chave
/// real: o <c>OpenAiProvider</c> de verdade (pedido, uso, erros, tempo limite) conversa com ele, e o AiGateway, o ledger e o Agents são os reais. Sem roteiro para a credencial, responde 401 de chave inválida.
/// </summary>
internal sealed class ServidorOpenAiSimulado : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>>> _roteiros = new();
    private readonly ConcurrentQueue<CapturaHttp> _capturas = new();

    public IReadOnlyList<CapturaHttp> Capturas => _capturas.ToList();

    public IReadOnlyList<CapturaHttp> DaCredencial(string credencial) => _capturas.Where(c => c.Credencial == credencial).ToList();

    public void Definir(string credencial, Func<CapturaHttp, CancellationToken, Task<HttpResponseMessage>> roteiro) => _roteiros[credencial] = roteiro;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var autorizacao = request.Headers.Authorization;
        var credencial = autorizacao is { Scheme: "Bearer" } ? autorizacao.Parameter ?? string.Empty : string.Empty;
        var captura = new CapturaHttp(
            credencial,
            request.RequestUri!.ToString(),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.TryGetValues("X-Client-Request-Id", out var ids) ? ids.FirstOrDefault() : null);
        _capturas.Enqueue(captura);

        return _roteiros.TryGetValue(credencial, out var roteiro)
            ? await roteiro(captura, cancellationToken)
            : Json(HttpStatusCode.Unauthorized, Erro("invalid_api_key"));
    }

    // Os corpos da Responses API

    public static HttpResponseMessage Json(HttpStatusCode status, string corpo, params (string Nome, string Valor)[] cabecalhos)
    {
        var resposta = new HttpResponseMessage(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };
        foreach (var (nome, valor) in cabecalhos)
        {
            resposta.Headers.TryAddWithoutValidation(nome, valor);
        }

        return resposta;
    }

    /// <summary>Uma resposta concluída com o texto (a decisão em JSON, na saída estruturada) e o consumo REAL informado.</summary>
    public static HttpResponseMessage Sucesso(string texto, int entrada = 120, int cache = 0, int saida = 30, string requestId = "req_teste") =>
        Json(
            HttpStatusCode.OK,
            new JsonObject
            {
                ["id"] = "resp_teste",
                ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = texto }),
                }),
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = entrada,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = cache },
                    ["output_tokens"] = saida,
                    ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                },
            }.ToJsonString(),
            ("x-request-id", requestId));

    public static HttpResponseMessage SemConsumo(string texto) =>
        Json(
            HttpStatusCode.OK,
            new JsonObject
            {
                ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = texto }),
                }),
            }.ToJsonString());

    public static string Erro(string codigo, string? tipo = null) =>
        new JsonObject { ["error"] = new JsonObject { ["message"] = "mensagem do provedor", ["type"] = tipo, ["param"] = null, ["code"] = codigo } }.ToJsonString();
}
