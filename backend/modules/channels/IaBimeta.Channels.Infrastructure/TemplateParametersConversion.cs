using System.Text.Json;
using IaBimeta.Channels.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>O texto JSON da coluna jsonb dos parâmetros de um modelo, e a lista de volta. A estrutura nunca carrega segredo: só chave, componente, índice, nome, exemplo e tamanho.</summary>
internal static class TemplateParametersJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<TemplateParameterSpec>? parametros) =>
        JsonSerializer.Serialize(parametros ?? Array.Empty<TemplateParameterSpec>(), Options);

    public static IReadOnlyList<TemplateParameterSpec> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? Array.Empty<TemplateParameterSpec>() : JsonSerializer.Deserialize<List<TemplateParameterSpec>>(json, Options) ?? [];

    public static bool AreEqual(IReadOnlyList<TemplateParameterSpec>? a, IReadOnlyList<TemplateParameterSpec>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));

    public static int HashOf(IReadOnlyList<TemplateParameterSpec>? valores)
    {
        var codigo = new HashCode();
        foreach (var v in valores ?? Array.Empty<TemplateParameterSpec>())
        {
            codigo.Add(v);
        }

        return codigo.ToHashCode();
    }

    public static IReadOnlyList<TemplateParameterSpec> Copy(IReadOnlyList<TemplateParameterSpec>? valores) =>
        valores is null ? Array.Empty<TemplateParameterSpec>() : valores.ToList();
}

internal sealed class TemplateParametersConverter()
    : ValueConverter<IReadOnlyList<TemplateParameterSpec>, string>(valores => TemplateParametersJson.Serialize(valores), json => TemplateParametersJson.Deserialize(json));

internal sealed class TemplateParametersComparer()
    : ValueComparer<IReadOnlyList<TemplateParameterSpec>>(
        (a, b) => TemplateParametersJson.AreEqual(a, b),
        valores => TemplateParametersJson.HashOf(valores),
        valores => TemplateParametersJson.Copy(valores));
