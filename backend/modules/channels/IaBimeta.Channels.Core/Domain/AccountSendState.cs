using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>O desfecho de um pedido de ficha ao balde da conta (SND-2 e SND-6).</summary>
/// <param name="Granted">Verdadeiro: a ficha foi concedida e o envio pode sair agora.</param>
/// <param name="Paused">Verdadeiro: a conta está pausada (SND-6) e nada sai dela até a retomada.</param>
/// <param name="Wait">Quando não concedida e não pausada, quanto esperar até haver ficha ou acabar a espera imposta pelo provedor.</param>
public readonly record struct SendAcquireResult(bool Granted, bool Paused, TimeSpan Wait)
{
    public static SendAcquireResult Allow() => new(true, false, TimeSpan.Zero);

    public static SendAcquireResult OnPause() => new(false, true, TimeSpan.Zero);

    public static SendAcquireResult After(TimeSpan wait) => new(false, false, wait);
}

/// <summary>
/// O estado de envio de uma conta do provedor, uma linha por conta (<c>channels.account_send_state</c>): o BALDE DE FICHAS que aplica a vazão por conta (SND-2, guardado no banco para valer entre instâncias do Worker),
/// a ESPERA imposta pelo provedor (limite de taxa de uma recusa 429) e a PAUSA da conta (SND-6). Só a persistência altera a linha, sempre com a linha travada na transação, então duas instâncias nunca gastam a mesma
/// ficha. Nenhum valor de credencial passa por aqui.
/// </summary>
public sealed class AccountSendState : ITenantOwned
{
    public const int PauseReasonMaxLength = 100;
    public const int MinThroughputPerSecond = 1;
    public const int MaxThroughputPerSecond = 1000;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private AccountSendState(TenantId tenantId, Guid accountId, double tokens, DateTimeOffset refilledAt, DateTimeOffset updatedAt)
    {
        TenantId = tenantId;
        AccountId = accountId;
        Tokens = tokens;
        RefilledAt = refilledAt;
        UpdatedAt = updatedAt;
    }

    public TenantId TenantId { get; }

    public Guid AccountId { get; }

    /// <summary>As fichas no balde (fracionárias: o balde reabastece de forma contínua).</summary>
    public double Tokens { get; private set; }

    /// <summary>O instante até o qual o balde já foi reabastecido.</summary>
    public DateTimeOffset RefilledAt { get; private set; }

