using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Token opaco de sessão: 32 bytes aleatórios do gerador criptográfico do sistema, em base64url. O token só existe na resposta do login (ou da troca do ticket); no banco fica só o SHA-256 dele. Como o token tem 256 bits de
/// entropia, o hash rápido basta (não há o que adivinhar por força bruta).
/// Dois formatos, que NUNCA se confundem (ADR 0039, E1-R): a sessão interativa (<c>ibs_</c>, hash do token) e a sessão EMBUTIDA (<c>ibe_</c>, hash do token com separação de domínio). Uma versão antiga da API, que só
/// conhece <c>ibs_</c> e o hash simples, nunca acha nem aceita um token embutido (formato diferente e hash que não confere), e a versão nova só aceita cada formato para o tipo de sessão correspondente.
/// </summary>
public static class SessionTokens
{
    public const string Prefix = "ibs_";

    /// <summary>O prefixo do token da sessão embutida.</summary>
    public const string EmbeddedPrefix = "ibe_";

    private const string EmbeddedDomain = "iab-embedded-session-v1\n";
    private const int RandomByteCount = 32;
    private const int EncodedLength = 43; // 32 bytes em base64url, sem preenchimento

    public static (string Token, byte[] Hash) Generate()
    {
        var aleatorio = RandomNumberGenerator.GetBytes(RandomByteCount);
        var token = Prefix + Base64Url.EncodeToString(aleatorio);
        return (token, HashOf(token));
    }

    /// <summary>Um token novo de sessão embutida (<c>ibe_</c>) e o hash que fica no banco.</summary>
    public static (string Token, byte[] Hash) GenerateEmbedded()
    {
        var aleatorio = RandomNumberGenerator.GetBytes(RandomByteCount);
        var token = EmbeddedPrefix + Base64Url.EncodeToString(aleatorio);
        return (token, HashOfEmbedded(token));
    }

    /// <summary>
    /// Calcula o hash de um token apresentado. Devolve <c>false</c> sem tocar o banco quando o formato não é o de um token nosso (prefixo, tamanho e alfabeto), o que descarta lixo e tentativas grosseiras.
    /// <paramref name="embedded"/> diz qual dos dois formatos era.
    /// </summary>
    public static bool TryHash(string? token, out byte[] hash, out bool embedded)
    {
        hash = Array.Empty<byte>();
        embedded = false;

        if (token is null || token.Length != Prefix.Length + EncodedLength)
        {
            return false;
        }

        var interativo = token.StartsWith(Prefix, StringComparison.Ordinal);
        var embutido = token.StartsWith(EmbeddedPrefix, StringComparison.Ordinal);
        if (!interativo && !embutido)
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

        embedded = embutido;
        hash = embutido ? HashOfEmbedded(token) : HashOf(token);
        return true;
    }

    public static bool TryHash(string? token, out byte[] hash) => TryHash(token, out hash, out _);

    private static byte[] HashOf(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static byte[] HashOfEmbedded(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddedDomain + token));
}
