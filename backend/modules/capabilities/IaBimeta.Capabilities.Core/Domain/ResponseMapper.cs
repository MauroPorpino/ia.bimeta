using System.Text;
using System.Text.Json;
using IaBimeta.Capabilities.Contracts;

namespace IaBimeta.Capabilities.Domain;

/// <summary>
/// O mapeamento da resposta do sistema externo para o que o motor devolve (ADR 0035, §2). A resposta é conteúdo NÃO confiável: só os campos declarados saem, tipados, com tamanho limitado e sem caracteres de controle;
/// tudo o mais (campos extras, cabeçalhos, mensagens de erro do sistema externo) é descartado. Campo obrigatório ausente ou de outro tipo invalida a resposta inteira.
/// </summary>
public static class ResponseMapper
{
    public const int DefaultFieldLength = 500;
    public const int HardFieldLength = 4000;

    /// <returns>O objeto de saída, ou <c>null</c> com <paramref name="invalid"/> verdadeiro se a resposta não corresponde ao esperado.</returns>
    public static JsonElement? Map(JsonElement body, CapabilityOperationSpec spec, out bool invalid)
    {
        invalid = false;
        using var buffer = new MemoryStream();
        using (var escritor = new Utf8JsonWriter(buffer))
        {
            escritor.WriteStartObject();

            if (spec.ResponseRoot is null)
            {
                if (!WriteFields(escritor, body, spec.Response, topLevel: true))
                {
                    invalid = true;
                    return null;
                }
            }
            else
            {
                var lista = Resolve(body, spec.ResponseRoot);
                if (lista is not { ValueKind: JsonValueKind.Array } array)
                {
                    invalid = true;
                    return null;
                }

                escritor.WriteStartArray("items");
                var total = 0;
                var truncada = false;
                foreach (var item in array.EnumerateArray())
                {
                    if (total >= spec.MaxItems)
                    {
                        truncada = true;
                        break;
                    }

                    escritor.WriteStartObject();
                    if (!WriteFields(escritor, item, spec.Response, topLevel: false))
                    {
                        invalid = true;
                        return null;
                    }

                    escritor.WriteEndObject();
                    total++;
                }

                escritor.WriteEndArray();
                escritor.WriteNumber("count", total);
                escritor.WriteBoolean("truncated", truncada);
            }

            escritor.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static bool WriteFields(Utf8JsonWriter escritor, JsonElement origem, IReadOnlyList<CapabilityResponseField> campos, bool topLevel)
    {
        foreach (var campo in campos)
        {
            var valor = Resolve(origem, campo.Pointer);
            if (valor is null || valor.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (campo.Required)
                {
                    return false;
                }

                escritor.WriteNull(campo.Name);
                continue;
            }

            var elemento = valor.Value;
            switch (campo.Type)
            {
                case CapabilityFieldType.String:
                    if (elemento.ValueKind != JsonValueKind.String)
                    {
                        if (campo.Required)
                        {
                            return false;
                        }

                        escritor.WriteNull(campo.Name);
                        break;
                    }

                    escritor.WriteString(campo.Name, Sanitize(elemento.GetString() ?? string.Empty, Math.Min(campo.MaxLength ?? DefaultFieldLength, HardFieldLength)));
                    break;
                case CapabilityFieldType.Integer:
                    if (elemento.ValueKind == JsonValueKind.Number && elemento.TryGetInt64(out var inteiro))
                    {
                        escritor.WriteNumber(campo.Name, inteiro);
                    }
                    else if (campo.Required)
                    {
                        return false;
                    }
                    else
                    {
                        escritor.WriteNull(campo.Name);
                    }

                    break;
                case CapabilityFieldType.Number:
                    if (elemento.ValueKind == JsonValueKind.Number && elemento.TryGetDecimal(out var numero))
                    {
                        escritor.WriteNumber(campo.Name, numero);
                    }
                    else if (campo.Required)
                    {
                        return false;
                    }
                    else
                    {
                        escritor.WriteNull(campo.Name);
                    }

                    break;
                case CapabilityFieldType.Boolean:
                    if (elemento.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        escritor.WriteBoolean(campo.Name, elemento.GetBoolean());
                    }
                    else if (campo.Required)
                    {
                        return false;
                    }
                    else
                    {
                        escritor.WriteNull(campo.Name);
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>Resolve um JSON Pointer (RFC 6901). Vazio = o próprio elemento. Objeto: propriedade exata; lista: índice numérico.</summary>
    internal static JsonElement? Resolve(JsonElement raiz, string pointer)
    {
        if (pointer.Length == 0)
        {
            return raiz;
        }

        if (pointer[0] != '/')
        {
            return null;
        }

        var atual = raiz;
        foreach (var bruto in pointer[1..].Split('/'))
        {
            var parte = bruto.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (atual.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!atual.TryGetProperty(parte, out var filho))
                    {
                        return null;
                    }

                    atual = filho;
                    break;
                case JsonValueKind.Array:
                    if (!int.TryParse(parte, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var indice) || indice >= atual.GetArrayLength())
                    {
                        return null;
                    }

                    atual = atual[indice];
                    break;
                default:
                    return null;
            }
        }

        return atual;
    }

    /// <summary>Remove caracteres de controle (menos quebra de linha e tabulação) e corta no tamanho máximo, sem partir um par substituto.</summary>
    internal static string Sanitize(string texto, int maximo)
    {
        var sb = new StringBuilder(Math.Min(texto.Length, maximo));
        foreach (var c in texto)
        {
            if (sb.Length >= maximo)
            {
                break;
            }

            if (char.IsControl(c) && c is not ('\n' or '\t'))
            {
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1]))
        {
            sb.Length--;
        }

        return sb.ToString();
    }
}
