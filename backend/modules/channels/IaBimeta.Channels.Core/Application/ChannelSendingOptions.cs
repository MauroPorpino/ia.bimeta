namespace IaBimeta.Channels.Application;

/// <summary>
/// Os parâmetros do envio (seção <c>Channels:Sending</c>): a vazão por conta (SND-2), a espera local e a espera depois de um limite de taxa do provedor. São CONFIGURAÇÃO, nunca constantes de código (BOUNDARIES.md
/// §12.11): o padrão de 80 mensagens por segundo é o da documentação oficial da Meta para cada número do Cloud API (consulta de 05/11/2026), e a vazão própria de uma conta (por exemplo, 20 para um número em
/// coexistência com o aplicativo WhatsApp Business) é definida na administração da conta.
/// </summary>
public sealed class ChannelSendingOptions
{
    /// <summary>A vazão padrão de uma conta, em mensagens por segundo (80 é o padrão do Cloud API).</summary>
    public int DefaultThroughputPerSecond { get; set; } = 80;

    /// <summary>Quantos segundos de vazão cabem no balde (a rajada máxima = vazão vezes este valor, no mínimo uma ficha).</summary>
    public double BurstSeconds { get; set; } = 1d;

    /// <summary>
    /// Quanto uma entrega espera, DENTRO do handler, por uma ficha ou pelo fim da espera do provedor. Acima disso a entrega é devolvida ao relay com a espera calculada (e gasta uma tentativa do relay). O lease do
    /// relay é de 2 minutos: este valor mais o tempo limite da chamada ao provedor precisa caber nele.
    /// </summary>
    public TimeSpan MaxLocalWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A espera imposta à conta depois de um limite de taxa do provedor que não disse quanto esperar.</summary>
    public TimeSpan DefaultRateLimitCooldown { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>O teto da espera imposta à conta (um <c>Retry-After</c> maior que isto é limitado).</summary>
    public TimeSpan MaxRateLimitCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        if (DefaultThroughputPerSecond is < 1 or > 1000
            || BurstSeconds is <= 0d or > 60d
            || MaxLocalWait <= TimeSpan.Zero || MaxLocalWait > TimeSpan.FromSeconds(60)
            || DefaultRateLimitCooldown <= TimeSpan.Zero
            || MaxRateLimitCooldown < DefaultRateLimitCooldown)
        {
            throw new InvalidOperationException("A configuração do envio do Channels é inválida (vazão, rajada, espera local ou espera de limite de taxa).");
        }
    }

    /// <summary>A espera a impor à conta: a sugerida pelo provedor, ou a padrão; nunca negativa e limitada ao teto.</summary>
    public TimeSpan CooldownFor(TimeSpan? suggestedByProvider)
    {
        var espera = suggestedByProvider is { } sugerida && sugerida > TimeSpan.Zero ? sugerida : DefaultRateLimitCooldown;
        return espera > MaxRateLimitCooldown ? MaxRateLimitCooldown : espera;
    }
}

/// <summary>
/// A configuração do webhook (seção <c>Channels:Webhook</c>). <see cref="PublicBaseUrl"/> é o endereço PÚBLICO e HTTPS do Api, o prefixo do caminho do webhook que se cadastra na Meta. Sem ele o Receiver
/// funciona do mesmo jeito; só o teste de prontidão avisa que falta.
/// </summary>
public sealed class ChannelWebhookOptions
{
    /// <summary>Por exemplo, <c>https://atendimento.exemplo.com.br</c>. Vazio: não configurado.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>O endereço público é HTTPS absoluto, sem usuário, consulta nem fragmento, e não é de laço local (a Meta não alcança nem aceita certificado autoassinado).</summary>
    public bool IsPublicHttps(out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)
            || !Uri.TryCreate(PublicBaseUrl.Trim(), UriKind.Absolute, out var endereco)
            || endereco.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(endereco.UserInfo)
            || !string.IsNullOrEmpty(endereco.Query)
            || !string.IsNullOrEmpty(endereco.Fragment)
            || endereco.IsLoopback
            || endereco.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || endereco.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        address = endereco;
        return true;
    }
}
