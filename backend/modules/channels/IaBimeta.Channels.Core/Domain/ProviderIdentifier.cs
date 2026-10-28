namespace IaBimeta.Channels.Domain;

/// <summary>
/// Identificador de um recurso no provedor (WABA ID, Phone Number ID). O formato exato de cada um (por exemplo, se é só
/// numérico) depende da documentação oficial do provedor e será confirmado antes de implementar o Receiver e o Sender
/// (BOUNDARIES.md PRV-2). Até lá a regra é a mais frouxa que ainda é segura: texto curto, sem espaço nem caractere de
/// controle, só letras, números e <c>_ . : -</c>, o que evita injeção em log, rota e chave de índice.
/// </summary>
public static class ProviderIdentifier
{
    public const int MaxLength = 64;

    /// <summary>Normaliza (tira os espaços das pontas) e valida.</summary>
    /// <exception cref="ChannelsValidationException">O identificador é vazio, longo demais ou tem caractere fora do conjunto aceito.</exception>
    public static string Normalize(string? value, string description)
    {
        var texto = (value ?? string.Empty).Trim();
        if (!IsValid(texto))
        {
            throw new ChannelsValidationException($"O identificador {description} deve ter de 1 a {MaxLength} caracteres, só letras, números e _ . : -.");
        }

        return texto;
    }

    /// <summary>Verdadeiro se o texto, exatamente como veio, é um identificador válido (sem normalizar).</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var aceito = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or ':' or '-';
            if (!aceito)
            {
                return false;
            }
        }

        return true;
    }
}
