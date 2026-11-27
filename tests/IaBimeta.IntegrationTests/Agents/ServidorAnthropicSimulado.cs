using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O "servidor da Anthropic" dos testes ponta a ponta: um <see cref="HttpMessageHandler"/> que responde pelo roteiro do TENANT cuja credencial chegou no cabeçalho <c>Authorization</c> (a mesma captura do
/// servidor da OpenAI). Nenhuma rede e nenhuma chave real: o <c>AnthropicProvider</c> de verdade (pedido, uso, erros, tempo limite) conversa com ele, e o AiGateway, o ledger e o Agents são os reais.
/// Sem roteiro para a credencial, responde 401 de chave inválida.
/// </summary>
internal sealed class ServidorAnthropicSimulado : HttpMessageHandler
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

        // O quarto campo da captura (identificador da chamada na OpenAI) leva aqui a versão da API: a Anthropic não tem cabeçalho de identificação do cliente.
        var captura = new CapturaHttp(
            credencial,
            request.RequestUri!.ToString(),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.TryGetValues("anthropic-version", out var versoes) ? versoes.FirstOrDefault() : null);
        _capturas.Enqueue(captura);

        return _roteiros.TryGetValue(credencial, out var roteiro)
            ? await roteiro(captura, cancellationToken)
            : Json(HttpStatusCode.Unauthorized, Erro("authentication_error"));
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

    /// <summary>Uma resposta concluída com o texto (a decisão em JSON, na saída estruturada) e o consumo REAL informado (a entrada vem em três parcelas, como na Messages API).</summary>
    public static HttpResponseMessage Sucesso(string texto, int entrada = 120, int leituraDoCache = 0, int escritaNoCache = 0, int saida = 30, string requestId = "req_teste", string stopReason = "end_turn") =>
        Json(
            HttpStatusCode.OK,
            new JsonObject
            {
                ["id"] = "msg_teste",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = "claude-haiku-5-5",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = texto }),
                ["stop_reason"] = stopReason,
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = entrada,
                    ["cache_creation_input_tokens"] = escritaNoCache,
                    ["cache_read_input_tokens"] = leituraDoCache,
                    ["output_tokens"] = saida,
                },
            }.ToJsonString(),
            ("request-id", requestId));

    public static string Erro(string tipo, string? codigoDeDetalhe = null)
    {
        var erro = new JsonObject { ["type"] = tipo, ["message"] = "mensagem do provedor" };
        if (codigoDeDetalhe is not null)
        {
            erro["details"] = new JsonObject { ["error_code"] = codigoDeDetalhe };
        }

        return new JsonObject { ["type"] = "error", ["error"] = erro, ["request_id"] = "req_erro" }.ToJsonString();
    }
}