    /// <summary>Enquanto não passar deste instante nenhum envio da conta sai (o provedor mandou esperar). Nulo: sem espera.</summary>
    public DateTimeOffset? CooldownUntil { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>O motivo da pausa: um código curto (<c>manual</c> ou <c>account-error.190</c>), nunca texto do provedor.</summary>
    public string? PauseReason { get; private set; }

    public DateTimeOffset? PausedAt { get; private set; }

    public DateTimeOffset? ResumedAt { get; private set; }

    /// <summary>A vazão própria desta conta, em mensagens por segundo; nulo = a padrão da configuração (por exemplo, 20 para um número em coexistência com o aplicativo WhatsApp Business).</summary>
    public int? ThroughputPerSecond { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A linha inicial de uma conta: balde cheio, sem espera, sem pausa.</summary>
    public static AccountSendState Create(TenantId tenantId, Guid accountId, double initialTokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return new AccountSendState(tenantId, accountId, initialTokens, now, now);
    }

    /// <summary>
    /// Pede UMA ficha. Pausada, não concede nem gasta nada. Dentro da espera imposta pelo provedor, não concede. Senão reabastece o balde pelo tempo decorrido (taxa = vazão da conta, capacidade = a vazão vezes os
    /// segundos de rajada, no mínimo uma ficha) e concede se houver uma ficha inteira; sem ficha, devolve quanto esperar.
    /// </summary>
    public SendAcquireResult TryTake(DateTimeOffset now, int defaultThroughputPerSecond, double burstSeconds)
    {
        if (IsPaused)
        {
            return SendAcquireResult.OnPause();
        }

        var taxa = EffectiveThroughput(defaultThroughputPerSecond);
        var capacidade = Capacity(taxa, burstSeconds);
        Refill(now, taxa, capacidade);

        if (CooldownUntil is { } fim)
        {
            if (now < fim)
            {
                return SendAcquireResult.After(fim - now);
            }

            CooldownUntil = null;
            UpdatedAt = now;
        }

        if (Tokens >= 1d)
        {
            Tokens -= 1d;
            UpdatedAt = now;
            return SendAcquireResult.Allow();
        }

        // Espera até a fração que falta virar uma ficha inteira (arredondada para cima ao milissegundo: nunca acorda antes da hora).
        var segundos = (1d - Tokens) / taxa;
        return SendAcquireResult.After(TimeSpan.FromMilliseconds(Math.Ceiling(segundos * 1000d)));
    }

    /// <summary>O provedor mandou esperar (limite de taxa): nenhum envio da conta sai até o instante dado. Só estende, nunca encurta uma espera em curso.</summary>
    public void ApplyCooldown(DateTimeOffset until, DateTimeOffset now)
    {
        if (CooldownUntil is not { } atual || until > atual)
        {
            CooldownUntil = until;
            UpdatedAt = now;
        }
    }

    /// <summary>Pausa a conta (SND-6). Falso: já estava pausada (vale o motivo da primeira pausa, que é o que explica a causa).</summary>
    public bool Pause(string reason, DateTimeOffset now)
    {
        if (IsPaused)
        {
            return false;
        }

        IsPaused = true;
        PauseReason = NormalizeReason(reason);
        PausedAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Retoma a conta. O balde recomeça com UMA ficha e sem espera: a vazão volta de forma gradual, não em rajada. Falso: não estava pausada.</summary>
    public bool Resume(DateTimeOffset now)
    {
        if (!IsPaused)
        {
            return false;
        }

        IsPaused = false;
        PauseReason = null;
        ResumedAt = now;
        CooldownUntil = null;
        Tokens = 1d;
        RefilledAt = now;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Define (ou, com nulo, limpa) a vazão própria da conta.</summary>
    /// <exception cref="ChannelsValidationException">A vazão está fora de 1 a 1000 mensagens por segundo.</exception>
    public void SetThroughput(int? perSecond, DateTimeOffset now)
    {
        if (perSecond is { } valor && (valor < MinThroughputPerSecond || valor > MaxThroughputPerSecond))
        {
            throw new ChannelsValidationException($"A vazão deve ficar entre {MinThroughputPerSecond} e {MaxThroughputPerSecond} mensagens por segundo.");
        }

        ThroughputPerSecond = perSecond;
        UpdatedAt = now;
    }

    public int EffectiveThroughput(int defaultThroughputPerSecond) => ThroughputPerSecond ?? defaultThroughputPerSecond;

    public static double Capacity(int throughputPerSecond, double burstSeconds) => Math.Max(1d, throughputPerSecond * burstSeconds);

    private void Refill(DateTimeOffset now, int throughputPerSecond, double capacity)
    {
        // O relógio nunca regride o balde: se o instante é anterior ao último reabastecimento, só limita ao teto.
        var decorrido = (now - RefilledAt).TotalSeconds;
        if (decorrido > 0d)
        {
            Tokens = Math.Min(capacity, Tokens + (decorrido * throughputPerSecond));
            RefilledAt = now;
        }
        else
        {
            Tokens = Math.Min(capacity, Tokens);
        }
    }

    private static string NormalizeReason(string? reason)
    {
        var texto = (reason ?? string.Empty).Trim().ToLowerInvariant();
        var limpo = new string(texto.Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_' ? c : '-').ToArray());
        if (limpo.Length == 0)
        {
            limpo = "unspecified";
        }

        return limpo.Length > PauseReasonMaxLength ? limpo[..PauseReasonMaxLength] : limpo;
    }
}
