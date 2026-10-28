using System.Text.RegularExpressions;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Domain;

/// <summary>
/// Usuário de um tenant. A chave é <c>(tenant_id, id)</c> e o e-mail é único dentro do tenant (TEN-2 e TEN-3). Guarda
/// só o hash da senha, nunca a senha.
/// </summary>
public sealed class User : ITenantOwned
{
    public const int EmailMaxLength = 254;
    public const int DisplayNameMaxLength = 120;
    public const int PasswordHashMaxLength = 200;

    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private User(Guid id, TenantId tenantId, string email, string displayName, string passwordHash, DateTimeOffset createdAt, Locale? preferredLocale)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
        PreferredLocale = preferredLocale;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    /// <summary>Normalizado: sem espaços nas pontas e em minúsculas.</summary>
    public string Email { get; }

    public string DisplayName { get; }

    public string PasswordHash { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>O idioma que o usuário prefere para a interface (BCP 47), ou <c>null</c> se ainda não escolheu. Não é o idioma de nenhuma conversa.</summary>
    public Locale? PreferredLocale { get; private set; }

    public static User Create(TenantId tenantId, string? email, string? displayName, string passwordHash, DateTimeOffset now, Locale? preferredLocale = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var (emailNormalizado, nome) = ValidateProfile(email, displayName);
        return new User(Guid.CreateVersion7(), tenantId, emailNormalizado, nome, passwordHash, now, preferredLocale);
    }

    /// <summary>
    /// Lê o idioma informado (<c>null</c> ou vazio significa "sem preferência").
    /// </summary>
    /// <exception cref="IdentityValidationException">Não é um identificador BCP 47 bem formado.</exception>
    public static Locale? ParsePreferredLocale(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Locale.TryParse(text, out var locale)
            ? locale
            : throw new IdentityValidationException("O idioma deve ser um identificador BCP 47 bem formado, como pt-BR, en-US, es-ES ou fr-FR.");
    }

    public void SetPreferredLocale(Locale? locale)
    {
        PreferredLocale = locale;
    }

    /// <summary>
    /// Valida e normaliza e-mail e nome. Separado da criação para que o caso de uso recuse dados inválidos antes de
    /// gastar CPU com o hash da senha.
    /// </summary>
    /// <exception cref="IdentityValidationException">E-mail ou nome inválido.</exception>
    public static (string Email, string DisplayName) ValidateProfile(string? email, string? displayName)
    {
        var emailNormalizado = NormalizeEmail(email)
            ?? throw new IdentityValidationException($"Informe um e-mail válido, com até {EmailMaxLength} caracteres.");

        var nome = (displayName ?? string.Empty).Trim();
        if (nome.Length == 0 || nome.Length > DisplayNameMaxLength)
        {
            throw new IdentityValidationException($"O nome do usuário deve ter de 1 a {DisplayNameMaxLength} caracteres.");
        }

        return (emailNormalizado, nome);
    }

    /// <summary>Devolve o e-mail normalizado, ou <c>null</c> se não for um e-mail aceitável.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var normalizado = (email ?? string.Empty).Trim().ToLowerInvariant();
        return normalizado.Length is > 0 and <= EmailMaxLength && EmailPattern.IsMatch(normalizado) ? normalizado : null;
    }

    public void ReplacePasswordHash(string newHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);
        PasswordHash = newHash;
    }
}
