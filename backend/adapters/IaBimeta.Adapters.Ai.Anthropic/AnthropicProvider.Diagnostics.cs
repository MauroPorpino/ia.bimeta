using System.Net.Http.Headers;
using System.Text.Json;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.Adapters.Ai.Anthropic;

/// <summary>
/// A verificação de conectividade da Anthropic (Etapa 05): <c>GET {base}/models/{modelo}</c> (Models API, documentação oficial lida em 27/11/2026), com <c>Authorization: Bearer</c> e <c>anthropic-version</c> (o mesmo
/// método e a mesma versão da geração). NÃO gera texto. A resposta traz <c>lifecycle</c> (<c>active</c>, <c>deprecated</c>, <c>retired</c>) e <c>capabilities</c>; a documentação NÃO diz se a chamada é gratuita, nem o código exato
/// do modelo inexistente (404 genérico) nem se <c>x-api-key</c> serve aqui. Um modelo <c>retired</c> é "indisponível"; as capacidades NÃO são lidas como "serve à conversa" (nenhuma delas diz isso): <see cref="AiConnectivityResult.ConversationCapable"/> fica nulo.
/// </summary>
public sealed partial class AnthropicProvider : IAiProviderDiagnostics
{
    public async Task<AiConnectivityResult> CheckConnectivityAsync(AiConnectivityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inicio = _clock.GetTimestamp();
        TimeSpan Decorrido() => _clock.GetElapsedTime(inicio);

        using var mensagem = AnthropicRequestFactory.CreateModelLookup(request, _options);
        if (mensagem is null)
        {
            return new AiConnectivityResult(AiConnectivityState.AuthenticationFailed, null, false, null, null, "credential-invalid-format", Decorrido());
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(10));
        try
        {
            using var resposta = await _http.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            var status = (int)resposta.StatusCode;
            if (status is >= 200 and < 300)
            {
                var corpo = await ReadBoundedAsync(resposta.Content, _options.MaxResponseBytes, limite.Token);
                return Interpretar(corpo, request.ModelId, Decorrido());
            }

            return status switch
            {
                401 => new AiConnectivityResult(AiConnectivityState.AuthenticationFailed, true, false, null, null, "unauthorized", Decorrido()),
                403 => new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "forbidden", Decorrido()),
                404 => new AiConnectivityResult(AiConnectivityState.ModelUnavailable, true, null, false, null, "model-not-found", Decorrido()),
                402 => new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "billing", Decorrido()),
                429 => new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "rate-limited", Decorrido()),
                >= 500 => new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "server-error", Decorrido()),
                _ => new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, $"http-{status}", Decorrido()),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new AiConnectivityResult(AiConnectivityState.Unreachable, false, null, null, null, "timeout", Decorrido());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new AiConnectivityResult(AiConnectivityState.Unreachable, false, null, null, null, "network-error", Decorrido());
        }
    }

    private static AiConnectivityResult Interpretar(byte[]? corpo, string modelo, TimeSpan latencia)
    {
        if (corpo is null)
        {
            return new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "invalid-response", latencia);
        }

        try
        {
            using var documento = JsonDocument.Parse(corpo);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object
                || !raiz.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), modelo, StringComparison.Ordinal))
            {
                // O identificador pode ser um apelido que a API resolve para o identificador de versão: sem igualdade exata não se conclui nada.
                return new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "invalid-response", latencia);
            }

            var ciclo = raiz.TryGetProperty("lifecycle", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            return ciclo == "retired"
                ? new AiConnectivityResult(AiConnectivityState.ModelUnavailable, true, true, false, null, "model-retired", latencia)
                : new AiConnectivityResult(AiConnectivityState.Verified, true, true, true, null, ciclo == "deprecated" ? "model-deprecated" : "ok", latencia);
        }
        catch (JsonException)
        {
            return new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "invalid-response", latencia);
        }
    }
}
