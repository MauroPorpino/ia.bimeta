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
}
