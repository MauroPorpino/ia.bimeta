namespace IaBimeta.Identity.Application;

/// <summary>Parâmetros do Identity. Os padrões são os de produção.</summary>
public sealed class IdentityOptions
{
    /// <summary>Iterações do PBKDF2-HMAC-SHA256. O padrão segue a recomendação atual da OWASP (600 mil).</summary>
    public const int DefaultPasswordHashIterations = 600_000;

    /// <summary>Piso aceito pelo hasher. Abaixo disso a configuração é recusada na partida.</summary>
    public const int MinimumPasswordHashIterations = 10_000;

    public int PasswordHashIterations { get; set; } = DefaultPasswordHashIterations;

    /// <summary>Duração de uma sessão desde o login. Passado esse tempo ela é recusada.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Senhas erradas, dentro da janela, para o mesmo e-mail no mesmo tenant, até o bloqueio temporário.</summary>
    public int MaxFailedLogins { get; set; } = 5;

    /// <summary>Janela em que as falhas são contadas. Passada a janela sem bloqueio, a contagem recomeça.</summary>
    public TimeSpan FailedLoginWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Duração do bloqueio temporário. Durante ele nem a senha certa é aceita (e a senha nem é verificada).</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
}
