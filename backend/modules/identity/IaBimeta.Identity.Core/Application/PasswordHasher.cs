using System.Security.Cryptography;

namespace IaBimeta.Identity.Application;

public enum PasswordVerification
{
    Failed = 0,
    Success = 1,

    /// <summary>A senha confere, mas o hash foi gerado com parâmetros mais fracos que os atuais: deve ser regravado.</summary>
    SuccessRehashNeeded = 2,
}

/// <summary>
/// Hash de senha com PBKDF2-HMAC-SHA256 do próprio .NET, com salt individual de 16 bytes. O formato guarda os
/// parâmetros usados (<c>pbkdf2-sha256$iterações$salt$hash</c>), então subir o número de iterações no futuro não
/// invalida os hashes antigos: eles continuam verificando e são regravados no próximo login bem-sucedido.
/// </summary>
public sealed class PasswordHasher
{
    public const string Algorithm = "pbkdf2-sha256";
    public const int SaltSize = 16;
    public const int HashSize = 32;

    // Teto defensivo: um hash adulterado no banco com um número absurdo de iterações não pode travar o servidor.
    private const int MaximumAcceptedIterations = 10_000_000;

    private static readonly byte[] DummySalt = new byte[SaltSize];

    private readonly int _iterations;

    public PasswordHasher(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.PasswordHashIterations < IdentityOptions.MinimumPasswordHashIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), $"PasswordHashIterations deve ser de pelo menos {IdentityOptions.MinimumPasswordHashIterations}.");
        }

        _iterations = options.PasswordHashIterations;
    }

    public int Iterations => _iterations;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, _iterations, HashSize);
        return $"{Algorithm}${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifica a senha. Quando não há hash (usuário inexistente) ou ele é inválido, faz o mesmo trabalho de uma
    /// verificação real antes de recusar, para que o tempo de resposta não revele se o usuário existe.
    /// </summary>
    public PasswordVerification Verify(string password, string? storedHash)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (storedHash is null || !TryParse(storedHash, out var iterations, out var salt, out var expected))
        {
            _ = Derive(password, DummySalt, _iterations, HashSize);
            return PasswordVerification.Failed;
        }

        var actual = Derive(password, salt, iterations, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerification.Failed;
        }

        return iterations < _iterations ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        var partes = stored.Split('$');
        if (partes.Length != 4 || partes[0] != Algorithm)
        {
            return false;
        }

        if (!int.TryParse(partes[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out iterations)
            || iterations < 1 || iterations > MaximumAcceptedIterations)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(partes[2]);
            hash = Convert.FromBase64String(partes[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 8 && hash.Length >= 16;
    }
}
