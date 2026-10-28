using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IaBimeta.SharedKernel;

/// <summary>
/// Identificador de idioma BCP 47 (RFC 5646), por exemplo <c>pt-BR</c>, <c>en-US</c>, <c>es-ES</c> ou <c>fr-FR</c>. Só
/// aceita tags <b>bem formadas</b> (idioma, escrita, região, variantes, extensões e uso privado, na ordem da gramática) e
/// as guarda na forma canônica de maiúsculas e minúsculas (<c>pt-br</c> vira <c>pt-BR</c>). Não consulta o registro da
/// IANA: a tag é sintaxe, não garantia de que o idioma existe. Não há <c>extlang</c> nem tags legadas (<c>i-klingon</c>).
/// Underscore (<c>pt_BR</c>, comum em provedores) é recusado: quem adapta o provedor converte na borda.
/// Não tem regra de negócio: serve a Tenancy, Identity e Conversations sem que um dependa do outro.
/// </summary>
[JsonConverter(typeof(LocaleJsonConverter))]
public sealed class Locale : IEquatable<Locale>
{
    /// <summary>O tamanho que a RFC 5646 recomenda que toda implementação aceite.</summary>
    public const int MaxLength = 35;

    private readonly string _value;

    private Locale(string value)
    {
        _value = value;
    }

    /// <summary>Lê e normaliza. Espaços nas pontas são ignorados.</summary>
    /// <exception cref="FormatException">O texto não é uma tag BCP 47 bem formada.</exception>
    public static Locale Parse(string? text) =>
        TryParse(text, out var locale)
            ? locale
            : throw new FormatException("O idioma deve ser um identificador BCP 47 bem formado, como pt-BR, en-US, es-ES ou fr-FR.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out Locale? locale)
    {
        locale = null;
        var valor = text?.Trim();
        if (string.IsNullOrEmpty(valor) || valor.Length > MaxLength)
        {
            return false;
        }

        var partes = valor.Split('-');
        var canonico = new List<string>(partes.Length);
        var i = 0;

        // Idioma: 2 ou 3 letras, ou 5 a 8 (registrado). Quatro letras são reservadas.
        var idioma = partes[i];
        if (!IsAlpha(idioma) || idioma.Length is 1 or 4 or > 8)
        {
            return false;
        }

        canonico.Add(idioma.ToLowerInvariant());
        i++;

        // Escrita: 4 letras, em Titlecase (Latn).
        if (i < partes.Length && partes[i].Length == 4 && IsAlpha(partes[i]))
        {
            canonico.Add(char.ToUpperInvariant(partes[i][0]) + partes[i][1..].ToLowerInvariant());
            i++;
        }

        // Região: 2 letras maiúsculas (BR) ou 3 dígitos (419).
        if (i < partes.Length && ((partes[i].Length == 2 && IsAlpha(partes[i])) || (partes[i].Length == 3 && IsDigits(partes[i]))))
        {
            canonico.Add(partes[i].ToUpperInvariant());
            i++;
        }

        // Variantes: 5 a 8 alfanuméricos, ou 4 começando por dígito. Sem repetição.
        var variantes = new HashSet<string>(StringComparer.Ordinal);
        while (i < partes.Length && IsVariant(partes[i]))
        {
            var variante = partes[i].ToLowerInvariant();
            if (!variantes.Add(variante))
            {
                return false;
            }

            canonico.Add(variante);
            i++;
        }

        // Extensões: um caractere (exceto x), seguido de um ou mais subtags de 2 a 8 alfanuméricos. Sem repetir o caractere.
        var extensoes = new HashSet<char>();
        while (i < partes.Length && partes[i].Length == 1 && IsAlphanumeric(partes[i]) && !IsPrivateUseSingleton(partes[i]))
        {
            var singleton = char.ToLowerInvariant(partes[i][0]);
            if (!extensoes.Add(singleton))
            {
                return false;
            }

            canonico.Add(singleton.ToString());
            i++;

            var contagem = 0;
            while (i < partes.Length && partes[i].Length is >= 2 and <= 8 && IsAlphanumeric(partes[i]))
            {
                canonico.Add(partes[i].ToLowerInvariant());
                i++;
                contagem++;
            }

            if (contagem == 0)
            {
                return false;
            }
        }

        // Uso privado: x seguido de um ou mais subtags de 1 a 8 alfanuméricos.
        if (i < partes.Length && IsPrivateUseSingleton(partes[i]))
        {
            canonico.Add("x");
            i++;

            var contagem = 0;
            while (i < partes.Length && partes[i].Length is >= 1 and <= 8 && IsAlphanumeric(partes[i]))
            {
                canonico.Add(partes[i].ToLowerInvariant());
                i++;
                contagem++;
            }

            if (contagem == 0)
            {
                return false;
            }
        }

        if (i != partes.Length)
        {
            return false;
        }

        locale = new Locale(string.Join('-', canonico));
        return true;
    }

    public bool Equals(Locale? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Locale outro && Equals(outro);

    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);

    /// <summary>A forma canônica (<c>pt-BR</c>).</summary>
    public override string ToString() => _value;

    public static bool operator ==(Locale? a, Locale? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Locale? a, Locale? b) => !(a == b);

    private static bool IsAlpha(string texto) => texto.Length > 0 && texto.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'));

    private static bool IsDigits(string texto) => texto.Length > 0 && texto.All(c => c is >= '0' and <= '9');

    private static bool IsAlphanumeric(string texto) => texto.Length > 0 && texto.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'));

    private static bool IsVariant(string texto) =>
        IsAlphanumeric(texto) && (texto.Length is >= 5 and <= 8 || (texto.Length == 4 && char.IsAsciiDigit(texto[0])));

    private static bool IsPrivateUseSingleton(string texto) => texto.Length == 1 && (texto[0] is 'x' or 'X');
}

/// <summary>Lê e escreve o idioma como texto (<c>"pt-BR"</c>). Texto que não é BCP 47 bem formado recusa a leitura (HTTP 400).</summary>
public sealed class LocaleJsonConverter : JsonConverter<Locale>
{
    public override Locale? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Locale.TryParse(reader.GetString(), out var locale))
        {
            throw new JsonException("O idioma deve ser um identificador BCP 47 bem formado, como pt-BR, en-US, es-ES ou fr-FR.");
        }

        return locale;
    }

    public override void Write(Utf8JsonWriter writer, Locale value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
