using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IaBimeta.Capabilities.Contracts;

namespace IaBimeta.Capabilities.Domain;

/// <summary>O JSON do molde de uma operação (guardado como texto; enums como texto). A leitura NÃO confia no que está guardado: o molde é revalidado ao carregar.</summary>
public static class CapabilitySpecJson
{
    public const int MaxLength = 16_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(CapabilityOperationSpec spec) => JsonSerializer.Serialize(spec, Options);

    public static CapabilityOperationSpec Deserialize(string json) =>
        JsonSerializer.Deserialize<CapabilityOperationSpec>(json, Options) ?? throw new JsonException("Molde de operação vazio.");
}

/// <summary>
/// As regras do molde de uma operação, conferidas no cadastro e de novo na publicação (ADR 0035). O molde é a ÚNICA origem do que sai na chamada: método, caminho, parâmetros (e onde cada um vai), cabeçalhos,
/// idempotência e mapeamento da resposta. Devolve frases fixas com o nome do campo; vazio = válido.
/// </summary>
public static partial class OperationSpecRules
{
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public const int MaxParameters = 20;
    public const int MaxResponseFields = 40;

    // Cabeçalhos que a operação nunca pode definir: identidade, enquadramento HTTP, proxy e roteamento. A autenticação vem da CONEXÃO, nunca de parâmetro.
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "authorization", "proxy-authorization", "cookie", "set-cookie", "content-length", "content-type", "content-encoding", "transfer-encoding",
        "connection", "keep-alive", "upgrade", "expect", "te", "trailer", "via", "forwarded", "x-real-ip", "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto",
        "x-http-method-override", "origin", "referer", "accept-encoding", "user-agent",
    };

    public static bool IsForbiddenHeader(string name) =>
        ForbiddenHeaders.Contains(name) || name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    internal static partial Regex HeaderNameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]{0,39}(\.[A-Za-z_][A-Za-z0-9_-]{0,39}){0,4}$")]
    private static partial Regex BodyPathRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex WireNameRegex();

    [GeneratedRegex(@"^/[A-Za-z0-9/_\-.~:@{}]*$")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"^(/([^/~]|~[01])*)*$")]
    private static partial Regex PointerRegex();

    public static List<string> Validate(CapabilityOperationSpec spec, CapabilityEffect effect, bool requiresConfirmation, IReadOnlyList<string> allowedHeaders)
    {
        var problemas = new List<string>();
        if (spec is null)
        {
            return ["spec: obrigatório."];
        }

        if (!Methods.Contains(spec.Method, StringComparer.Ordinal))
        {
            problemas.Add("spec.method: use GET, POST, PUT, PATCH ou DELETE (maiúsculas).");
        }

        var metodo = spec.Method;
        if (metodo == "GET" && effect != CapabilityEffect.Read)
        {
            problemas.Add("effect: GET é sempre leitura.");
        }

        if (metodo is "PUT" or "PATCH" or "DELETE" && effect != CapabilityEffect.Write)
        {
            problemas.Add("effect: PUT, PATCH e DELETE são sempre escrita.");
        }

        var parametros = spec.Parameters ?? [];
        if (parametros.Count > MaxParameters)
        {
            problemas.Add($"spec.parameters: no máximo {MaxParameters}.");
        }

        var nomes = new HashSet<string>(StringComparer.Ordinal);
        var pathParams = new List<string>();
        var wires = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parametros)
        {
            if (p is null || !NameRegex().IsMatch(p.Name ?? string.Empty))
            {
                problemas.Add("spec.parameters: nome inválido (letra seguida de letras, números ou _; até 40).");
                continue;
            }

            if (!nomes.Add(p.Name!))
            {
                problemas.Add($"spec.parameters.{p.Name}: nome repetido.");
            }

            var wire = p.WireName ?? p.Name!;
            var wireOk = p.Location == CapabilityParameterLocation.Body ? BodyPathRegex().IsMatch(wire) : WireNameRegex().IsMatch(wire);
            if (!wireOk)
            {
                problemas.Add($"spec.parameters.{p.Name}.wireName: inválido.");
            }

            if (!Enum.IsDefined(p.Type) || !Enum.IsDefined(p.Location))
            {
                problemas.Add($"spec.parameters.{p.Name}: tipo ou lugar inválido.");
                continue;
            }

            switch (p.Location)
            {
                case CapabilityParameterLocation.Path:
                    pathParams.Add(p.Name!);
                    if (!p.Required)
                    {
                        problemas.Add($"spec.parameters.{p.Name}: parâmetro de caminho é obrigatório.");
                    }

                    break;
                case CapabilityParameterLocation.Header:
                    if (!HeaderNameRegex().IsMatch(wire) || IsForbiddenHeader(wire) || !allowedHeaders.Contains(wire, StringComparer.OrdinalIgnoreCase))
                    {
                        problemas.Add($"spec.parameters.{p.Name}: cabeçalho não permitido (precisa constar nos cabeçalhos permitidos da conexão e não ser reservado).");
                    }

                    break;
                case CapabilityParameterLocation.Body:
                    if (metodo is "GET" or "DELETE")
                    {
                        problemas.Add($"spec.parameters.{p.Name}: {metodo} não tem corpo.");
                    }

                    break;
            }

            if (p.Location != CapabilityParameterLocation.Body && !wires.Add($"{p.Location}:{wire}"))
            {
                problemas.Add($"spec.parameters.{p.Name}: nome externo repetido.");
            }

            ValidateParameterRules(p, problemas);
        }

        ValidatePath(spec.PathTemplate, pathParams, problemas);
        ValidateResponse(spec, problemas);
        ValidateIdempotency(spec, effect, requiresConfirmation, problemas);

        if (spec.MaxAttempts is < 1 or > 3)
        {
            problemas.Add("spec.maxAttempts: de 1 a 3.");
        }
        else if (spec.MaxAttempts > 1 && effect == CapabilityEffect.Write && spec.Idempotency != IdempotencySupport.Proven)
        {
            problemas.Add("spec.maxAttempts: retentativa de escrita só com idempotência comprovada (Proven).");
        }

        // Os padrões e as faixas valem também para o padrão (Default) declarado.
        if (problemas.Count == 0)
        {
            var vazio = new Dictionary<string, JsonElement>();
            var verificacao = ParameterValidator.Validate(parametros, vazio);
            foreach (var problema in verificacao.Problems.Where(x => x.Rule != "required"))
            {
                problemas.Add($"spec.parameters.{problema.Parameter}: o valor padrão não respeita as regras do parâmetro.");
            }
        }

        return problemas;
    }

    private static void ValidateParameterRules(CapabilityParameterSpec p, List<string> problemas)
    {
        if (p.MaxLength is { } max && (max < 1 || max > ParameterValidator.HardMaxLength))
        {
            problemas.Add($"spec.parameters.{p.Name}.maxLength: de 1 a {ParameterValidator.HardMaxLength}.");
        }

        if (p.Min is { } min && p.Max is { } maxv && min > maxv)
        {
            problemas.Add($"spec.parameters.{p.Name}: min maior que max.");
        }

        if (p.Pattern is not null)
        {
            if (p.Type != CapabilityParameterType.String || p.Pattern.Length > 200)
            {
                problemas.Add($"spec.parameters.{p.Name}.pattern: só para texto, até 200 caracteres.");
            }
            else
            {
                try
                {
                    ParameterValidator.CompilePattern(p.Pattern);
                }
                catch (ArgumentException)
                {
                    problemas.Add($"spec.parameters.{p.Name}.pattern: expressão inválida.");
                }
            }
        }

        if (p.Type == CapabilityParameterType.Enum)
        {
            if (p.EnumValues is not { Count: > 0 and <= 50 } valores || valores.Any(v => string.IsNullOrEmpty(v) || v.Length > 100 || v.Any(char.IsControl)))
            {
                problemas.Add($"spec.parameters.{p.Name}.enumValues: de 1 a 50 valores de até 100 caracteres, sem controle.");
            }
        }
        else if (p.EnumValues is { Count: > 0 })
        {
            problemas.Add($"spec.parameters.{p.Name}.enumValues: só para o tipo Enum.");
        }
    }

    private static void ValidatePath(string? template, List<string> pathParams, List<string> problemas)
    {
        if (string.IsNullOrEmpty(template) || template.Length > 300 || !PathRegex().IsMatch(template) || template.Contains("..", StringComparison.Ordinal) || template.Contains("//", StringComparison.Ordinal))
        {
            problemas.Add("spec.pathTemplate: precisa começar com / e usar só letras, números e / _ - . ~ : @ {nome}; até 300.");
            return;
        }

        var marcados = Regex.Matches(template, @"\{([^{}]*)\}").Select(m => m.Groups[1].Value).ToList();
        var semChaves = Regex.Replace(template, @"\{[^{}]*\}", string.Empty);
        if (semChaves.Contains('{') || semChaves.Contains('}'))
        {
            problemas.Add("spec.pathTemplate: chaves desbalanceadas.");
            return;
        }

        foreach (var marcado in marcados)
        {
            if (!pathParams.Contains(marcado, StringComparer.Ordinal))
            {
                problemas.Add("spec.pathTemplate: há um marcador sem parâmetro de caminho declarado.");
            }
        }

        foreach (var parametro in pathParams)
        {
            if (marcados.Count(m => m == parametro) != 1)
            {
                problemas.Add($"spec.pathTemplate: o parâmetro de caminho {parametro} precisa aparecer exatamente uma vez.");
            }
        }
    }

    private static void ValidateResponse(CapabilityOperationSpec spec, List<string> problemas)
    {
        var campos = spec.Response ?? [];
        if (campos.Count > MaxResponseFields)
        {
            problemas.Add($"spec.response: no máximo {MaxResponseFields} campos.");
        }

        if (spec.ResponseRoot is not null && (spec.ResponseRoot.Length > 200 || !PointerRegex().IsMatch(spec.ResponseRoot)))
        {
            problemas.Add("spec.responseRoot: ponteiro JSON inválido.");
        }

        if (spec.MaxItems is < 1 or > 100)
        {
            problemas.Add("spec.maxItems: de 1 a 100.");
        }

        var nomes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var campo in campos)
        {
            if (campo is null || !NameRegex().IsMatch(campo.Name ?? string.Empty))
            {
                problemas.Add("spec.response: nome de campo inválido.");
                continue;
            }

            if (!nomes.Add(campo.Name!) || campo.Name is "items" or "count" or "truncated" && spec.ResponseRoot is not null)
            {
                problemas.Add($"spec.response.{campo.Name}: nome repetido ou reservado.");
            }

            if (campo.Pointer is null || campo.Pointer.Length > 200 || !PointerRegex().IsMatch(campo.Pointer))
            {
                problemas.Add($"spec.response.{campo.Name}.pointer: ponteiro JSON inválido.");
            }

            if (!Enum.IsDefined(campo.Type) || (campo.MaxLength is { } m && (m < 1 || m > ResponseMapper.HardFieldLength)))
            {
                problemas.Add($"spec.response.{campo.Name}: tipo ou tamanho inválido.");
            }
        }
    }

    private static void ValidateIdempotency(CapabilityOperationSpec spec, CapabilityEffect effect, bool requiresConfirmation, List<string> problemas)
    {
        if (!Enum.IsDefined(spec.Idempotency))
        {
            problemas.Add("spec.idempotency: valor inválido.");
            return;
        }

        if (spec.IdempotencyHeader is not null && (!HeaderNameRegex().IsMatch(spec.IdempotencyHeader) || IsForbiddenHeader(spec.IdempotencyHeader)))
        {
            problemas.Add("spec.idempotencyHeader: cabeçalho inválido ou reservado.");
        }

        if (effect == CapabilityEffect.Write && spec.Idempotency == IdempotencySupport.Unspecified)
        {
            problemas.Add("spec.idempotency: escrita precisa declarar o suporte (Proven, Reconcilable ou None).");
        }

        if (effect == CapabilityEffect.Read)
        {
            if (spec.IdempotencyHeader is not null)
            {
                problemas.Add("spec.idempotencyHeader: leitura não usa chave de idempotência.");
            }

            return;
        }

        if (spec.Idempotency == IdempotencySupport.Proven && spec.IdempotencyHeader is null && spec.Method is not ("PUT" or "DELETE"))
        {
            problemas.Add("spec.idempotency: Proven em POST ou PATCH exige o cabeçalho de idempotência do sistema externo (PUT e DELETE são naturalmente idempotentes).");
        }

        if (!requiresConfirmation && spec.Idempotency != IdempotencySupport.Proven)
        {
            problemas.Add("requiresConfirmation: dispensar a confirmação só é permitido para escrita com idempotência comprovada.");
        }
    }
}
