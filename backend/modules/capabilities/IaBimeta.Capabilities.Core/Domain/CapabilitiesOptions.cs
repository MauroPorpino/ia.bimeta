namespace IaBimeta.Capabilities.Domain;

/// <summary>
/// A política de DESTINO das chamadas de saída (seção <c>Capabilities:Options:Outbound</c>). É decisão do OPERADOR da plataforma, nunca do tenant: o administrador de uma empresa cadastra o endereço de um sistema, mas só o
/// operador libera http, portas extras ou uma rede privada (por exemplo, o CIDR de um ERP interno acessado por VPN). Metadados de nuvem, link-local, multicast e faixas reservadas NUNCA são liberáveis.
/// </summary>
public sealed class OutboundOptions
{
    /// <summary>Aceita <c>http</c> (sem TLS). Desligado por padrão: só para desenvolvimento e homologação em rede controlada.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>As portas de destino permitidas. Vazio = só as padrão do esquema (443; 80 se <see cref="AllowHttp"/>).</summary>
    public List<int> AllowedPorts { get; set; } = [];

    /// <summary>Redes privadas ou de loopback que o operador libera (por exemplo, <c>10.20.0.0/16</c>). Nada fora desta lista e das faixas públicas é alcançável.</summary>
    public List<string> AllowedPrivateCidrs { get; set; } = [];
}

/// <summary>As opções do motor de integração (seção <c>Capabilities:Options</c>). Todos os limites são configuráveis sem redeploy (LIM-1).</summary>
public sealed class CapabilitiesOptions
{
    /// <summary>Interruptor global: desligado, nenhuma operação externa executa (a administração continua).</summary>
    public bool Enabled { get; set; } = true;

    public OutboundOptions Outbound { get; set; } = new();

    public int MaxConnectionsPerTenant { get; set; } = 20;

    public int MaxDefinitionKeysPerTenant { get; set; } = 200;

    public int MaxTimeoutSeconds { get; set; } = 30;

    public int DefaultTimeoutSeconds { get; set; } = 10;

    /// <summary>O teto do tamanho da resposta que uma conexão pode pedir.</summary>
    public int MaxResponseBytesCeiling { get; set; } = 1_048_576;

    public int DefaultMaxResponseBytes { get; set; } = 262_144;

    /// <summary>A validade de uma escrita que espera confirmação.</summary>
    public int ConfirmationTtlMinutes { get; set; } = 15;

    /// <summary>Depois deste prazo, uma execução ainda "em curso" (o processo caiu no meio) é tratada como resultado ambíguo.</summary>
    public int RunningLeaseSeconds { get; set; } = 180;

    public int PerTenantPerMinute { get; set; } = 120;

    public int PerConnectionPerMinute { get; set; } = 60;

    public int PerConnectionMaxInFlight { get; set; } = 5;

    public int WritesPerTenantPerMinute { get; set; } = 30;

    public int TestsPerConnectionPerMinute { get; set; } = 10;

    /// <summary>A espera base entre tentativas (dobra a cada tentativa, até o teto). Só vale para leitura ou escrita com idempotência comprovada.</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    public int RetryMaxDelayMs { get; set; } = 2000;

    public void Validate()
    {
        static void Positive(string name, int value)
        {
            if (value < 1)
            {
                throw new InvalidOperationException($"Capabilities:Options:{name} precisa ser maior que zero.");
            }
        }

        Positive(nameof(MaxConnectionsPerTenant), MaxConnectionsPerTenant);
        Positive(nameof(MaxDefinitionKeysPerTenant), MaxDefinitionKeysPerTenant);
        Positive(nameof(MaxTimeoutSeconds), MaxTimeoutSeconds);
        Positive(nameof(DefaultTimeoutSeconds), DefaultTimeoutSeconds);
        Positive(nameof(MaxResponseBytesCeiling), MaxResponseBytesCeiling);
        Positive(nameof(DefaultMaxResponseBytes), DefaultMaxResponseBytes);
        Positive(nameof(ConfirmationTtlMinutes), ConfirmationTtlMinutes);
        Positive(nameof(RunningLeaseSeconds), RunningLeaseSeconds);
        Positive(nameof(PerTenantPerMinute), PerTenantPerMinute);
        Positive(nameof(PerConnectionPerMinute), PerConnectionPerMinute);
        Positive(nameof(PerConnectionMaxInFlight), PerConnectionMaxInFlight);
        Positive(nameof(WritesPerTenantPerMinute), WritesPerTenantPerMinute);
        Positive(nameof(TestsPerConnectionPerMinute), TestsPerConnectionPerMinute);

        if (RetryBaseDelayMs < 0 || RetryMaxDelayMs < RetryBaseDelayMs)
        {
            throw new InvalidOperationException("Capabilities:Options: a espera entre tentativas é inválida.");
        }

        if (DefaultTimeoutSeconds > MaxTimeoutSeconds || DefaultMaxResponseBytes > MaxResponseBytesCeiling)
        {
            throw new InvalidOperationException("Capabilities:Options: os padrões de tempo e de tamanho não podem passar dos tetos.");
        }

        // Falha fechada na partida: um CIDR ilegível nunca vira "liberar tudo" nem "ignorar".
        foreach (var cidr in Outbound.AllowedPrivateCidrs)
        {
            if (!System.Net.IPNetwork.TryParse(cidr, out _))
            {
                throw new InvalidOperationException("Capabilities:Options:Outbound:AllowedPrivateCidrs tem um CIDR inválido.");
            }
        }

        foreach (var porta in Outbound.AllowedPorts)
        {
            if (porta is < 1 or > 65535)
            {
                throw new InvalidOperationException("Capabilities:Options:Outbound:AllowedPorts tem uma porta inválida.");
            }
        }
    }
}
