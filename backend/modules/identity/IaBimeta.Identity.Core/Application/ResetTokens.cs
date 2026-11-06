using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Token opaco de redefinição de senha: 32 bytes aleatórios do gerador criptográfico, em base64url, com o prefixo <c>ibr_</c> (distinto do <c>ibs_</c> da sessão, e fácil de achar em varredura de segredo). Existe em claro só na
/// resposta da emissão; no banco fica só o SHA-256. Com 256 bits de entropia, o hash rápido basta.
/// </summary>
public static class ResetTokens
{
    public const string Prefix = "ibr_";
    private const int RandomByteCount = 32;
    private const int EncodedLength = 43;

    public static (string Token, byte[] Hash) Generate()
    {
        var aleatorio = RandomNumberGenerator.GetBytes(RandomByteCount);
        var token = Prefix + Base64Url.EncodeToString(aleatorio);
        return (token, HashOf(token));
    }

    /// <summary>O hash de um token apresentado. <c>false</c>, sem tocar o banco, quando o formato não é o de um token nosso (descarta lixo e tentativas grosseiras).</summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = Array.Empty<byte>();
        var texto = token?.Trim();

        if (texto is null || texto.Length != Prefix.Length + EncodedLength || !texto.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in texto.AsSpan(Prefix.Length))
        {
            var valido = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!valido)
            {
                return false;
            }
        }

        hash = HashOf(texto);
        return true;
    }

    private static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
