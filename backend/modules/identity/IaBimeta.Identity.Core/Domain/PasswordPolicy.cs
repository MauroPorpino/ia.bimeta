namespace IaBimeta.Identity.Domain;

/// <summary>
/// Política mínima de senha: comprimento. O limite máximo existe para que uma senha enorme não vire um custo de CPU
/// para quem chama o servidor (a derivação PBKDF2 é cara de propósito).
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <exception cref="IdentityValidationException">A senha não atende à política.</exception>
    public static void Validate(string? password)
    {
        if (password is null || password.Length < MinLength || password.Length > MaxLength)
        {
            throw new IdentityValidationException($"A senha deve ter de {MinLength} a {MaxLength} caracteres.");
        }
    }

    public static bool IsWithinLimits(string? password) =>
        password is not null && password.Length is >= 1 and <= MaxLength;
}
