using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Ticket opaco de uso único do Embedded (ADR 0039): 32 bytes aleatórios em base64url com o prefixo <c>ibt_</c>. Só o SHA-256 vai ao banco. Como o ticket tem 256 bits de entropia, o hash rápido basta.
/// </summary>
public static class TicketTokens
{
    public const string Prefix = "ibt_";
    private const int RandomByteCount = 32;
    private const int EncodedLength = 43;

    public static (string Token, byte[] Hash) Generate()
    {
        var token = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomByteCount));
        return (token, HashOf(token));
    }

    /// <summary>Devolve <c>false</c> sem tocar o banco quando o formato não é o de um ticket nosso.</summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = Array.Empty<byte>();
        if (token is null || token.Length != Prefix.Length + EncodedLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in token.AsSpan(Prefix.Length))
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        hash = HashOf(token);
        return true;
    }

    private static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>
/// O par verifier/challenge no estilo PKCE (S256): o iframe gera o <em>verifier</em> (43 a 128 caracteres do conjunto não reservado) e fica com ele em memória; o <em>challenge</em> (base64url do SHA-256 do verifier)
/// é o que o ERP vê e o ticket guarda. Quem tem o ticket mas não o verifier não consegue trocá-lo.
/// </summary>
public static class PkceVerifier
{
    public const int MinVerifierLength = 43;
    public const int MaxVerifierLength = 128;

    public static bool IsValidVerifier(string? verifier)
    {
        if (verifier is null || verifier.Length is < MinVerifierLength or > MaxVerifierLength)
        {
            return false;
        }

        foreach (var c in verifier)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidChallenge(string? challenge)
    {
        if (challenge is not { Length: 43 })
        {
            return false;
        }

        foreach (var c in challenge)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static string ComputeChallenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Compara em tempo constante o challenge guardado com o calculado a partir do verifier apresentado.</summary>
    public static bool Matches(string storedChallenge, string verifier) =>
        IsValidVerifier(verifier)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedChallenge), Encoding.ASCII.GetBytes(ComputeChallenge(verifier)));
}
