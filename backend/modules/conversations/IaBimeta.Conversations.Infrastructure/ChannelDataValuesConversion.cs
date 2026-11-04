using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// Como os pares chave/valor do bloco de metadados do canal (<c>ChannelData</c>) viram o texto JSON da coluna <c>messages.channel_data</c> (jsonb) e voltam. O
/// Conversations não interpreta nenhum par: o que sai é exatamente o que entrou, em chaves e valores de texto. As chaves saem em ordem ordinal, então o texto é
/// determinístico (o jsonb, de todo modo, não guarda a ordem). Os valores já passaram pela validação do domínio (sem caractere de controle e sem substituto UTF-16
/// sem par), o que os torna sempre graváveis como JSON.
/// </summary>
internal static class ChannelDataJson
{
    public static string Serialize(IReadOnlyDictionary<string, string>? valores)
    {
        // O EF nunca chama o conversor com nulo (coluna nula fica nula): nulo aqui é defeito de quem chama.
        ArgumentNullException.ThrowIfNull(valores);

        var ordenado = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (chave, valor) in valores)
        {
            ordenado.Add(chave, valor);
        }

        return JsonSerializer.Serialize(ordenado);
    }

    public static IReadOnlyDictionary<string, string> Deserialize(string json)
    {
        var pares = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException("O bloco de metadados do canal guardado não é um objeto JSON de texto para texto.");

        // Um valor nulo dentro do objeto não é texto: o JSON o aceitaria, mas o contrato (e a validação de entrada) nunca o produzem. Falhar alto aqui é preferível a devolver um
        // dicionário com nulo para quem o lê.
        if (pares.Values.Any(valor => valor is null))
        {
            throw new InvalidOperationException("O bloco de metadados do canal guardado tem um valor nulo: só texto é válido.");
        }

        return new ReadOnlyDictionary<string, string>(pares);
    }

    /// <summary>Mesmos pares, com chaves comparadas e valores comparados de forma ordinal (nulo só é igual a nulo).</summary>
    public static bool AreEqual(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var (chave, valor) in a)
        {
            if (!b.TryGetValue(chave, out var outro) || !string.Equals(valor, outro, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Um código que não depende da ordem dos pares (dois dicionários iguais têm o mesmo código).</summary>
    public static int HashOf(IReadOnlyDictionary<string, string>? valores)
    {
        if (valores is null)
        {
            return 0;
        }

        var codigo = 0;
        foreach (var (chave, valor) in valores)
        {
            codigo ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(chave), StringComparer.Ordinal.GetHashCode(valor));
        }

        return codigo;
    }

    /// <summary>A cópia que o rastreador de mudanças guarda para comparar depois.</summary>
    public static IReadOnlyDictionary<string, string>? Copy(IReadOnlyDictionary<string, string>? valores) =>
        valores is null ? null : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(valores, StringComparer.Ordinal));
}

/// <summary>O conversor dos pares do <c>ChannelData</c> para o texto JSON da coluna jsonb, e de volta.</summary>
internal sealed class ChannelDataValuesConverter()
    : ValueConverter<IReadOnlyDictionary<string, string>?, string>(valores => ChannelDataJson.Serialize(valores), json => ChannelDataJson.Deserialize(json));

/// <summary>A comparação por conteúdo dos pares (um dicionário não tem igualdade por valor), para o rastreador de mudanças do EF.</summary>
internal sealed class ChannelDataValuesComparer()
    : ValueComparer<IReadOnlyDictionary<string, string>?>(
        (a, b) => ChannelDataJson.AreEqual(a, b),
        valores => ChannelDataJson.HashOf(valores),
        valores => ChannelDataJson.Copy(valores));
