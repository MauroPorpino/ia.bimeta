using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IaBimeta.Knowledge.Contracts;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>
/// O adaptador do Ollama (ADR 0034): implementa as portas de embeddings e de geração local do Knowledge por HTTP. Nada do negócio depende dele: trocar de executor é trocar este adaptador. Falha vira exceção
/// tipada com código curto fixo (nunca o texto enviado nem o corpo da resposta). O vetor devolvido é validado (quantidade e dimensão) e normalizado.
/// </summary>
internal sealed class OllamaProvider : IEmbeddingProvider, ILocalGenerationProvider
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;

    public OllamaProvider(HttpClient http, OllamaOptions options)
    {
        _http = http;
        _options = options;
    }

    public EmbeddingModelInfo Model => new(_options.EmbeddingModelId, _options.EmbeddingDimension);

    public string ModelId => _options.GenerationModelId ?? string.Empty;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        var entradas = purpose == EmbeddingPurpose.Query && !string.IsNullOrWhiteSpace(_options.QueryInstruction)
            ? texts.Select(t => $"Instruct: {_options.QueryInstruction}\nQuery: {t}").ToList()
            : texts.ToList();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EmbeddingTimeout);
        try
        {
            using var resposta = await _http.PostAsJsonAsync("api/embed", new { model = _options.EmbeddingModelId, input = entradas }, timeout.Token);
            if (!resposta.IsSuccessStatusCode)
            {
                throw new EmbeddingUnavailableException(resposta.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? "unauthorized" : $"http-{(int)resposta.StatusCode}", "O serviço de embeddings recusou o pedido.");
            }

            var corpo = await resposta.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            var lista = corpo?["embeddings"] as JsonArray ?? throw new EmbeddingUnavailableException("invalid-response", "A resposta do serviço de embeddings não tem vetores.");
            if (lista.Count != texts.Count)
            {
                throw new EmbeddingUnavailableException("invalid-response", "A quantidade de vetores não bate com a de textos.");
            }

            var vetores = new List<float[]>(lista.Count);
            foreach (var item in lista)
            {
                var numeros = item as JsonArray ?? throw new EmbeddingUnavailableException("invalid-response", "Vetor inválido.");
                if (numeros.Count != _options.EmbeddingDimension)
                {
                    throw new EmbeddingUnavailableException("dimension-mismatch", "O vetor não tem a dimensão configurada.");
                }

                vetores.Add(Normalize(numeros.Select(n => n!.GetValue<float>()).ToArray()));
            }

            return vetores;
        }
        catch (EmbeddingUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbeddingUnavailableException("timeout", "O serviço de embeddings não respondeu a tempo.");
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingUnavailableException("unreachable", "O serviço de embeddings não está acessível.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new EmbeddingUnavailableException("invalid-response", "A resposta do serviço de embeddings é inválida.", ex);
        }
    }

    public async Task<LocalGenerationResult> GenerateJsonAsync(LocalGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_options.GenerationModelId))
        {
            throw new LocalGenerationUnavailableException("not-configured", "O modelo generativo local não foi configurado.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.GenerationTimeout);
        var inicio = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            JsonNode? formato;
            try
            {
                formato = JsonNode.Parse(request.JsonSchema);
            }
            catch (JsonException ex)
            {
                throw new LocalGenerationUnavailableException("invalid-schema", "O esquema de saída é inválido.", ex);
            }

            var pedido = new JsonObject
            {
                ["model"] = _options.GenerationModelId,
                ["stream"] = false,
                ["format"] = formato,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = request.SystemInstruction },
                    new JsonObject { ["role"] = "user", ["content"] = request.UserContent }),
                ["options"] = new JsonObject { ["temperature"] = 0, ["num_predict"] = request.MaxOutputTokens },
            };
            using var resposta = await _http.PostAsJsonAsync("api/chat", pedido, timeout.Token);
            if (!resposta.IsSuccessStatusCode)
            {
                throw new LocalGenerationUnavailableException($"http-{(int)resposta.StatusCode}", "O serviço de geração local recusou o pedido.");
            }

            var corpo = await resposta.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            var conteudo = corpo?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(conteudo))
            {
                throw new LocalGenerationUnavailableException("invalid-response", "A resposta não tem conteúdo.");
            }

            try
            {
                using var _ = JsonDocument.Parse(conteudo);
            }
            catch (JsonException ex)
            {
                throw new LocalGenerationUnavailableException("invalid-response", "A saída do modelo não é JSON.", ex);
            }

            return new LocalGenerationResult(conteudo, (long)System.Diagnostics.Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        }
        catch (LocalGenerationUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalGenerationUnavailableException("timeout", "O serviço de geração local não respondeu a tempo.");
        }
        catch (HttpRequestException ex)
        {
            throw new LocalGenerationUnavailableException("unreachable", "O serviço de geração local não está acessível.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new LocalGenerationUnavailableException("invalid-response", "A resposta do serviço de geração local é inválida.", ex);
        }
    }

    internal static float[] Normalize(float[] vector)
    {
        double soma = 0;
        foreach (var v in vector)
        {
            soma += (double)v * v;
        }

        var norma = Math.Sqrt(soma);
        if (norma < 1e-12)
        {
            throw new EmbeddingUnavailableException("invalid-response", "Vetor nulo.");
        }

        var saida = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            saida[i] = (float)(vector[i] / norma);
        }

        return saida;
    }
}
