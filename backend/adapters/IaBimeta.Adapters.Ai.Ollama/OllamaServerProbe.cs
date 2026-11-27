using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IaBimeta.Adapters.Ai.Ollama;

/// <summary>A situação de UM modelo do catálogo no servidor Ollama.</summary>
/// <param name="Installed">O modelo está instalado (aparece em <c>GET /api/tags</c>).</param>
/// <param name="Compatible">Instalado E declara a capacidade <c>completion</c> em <c>POST /api/show</c> (conversa). Embedding puro ou modelo sem a capacidade não é compatível.</param>
/// <param name="SupportsThinking">Declara a capacidade <c>thinking</c>: o perfil pode precisar de <c>think</c>.</param>
/// <param name="ConversationCapabilityKnown">Falso quando <c>POST /api/show</c> falhou: a capacidade é DESCONHECIDA (não "incompatível").</param>
/// <param name="ContextLength">A janela nativa do modelo (<c>model_info.*.context_length</c>), quando informada.</param>
public sealed record OllamaModelStatus(string ModelId, bool Installed, bool Compatible, bool SupportsThinking, int? ContextLength, bool ConversationCapabilityKnown = true);

/// <summary>O servidor Ollama visto pelo ia.bimeta: alcançável ou não, a versão e a situação de cada modelo pedido. Nunca traz o corpo do servidor nem o endereço.</summary>
public sealed record OllamaServerStatus(bool Reachable, string? Version, string? ErrorCode, IReadOnlyList<OllamaModelStatus> Models);

/// <summary>
/// A sondagem SOMENTE LEITURA do servidor Ollama (<c>GET /api/version</c>, <c>GET /api/tags</c> e <c>POST /api/show</c>), que distingue servidor acessível, modelo instalado e modelo compatível com conversa. Nada é
/// baixado, instalado nem alterado, e nenhum modelo é escolhido por ela: quem escolhe é o catálogo da configuração. O endereço é o mesmo da configuração da infraestrutura.
/// </summary>
public sealed class OllamaServerProbe
{
    private readonly HttpClient _http;
    private readonly OllamaChatOptions _options;

    public OllamaServerProbe(HttpClient http, OllamaChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = http;
        _options = options;
    }

    public static OllamaServerProbe Create(OllamaChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new OllamaServerProbe(new HttpClient(OllamaChatProvider.CreateHandler(options)) { Timeout = Timeout.InfiniteTimeSpan }, options);
    }

    public async Task<OllamaServerStatus> ProbeAsync(IEnumerable<string> modelIds, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        var pedidos = modelIds.Distinct(StringComparer.Ordinal).ToList();
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(timeout);

        try
        {
            string? versao = null;
            var rotuloVersao = await GetJsonAsync("api/version", limite.Token);
            if (rotuloVersao is { } v && v["version"] is JsonValue valor && valor.TryGetValue<string>(out var texto) && texto.Length <= 40)
            {
                versao = texto;
            }

            var tags = await GetJsonAsync("api/tags", limite.Token);
            if (tags?["models"] is not JsonArray lista)
            {
                return new OllamaServerStatus(true, versao, "invalid-response", []);
            }

            var instalados = lista.Select(m => m?["name"]?.GetValue<string>()).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
            var situacao = new List<OllamaModelStatus>();
            foreach (var id in pedidos)
            {
                // Um modelo escolhido sem tag explícita é "id:latest" no servidor.
                var instalado = instalados.Contains(id) || (!id.Contains(':') && instalados.Contains(id + ":latest"));
                if (!instalado)
                {
                    situacao.Add(new OllamaModelStatus(id, false, false, false, null));
                    continue;
                }

                situacao.Add(await ShowAsync(id, limite.Token));
            }

            return new OllamaServerStatus(true, versao, null, situacao);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new OllamaServerStatus(false, null, "timeout", []);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new OllamaServerStatus(false, null, "server-unavailable", []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new OllamaServerStatus(true, null, "invalid-response", []);
        }
    }

    private async Task<OllamaModelStatus> ShowAsync(string modelId, CancellationToken cancellationToken)
    {
        using var pedido = Request(HttpMethod.Post, "api/show");
        pedido.Content = new StringContent(new JsonObject { ["model"] = modelId }.ToJsonString(), Encoding.UTF8, "application/json");
        using var resposta = await _http.SendAsync(pedido, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!resposta.IsSuccessStatusCode)
        {
            // Instalado, mas sem como confirmar a capacidade: NÃO é tratado como compatível.
            return new OllamaModelStatus(modelId, true, false, false, null, ConversationCapabilityKnown: false);
        }

        var corpo = JsonNode.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        var capacidades = (corpo?["capabilities"] as JsonArray)?.Select(c => c?.GetValue<string>()).Where(c => c is not null).ToHashSet(StringComparer.Ordinal) ?? [];
        int? contexto = null;
        if (corpo?["model_info"] is JsonObject info)
        {
            foreach (var (chave, valor) in info)
            {
                if (chave.EndsWith(".context_length", StringComparison.Ordinal) && valor is JsonValue numero && numero.TryGetValue<int>(out var n))
                {
                    contexto = n;
                    break;
                }
            }
        }

        return new OllamaModelStatus(modelId, true, capacidades.Contains("completion"), capacidades.Contains("thinking"), contexto);
    }

    private async Task<JsonObject?> GetJsonAsync(string caminho, CancellationToken cancellationToken)
    {
        using var pedido = Request(HttpMethod.Get, caminho);
        using var resposta = await _http.SendAsync(pedido, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!resposta.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonNode.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
    }

    private HttpRequestMessage Request(HttpMethod metodo, string caminho)
    {
        var mensagem = new HttpRequestMessage(metodo, new Uri(_options.Base, caminho));
        mensagem.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(_options.BearerToken))
        {
            mensagem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        return mensagem;
    }
}
