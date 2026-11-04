using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// O esquema da saída estruturada precisa ser o MÍNIMO COMUM aos provedores (ADR 0017, seção C.5): objeto na raiz, <c>additionalProperties: false</c>,
/// todos os campos obrigatórios (nenhum opcional), sem <c>anyOf</c> na raiz e sem <c>minimum</c>, <c>maximum</c>, <c>minLength</c> nem <c>maxLength</c> em
/// lugar algum. Um esquema que foge disso funcionaria num provedor e falharia no outro; o AiGateway o recusa antes de qualquer chamada.
/// </summary>
public static class AiStructuredOutputRules
{
    public const int NameMaxLength = 64;
    public const int SchemaMaxLength = 64_000;

    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ForbiddenKeywords = { "minimum", "maximum", "minLength", "maxLength" };

    /// <summary>O problema do esquema, ou nulo quando ele obedece ao mínimo comum.</summary>
    public static string? FindProblem(AiStructuredOutput? output)
    {
        if (output is null)
        {
            return "A saída estruturada exige o esquema.";
        }

        if (output.Name is null || !NamePattern.IsMatch(output.Name))
        {
            return $"O nome do esquema deve ter de 1 a {NameMaxLength} caracteres: letras, números, '_' e '-'.";
        }

        if (string.IsNullOrWhiteSpace(output.JsonSchema) || output.JsonSchema.Length > SchemaMaxLength)
        {
            return $"O esquema deve ter de 1 a {SchemaMaxLength} caracteres.";
        }

        try
        {
            using var documento = JsonDocument.Parse(output.JsonSchema, new JsonDocumentOptions { MaxDepth = 32 });
            return FindProblem(documento.RootElement);
        }
        catch (JsonException)
        {
            return "O esquema não é um JSON válido.";
        }
    }

    private static string? FindProblem(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object
            || !raiz.TryGetProperty("type", out var tipo)
            || tipo.ValueKind != JsonValueKind.String
            || tipo.GetString() != "object")
        {
            return "A raiz do esquema deve ser um objeto (\"type\": \"object\").";
        }

        if (raiz.TryGetProperty("anyOf", out _))
        {
            return "A raiz do esquema não pode usar 'anyOf'.";
        }

        if (!raiz.TryGetProperty("additionalProperties", out var adicionais) || adicionais.ValueKind != JsonValueKind.False)
        {
            return "A raiz do esquema deve declarar \"additionalProperties\": false.";
        }

        if (!raiz.TryGetProperty("properties", out var propriedades) || propriedades.ValueKind != JsonValueKind.Object)
        {
            return "A raiz do esquema deve declarar 'properties'.";
        }

        var obrigatorios = new HashSet<string>(StringComparer.Ordinal);
        if (raiz.TryGetProperty("required", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in lista.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    obrigatorios.Add(item.GetString()!);
                }
            }
        }

        foreach (var propriedade in propriedades.EnumerateObject())
        {
            if (!obrigatorios.Contains(propriedade.Name))
            {
                return $"O campo '{propriedade.Name}' deve ser obrigatório: o esquema não pode ter campo opcional.";
            }
        }

        return FindForbiddenKeyword(raiz, namesOfProperties: false);
    }

    // As chaves do mapa "properties" são NOMES de campo e não palavras-chave: um campo chamado "minimum" não é um limite.
    private static string? FindForbiddenKeyword(JsonElement elemento, bool namesOfProperties)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var membro in elemento.EnumerateObject())
                {
                    if (!namesOfProperties && Array.IndexOf(ForbiddenKeywords, membro.Name) >= 0)
                    {
                        return $"O esquema não pode usar '{membro.Name}'.";
                    }

                    var problema = FindForbiddenKeyword(membro.Value, namesOfProperties: !namesOfProperties && membro.Name == "properties");
                    if (problema is not null)
                    {
                        return problema;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    var problema = FindForbiddenKeyword(item, namesOfProperties: false);
                    if (problema is not null)
                    {
                        return problema;
                    }
                }

                return null;

            default:
                return null;
        }
    }
}

/// <summary>
/// As regras de FORMA do pedido de geração, aplicadas antes de qualquer acesso a configuração, credencial ou provedor. O que fere uma regra é erro de
/// quem chamou (<see cref="AiGatewayValidationException"/>), não falha do provedor: nada é chamado nem contabilizado.
/// </summary>
public static class AiGenerationRequestRules
{
    public const int CorrelationIdMaxLength = 100;

    /// <exception cref="AiGatewayValidationException">O pedido não obedece à forma.</exception>
    public static void Validate(AiGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CorrelationId) || request.CorrelationId.Length > CorrelationIdMaxLength)
        {
            throw new AiGatewayValidationException($"A correlação deve ter de 1 a {CorrelationIdMaxLength} caracteres.");
        }

        if (string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            throw new AiGatewayValidationException("A instrução de sistema é obrigatória.");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new AiGatewayValidationException("O pedido precisa de ao menos uma mensagem.");
        }

        foreach (var mensagem in request.Messages)
        {
            if (mensagem is null || !Enum.IsDefined(mensagem.Role) || string.IsNullOrWhiteSpace(mensagem.Text))
            {
                throw new AiGatewayValidationException("Cada mensagem precisa de um papel válido e de texto.");
            }
        }

        if (request.Messages[^1].Role != AiMessageRole.User)
        {
            throw new AiGatewayValidationException("A última mensagem do pedido deve ser do usuário.");
        }

        if (request.MaxOutputTokens <= 0)
        {
            throw new AiGatewayValidationException("O teto de tokens de saída deve ser positivo.");
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new AiGatewayValidationException("O tempo limite deve ser positivo.");
        }

        switch (request.OutputMode)
        {
            case AiOutputMode.Text:
                if (request.StructuredOutput is not null)
                {
                    throw new AiGatewayValidationException("O esquema só vale com a saída estruturada.");
                }

                break;

            case AiOutputMode.StructuredJson:
                if (AiStructuredOutputRules.FindProblem(request.StructuredOutput) is { } problema)
                {
                    throw new AiGatewayValidationException(problema);
                }

                break;

            default:
                throw new AiGatewayValidationException("O modo de saída é desconhecido.");
        }
    }
}
