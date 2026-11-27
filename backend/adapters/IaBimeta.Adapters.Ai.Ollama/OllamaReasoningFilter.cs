using System.Text.Json;
using System.Text.RegularExpressions;

namespace IaBimeta.Adapters.Ai.Ollama;

internal enum ReasoningOutcome
{
    /// <summary>Nada a descartar, ou só blocos completos separados no INÍCIO foram descartados: o texto devolvido pode seguir.</summary>
    Clean = 1,

    /// <summary>Há marcador incompleto, isolado, fora de lugar ou no campo de resposta de um JSON: risco de expor raciocínio; a saída é INUTILIZÁVEL.</summary>
    Leak = 2,

    /// <summary>A saída é só raciocínio (nada sobra depois dos blocos).</summary>
    OnlyReasoning = 3,
}

/// <summary>
/// A filtragem MÍNIMA do raciocínio que alguns modelos deixam NO TEXTO da resposta entre marcadores <c>&lt;think&gt;…&lt;/think&gt;</c> (o campo separado <c>message.thinking</c> nunca é lido). Regras, nesta ordem:
/// <list type="number">
/// <item>Blocos COMPLETOS no INÍCIO da saída (um ou vários, só separados por espaço) são descartados.</item>
/// <item>Se o que sobra é vazio, a saída é só raciocínio. Se sobra um JSON (objeto ou lista), ele NÃO é reescrito: é aceito sem alteração, EXCETO quando algum texto dele começa com marcador de abertura ou contém marcador de
/// fechamento (raciocínio no campo de resposta ao cliente), caso em que é inutilizável. Um marcador de abertura no MEIO de um texto de JSON é tratado como conteúdo literal legítimo.</item>
/// <item>Se sobra texto comum, qualquer marcador restante (abertura sem fechamento, fechamento isolado, bloco no meio) torna a saída inutilizável: o texto não é "consertado".</item>
/// </list>
/// LIMITE: só enxerga o raciocínio MARCADO com <c>think</c>. Raciocínio sem marcador, ou com outro marcador, não é detectável e não é afirmado que seja.
/// </summary>
internal static class OllamaReasoningFilter
{
    private static readonly Regex Marker = new(@"</?\s*think\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Opening = new(@"^\s*<\s*think\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Closing = new(@"</\s*think\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ReasoningOutcome Clean(string? content, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return ReasoningOutcome.Clean;
        }

        var resto = content;
        var tinhaBloco = false;
        while (Opening.IsMatch(resto))
        {
            var abertura = Opening.Match(resto);
            var fechamento = Closing.Match(resto, abertura.Length);
            if (!fechamento.Success)
            {
                return ReasoningOutcome.Leak; // bloco sem fechamento: tudo o que vem depois pode ser raciocínio
            }

            tinhaBloco = true;
            resto = resto[(fechamento.Index + fechamento.Length)..];
        }

        resto = resto.Trim();
        if (resto.Length == 0)
        {
            return tinhaBloco ? ReasoningOutcome.OnlyReasoning : ReasoningOutcome.Clean;
        }

        if (resto[0] is '{' or '[' && TryJson(resto, out var json))
        {
            using (json)
            {
                if (HasLeakInStrings(json.RootElement))
                {
                    return ReasoningOutcome.Leak;
                }
            }

            text = resto;
            return ReasoningOutcome.Clean;
        }

        if (Marker.IsMatch(resto))
        {
            return ReasoningOutcome.Leak;
        }

        text = resto;
        return ReasoningOutcome.Clean;
    }

    private static bool TryJson(string texto, out JsonDocument documento)
    {
        try
        {
            documento = JsonDocument.Parse(texto);
            return true;
        }
        catch (JsonException)
        {
            documento = null!;
            return false;
        }
    }

    private static bool HasLeakInStrings(JsonElement elemento)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.String:
                var valor = elemento.GetString() ?? string.Empty;
                return Opening.IsMatch(valor) || Closing.IsMatch(valor);
            case JsonValueKind.Array:
                return elemento.EnumerateArray().Any(HasLeakInStrings);
            case JsonValueKind.Object:
                return elemento.EnumerateObject().Any(p => HasLeakInStrings(p.Value));
            default:
                return false;
        }
    }
}
