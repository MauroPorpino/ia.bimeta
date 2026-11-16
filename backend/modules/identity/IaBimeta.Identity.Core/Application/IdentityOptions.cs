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

    /// <summary>Validade do token de redefinição de senha emitido pelo administrador. Curta de propósito; o piso é 1 minuto e o teto, 24 horas.</summary>
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Validade do ticket de uso único do Embedded (ADR 0039). Poucos segundos: o piso é 10 s e o teto, 5 minutos.</summary>
    public TimeSpan EmbeddedTicketLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Duração da sessão embutida. Nunca é estendida: renovar é trocar outro ticket. O piso é 1 minuto e o teto, 8 horas.</summary>
    public TimeSpan EmbeddedSessionLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Quantas sessões embutidas ativas um usuário externo mantém por conexão (várias abas). A mais antiga é revogada quando chega mais uma.</summary>
    public int MaxEmbeddedSessionsPerUser { get; set; } = 5;

    /// <summary>A tolerância do relógio nas chamadas assinadas do servidor do ERP (HMAC). O piso é 30 s e o teto, 10 minutos.</summary>
    public TimeSpan IntegrationSignatureWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Janela do limite de taxa das chamadas assinadas por identidade autenticada ou conhecida (E1-R).</summary>
    public TimeSpan IntegrationRateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Falhas de autenticação toleradas por chave conhecida e por janela; depois, a chave é recusada até a janela virar.</summary>
    public int IntegrationPermitsPerKey { get; set; } = 30;

    /// <summary>Chamadas autenticadas por conexão e por janela (todas as chaves dela somam).</summary>
    public int IntegrationPermitsPerConnection { get; set; } = 600;

    /// <summary>Quanto tempo o nonce de uma chamada assinada fica registrado (precisa cobrir a janela dos dois lados).</summary>
    public TimeSpan IntegrationNonceRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A carência padrão das chaves antigas numa rotação.</summary>
    public TimeSpan IntegrationKeyRotationGrace { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Confere os parâmetros do Embedded na partida (falha na configuração, nunca no meio de uma requisição): ticket de 10 s a 5 min, sessão embutida de 1 min a 8 h, janela de assinatura de 30 s a 10 min, nonce lembrado por
    /// mais que o dobro da janela, ao menos uma sessão por usuário externo e carência de rotação de até 7 dias.
    /// </summary>
    public void ValidateEmbedded()
    {
        if (EmbeddedTicketLifetime < TimeSpan.FromSeconds(10) || EmbeddedTicketLifetime > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Identity:EmbeddedTicketLifetime deve ficar entre 10 segundos e 5 minutos.");
        }

        if (EmbeddedSessionLifetime < TimeSpan.FromMinutes(1) || EmbeddedSessionLifetime > TimeSpan.FromHours(8))
        {
            throw new InvalidOperationException("Identity:EmbeddedSessionLifetime deve ficar entre 1 minuto e 8 horas.");
        }

        if (IntegrationSignatureWindow < TimeSpan.FromSeconds(30) || IntegrationSignatureWindow > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("Identity:IntegrationSignatureWindow deve ficar entre 30 segundos e 10 minutos.");
        }

        if (IntegrationNonceRetention <= IntegrationSignatureWindow * 2)
        {
            throw new InvalidOperationException("Identity:IntegrationNonceRetention deve ser maior que o dobro da janela de assinatura (cobre os dois lados do relógio).");
        }

        if (MaxEmbeddedSessionsPerUser < 1 || MaxEmbeddedSessionsPerUser > 20)
        {
            throw new InvalidOperationException("Identity:MaxEmbeddedSessionsPerUser deve ficar entre 1 e 20.");
        }

        if (IntegrationKeyRotationGrace <= TimeSpan.Zero || IntegrationKeyRotationGrace > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException("Identity:IntegrationKeyRotationGrace deve ficar entre mais que zero e 7 dias.");
        }
    }
}
