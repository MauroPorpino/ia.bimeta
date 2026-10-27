using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Token opaco de sessão: 32 bytes aleatórios do gerador criptográfico do sistema, em base64url, com o prefixo
/// <c>ibs_</c> (facilita varreduras de segredo). O token só existe na resposta do login; no banco fica só o SHA-256
/// dele. Como o token tem 256 bits de entropia, o hash rápido basta (não há o que adivinhar por força bruta).
/// </summary>
public static class SessionTokens
{
    public const string Prefix = "ibs_";
    private const int RandomByteCount = 32;
    private const int EncodedLength = 43; // 32 bytes em base64url, sem preenchimento

    public static (string Token, byte[] Hash) Generate()
    {
        var aleatorio = RandomNumberGenerator.GetBytes(RandomByteCount);
        var token = Prefix + Base64Url.EncodeToString(aleatorio);
        return (token, HashOf(token));
    }

    /// <summary>
    /// Calcula o hash de um token apresentado. Devolve <c>false</c> sem tocar o banco quando o formato não é o de um
    /// token nosso (prefixo, tamanho e alfabeto), o que descarta lixo e tentativas grosseiras.
    /// </summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = Array.Empty<byte>();

        if (token is null || token.Length != Prefix.Length + EncodedLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in token.AsSpan(Prefix.Length))
        {
            var valido = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!valido)
            {
                return false;
            }
        }

        hash = HashOf(token);
        return true;
    }

    private static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
