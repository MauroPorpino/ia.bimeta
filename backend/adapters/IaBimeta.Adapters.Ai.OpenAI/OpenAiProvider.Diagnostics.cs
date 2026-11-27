using System.Net.Http.Headers;
using System.Text.Json;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// A verificação de conectividade da OpenAI (Etapa 05): <c>GET {base}/models/{modelo}</c> (Models API, documentação oficial lida em 27/11/2026), que exige só o cabeçalho <c>Authorization: Bearer</c> e devolve o objeto do
/// modelo (<c>id</c>, <c>object</c>, <c>created</c>, <c>owned_by</c>). NÃO gera texto. Pontos que a documentação NÃO diz e que este adaptador trata de forma conservadora: os códigos 401/403/404 desta chamada, se ela é gratuita
/// ou limitada por taxa, e o escopo de chave restrita (um 403 vira <c>Inconclusive</c>, nunca "indisponível"). A OpenAI não declara capacidades do modelo: <see cref="AiConnectivityResult.ConversationCapable"/> fica nulo.
/// </summary>
public sealed partial class OpenAiProvider : IAiProviderDiagnostics
{
    public async Task<AiConnectivityResult> CheckConnectivityAsync(AiConnectivityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inicio = _clock.GetTimestamp();
        TimeSpan Decorrido() => _clock.GetElapsedTime(inicio);

        using var mensagem = OpenAiRequestFactory.CreateModelLookup(request, _options);
        if (mensagem is null)
        {
            // Nada foi enviado: a credencial fora do formato nunca vai a um cabeçalho.
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
                var confere = corpo is not null && IdConfere(corpo, request.ModelId);
                return confere
                    ? new AiConnectivityResult(AiConnectivityState.Verified, true, true, true, null, "ok", Decorrido())
                    : new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "invalid-response", Decorrido());
            }

            return status switch
            {
                401 => new AiConnectivityResult(AiConnectivityState.AuthenticationFailed, true, false, null, null, "unauthorized", Decorrido()),
                403 => new AiConnectivityResult(AiConnectivityState.Inconclusive, true, null, null, null, "forbidden", Decorrido()),
                404 => new AiConnectivityResult(AiConnectivityState.ModelUnavailable, true, null, false, null, "model-not-found", Decorrido()),
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

    private static bool IdConfere(byte[] corpo, string modelo)
    {
        try
        {
            using var documento = JsonDocument.Parse(corpo);
            return documento.RootElement.ValueKind == JsonValueKind.Object
                && documento.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), modelo, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
