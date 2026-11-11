using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Capabilities.Contracts;

namespace IaBimeta.Capabilities.Domain;

public sealed record ParameterValidationResult(IReadOnlyDictionary<string, object> Values, IReadOnlyList<CapabilityParameterProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// A validação dos parâmetros contra o esquema DECLARADO da operação (ADR 0035, §2). Os valores chegam como JSON não confiável (podem vir de um modelo de IA) e saem tipados e normalizados, ou o resultado traz problemas
/// com o NOME do parâmetro e a regra violada (nunca o valor). Regras: nome desconhecido é recusado; obrigatório sem valor nem padrão é recusado; <c>null</c> vale como ausente; o tipo é estrito (um número não entra
/// onde se espera texto e vice-versa); texto tem tamanho máximo, sem caracteres de controle (um parâmetro de caminho só aceita um conjunto seguro, e <c>.</c> e <c>..</c> nunca); o padrão (regex) roda em modo
/// não retrocedente, imune a ReDoS; inteiro e número respeitam faixa; enumeração é comparada exatamente.
/// </summary>
public static class ParameterValidator
{
    public const int DefaultMaxLength = 200;
    public const int HardMaxLength = 2000;

    private static readonly Regex SafePathValue = new(@"^[A-Za-z0-9._~@:+\-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static ParameterValidationResult Validate(IReadOnlyList<CapabilityParameterSpec> specs, IReadOnlyDictionary<string, JsonElement> input)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(input);

        var valores = new Dictionary<string, object>(StringComparer.Ordinal);
        var problemas = new List<CapabilityParameterProblem>();
        var conhecidos = specs.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var nome in input.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!conhecidos.Contains(nome))
            {
                problemas.Add(new CapabilityParameterProblem(Safe(nome), "unknown"));
            }
        }

        foreach (var spec in specs)
        {
            input.TryGetValue(spec.Name, out var bruto);
            var ausente = bruto.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

            if (ausente)
            {
                if (spec.Default is not null)
                {
                    if (TryCoerceDefault(spec, out var padrao))
                    {
                        valores[spec.Name] = padrao;
                    }
                    else
                    {
                        problemas.Add(new CapabilityParameterProblem(spec.Name, "format"));
                    }
                }
                else if (spec.Required)
                {
                    problemas.Add(new CapabilityParameterProblem(spec.Name, "required"));
                }

                continue;
            }

            var regra = TryNormalize(spec, bruto, out var valor);
            if (regra is not null)
            {
                problemas.Add(new CapabilityParameterProblem(spec.Name, regra));
            }
            else
            {
                valores[spec.Name] = valor!;
            }
        }

        return new ParameterValidationResult(valores, problemas);
    }

    /// <summary>Um nome desconhecido vem do chamador: nunca o devolve inteiro (pode ser enorme ou conter texto de ataque). Só um identificador curto e seguro; senão, um marcador.</summary>
    private static string Safe(string nome) => nome.Length is > 0 and <= 40 && nome.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? nome : "?";

    private static bool TryCoerceDefault(CapabilityParameterSpec spec, out object valor)
    {
        valor = default!;
        var texto = spec.Default!;
        JsonElement elemento;
        try
        {
            elemento = spec.Type switch
            {
                CapabilityParameterType.Integer or CapabilityParameterType.Number => JsonDocument.Parse(texto).RootElement.Clone(),
                CapabilityParameterType.Boolean => JsonDocument.Parse(texto.ToLowerInvariant()).RootElement.Clone(),
                _ => JsonSerializer.SerializeToElement(texto),
            };
        }
        catch (JsonException)
        {
            return false;
        }

        if (TryNormalize(spec, elemento, out var normalizado) is not null)
        {
            return false;
        }

        valor = normalizado!;
        return true;
    }

    /// <summary>Devolve <c>null</c> se o valor é válido (e o normaliza), ou o código curto da regra violada.</summary>
    private static string? TryNormalize(CapabilityParameterSpec spec, JsonElement bruto, out object? valor)
    {
        valor = null;
        switch (spec.Type)
        {
            case CapabilityParameterType.String:
            case CapabilityParameterType.Enum:
            {
                if (bruto.ValueKind != JsonValueKind.String)
                {
                    return "type";
                }

                var texto = bruto.GetString() ?? string.Empty;
                var maximo = Math.Min(spec.MaxLength ?? DefaultMaxLength, HardMaxLength);
                if (texto.Length > maximo)
                {
                    return "too-long";
                }

                if (texto.Length == 0)
                {
                    return spec.Required ? "required" : "format";
                }

                if (HasUnsafeChars(texto, spec.Location))
                {
                    return "unsafe-chars";
                }

                if (spec.Location == CapabilityParameterLocation.Path && (!SafePathValue.IsMatch(texto) || texto is "." or ".."))
                {
                    return "unsafe-chars";
                }

                if (spec.Type == CapabilityParameterType.Enum)
                {
                    if (spec.EnumValues is null || !spec.EnumValues.Contains(texto, StringComparer.Ordinal))
                    {
                        return "enum";
                    }
                }
                else if (spec.Pattern is not null && !CompilePattern(spec.Pattern).IsMatch(texto))
                {
                    return "pattern";
                }

                valor = texto;
                return null;
            }

            case CapabilityParameterType.Integer:
            {
                if (bruto.ValueKind != JsonValueKind.Number || !bruto.TryGetInt64(out var inteiro))
                {
                    return "type";
                }

                if ((spec.Min is { } min && inteiro < min) || (spec.Max is { } max && inteiro > max))
                {
                    return "range";
                }

                valor = inteiro;
                return null;
            }

            case CapabilityParameterType.Number:
            {
                if (bruto.ValueKind != JsonValueKind.Number || !bruto.TryGetDecimal(out var numero))
                {
                    return "type";
                }

                if ((spec.Min is { } min && numero < min) || (spec.Max is { } max && numero > max))
                {
                    return "range";
                }

                valor = numero;
                return null;
            }

            case CapabilityParameterType.Boolean:
            {
                if (bruto.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return "type";
                }

                valor = bruto.GetBoolean();
                return null;
            }

            case CapabilityParameterType.Date:
            {
                if (bruto.ValueKind != JsonValueKind.String)
                {
                    return "type";
                }

                var texto = bruto.GetString() ?? string.Empty;
                if (!DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var data))
                {
                    return "format";
                }

                valor = data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return null;
            }

            default:
                return "type";
        }
    }

    /// <summary>Caracteres de controle nunca entram (CR e LF dariam injeção de cabeçalho ou de linha). Só o corpo aceita quebra de linha e tabulação, para texto de várias linhas.</summary>
    private static bool HasUnsafeChars(string texto, CapabilityParameterLocation lugar)
    {
        foreach (var c in texto)
        {
            if (!char.IsControl(c))
            {
                continue;
            }

            if (lugar == CapabilityParameterLocation.Body && c is '\n' or '\t')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>O padrão é do administrador (validado no cadastro), mas roda sempre em modo não retrocedente e com tempo limite: nenhuma entrada trava o motor.</summary>
    internal static Regex CompilePattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));
}
