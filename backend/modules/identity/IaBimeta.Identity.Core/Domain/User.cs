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
    private User(Guid id, TenantId tenantId, string email, string displayName, string passwordHash, DateTimeOffset createdAt, Locale? preferredLocale, UserAuthMode authMode, UserStatus status, DateTimeOffset? statusChangedAt, bool isInitialAdministrator)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
        PreferredLocale = preferredLocale;
        AuthMode = authMode;
        Status = status;
        StatusChangedAt = statusChangedAt;
        IsInitialAdministrator = isInitialAdministrator;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    /// <summary>Normalizado: sem espaços nas pontas e em minúsculas.</summary>
    public string Email { get; private set; }

    public string DisplayName { get; private set; }

    public string PasswordHash { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>O idioma que o usuário prefere para a interface (BCP 47), ou <c>null</c> se ainda não escolheu. Não é o idioma de nenhuma conversa.</summary>
    public Locale? PreferredLocale { get; private set; }

    /// <summary>Como o usuário prova quem é. O usuário federado nunca faz login por senha.</summary>
    public UserAuthMode AuthMode { get; }

    /// <summary>O "hash" de um usuário federado: não é um hash válido, então nenhuma senha o confere.</summary>
    public const string FederatedPasswordSentinel = "!federated";

    /// <summary>O domínio reservado (RFC 2606) do e-mail sintético dos usuários federados: nunca coincide com um e-mail real.</summary>
    public const string FederatedEmailDomain = "federated.invalid";

    /// <summary>
    /// Cria o usuário que a federação (<c>JitAttendant</c>) precisa: e-mail sintético e único (nunca o do ERP, para que coincidência de e-mail jamais vincule a uma conta existente), sem senha utilizável.
    /// O nome vem do ERP como informação de exibição.
    /// </summary>
    public static User CreateFederated(TenantId tenantId, string? displayName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var nome = (displayName ?? string.Empty).Trim();
        nome = nome.Length == 0 ? "Usuário externo" : nome.Length > DisplayNameMaxLength ? nome[..DisplayNameMaxLength].TrimEnd() : nome;
        var id = Guid.CreateVersion7();
        return new User(id, tenantId, $"fed.{id:N}@{FederatedEmailDomain}", nome, FederatedPasswordSentinel, now, null, UserAuthMode.Federated, UserStatus.Active, null, false);
    }

    public static User Create(TenantId tenantId, string? email, string? displayName, string passwordHash, DateTimeOffset now, Locale? preferredLocale = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var (emailNormalizado, nome) = ValidateProfile(email, displayName);
        return new User(Guid.CreateVersion7(), tenantId, emailNormalizado, nome, passwordHash, now, preferredLocale, UserAuthMode.Password, UserStatus.Active, null, false);
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

    /// <summary>A situação do usuário. Muda só pelos métodos abaixo, sempre sob a trava de admissão (Identity).</summary>
    public UserStatus Status { get; private set; }

    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>
    /// Marca INEQUÍVOCA do administrador inicial provisionado pela BImeta (comando first-admin) e ainda sem primeiro acesso concluído. Só o provisionamento a liga; some (para sempre) quando o primeiro acesso é concluído ou a admissão é cancelada.
    /// É o único critério para a BImeta reemitir o token de primeiro acesso dessa conta: perfil administrativo, e-mail ou "não há outro administrador" não bastam.
    /// </summary>
    public bool IsInitialAdministrator { get; private set; }

    /// <summary>O "hash" do usuário pendente: não é um hash válido, então nenhuma senha o confere. A senha só nasce no primeiro acesso, pelo token.</summary>
    public const string PendingPasswordSentinel = "!pending";

    /// <summary>Cria a ADMISSÃO: usuário pendente com credencial inutilizável. O acesso só nasce quando ele usa o token de primeiro acesso.</summary>
    public static User CreatePending(TenantId tenantId, string? email, string? displayName, DateTimeOffset now, Locale? preferredLocale = null, bool initialAdministrator = false)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        var (emailNormalizado, nome) = ValidateProfile(email, displayName);
        return new User(Guid.CreateVersion7(), tenantId, emailNormalizado, nome, PendingPasswordSentinel, now, preferredLocale, UserAuthMode.Password, UserStatus.Pending, now, initialAdministrator);
    }

    /// <summary>Pendente ou inativo passa a ativo. A admissão cancelada não é reativada (readmitir é <see cref="Readmit"/>, um novo ingresso).</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status is not (UserStatus.Pending or UserStatus.Inactive))
        {
            throw new IdentityValidationException("Só um usuário pendente ou inativo pode ser ativado.");
        }

        Status = UserStatus.Active;
        IsInitialAdministrator = false;
        StatusChangedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        if (Status != UserStatus.Active)
        {
            throw new IdentityValidationException("Só um usuário ativo pode ser desativado.");
        }

        Status = UserStatus.Inactive;
        StatusChangedAt = now;
    }

    public void CancelAdmission(DateTimeOffset now)
    {
        if (Status != UserStatus.Pending)
        {
            throw new IdentityValidationException("Só uma admissão pendente pode ser cancelada.");
        }

        Status = UserStatus.Cancelled;
        IsInitialAdministrator = false;
        StatusChangedAt = now;
    }

    /// <summary>Uma admissão cancelada volta a pendente: é um novo ingresso, tratado como tal pelo serviço.</summary>
    public void Readmit(DateTimeOffset now)
    {
        if (Status != UserStatus.Cancelled)
        {
            throw new IdentityValidationException("Só uma admissão cancelada pode ser readmitida.");
        }

        Status = UserStatus.Pending;
        StatusChangedAt = now;
    }

    /// <summary>Altera nome e e-mail (só usuário de senha: o e-mail do federado é sintético). A credencial não muda.</summary>
    public void UpdateProfile(string? email, string? displayName)
    {
        var (emailNormalizado, nome) = ValidateProfile(email, displayName);
        if (AuthMode == UserAuthMode.Federated && !string.Equals(emailNormalizado, Email, StringComparison.Ordinal))
        {
            throw new IdentityValidationException("O e-mail de um usuário externo não pode ser alterado.");
        }

        Email = emailNormalizado;
        DisplayName = nome;
    }

    public void ReplacePasswordHash(string newHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);
        PasswordHash = newHash;
    }
}
