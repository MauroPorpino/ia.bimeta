using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>
/// O papel de uma credencial. A lista é provisória: os segredos que cada integração exige e o formato deles são confirmados na
/// documentação oficial do provedor antes do Receiver e do Sender (BOUNDARIES.md PRV-2).
/// </summary>
public enum ChannelSecretKind
{
    /// <summary>Token de acesso para enviar mensagens e consultar a API do provedor.</summary>
    AccessToken = 1,

    /// <summary>Segredo do aplicativo, usado para validar a assinatura dos webhooks.</summary>
    AppSecret = 2,

    /// <summary>Token de verificação do cadastro do webhook.</summary>
    WebhookVerifyToken = 3,
}

/// <summary>
/// Uma credencial da conta, guardada CIFRADA e referenciada pelo <see cref="Id"/>. Este tipo só conhece o texto cifrado: o
/// valor em claro nunca é uma propriedade, não sai em visão de API, não vai para log nem para a trilha de auditoria. A chave
/// de cifra não fica no banco. O texto cifrado é amarrado ao tenant, ao identificador e ao papel (dado autenticado
/// adicional), então copiar a linha para outro tenant ou outro papel não a torna legível.
/// </summary>
public sealed class ChannelSecret : ITenantOwned
{
    public const int MaxValueLength = 4096;

    private ChannelSecret(Guid id, TenantId tenantId, Guid accountId, ChannelSecretKind kind, string keyId, string ciphertext, int version, Guid? updatedBy, DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        AccountId = accountId;
        Kind = kind;
        KeyId = keyId;
        Ciphertext = ciphertext;
        Version = version;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    /// <summary>A referência da credencial: é o que o restante do sistema guarda e passa adiante, nunca o valor.</summary>
    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid AccountId { get; }

    public ChannelSecretKind Kind { get; }

    /// <summary>Qual chave mestra cifrou o valor (permite girar a chave sem perder o que já está guardado).</summary>
    public string KeyId { get; private set; }

    public string Ciphertext { get; private set; }

    /// <summary>Quantas vezes o valor foi definido. Começa em 1 e sobe a cada rotação.</summary>
    public int Version { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ChannelSecret Create(Guid id, TenantId tenantId, Guid accountId, ChannelSecretKind kind, string keyId, string ciphertext, Guid? updatedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (id == Guid.Empty || accountId == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new ChannelsValidationException("A credencial precisa de conta e papel válidos.");
        }

        return new ChannelSecret(id, tenantId, accountId, kind, keyId, ciphertext, 1, updatedBy, now);
    }

    /// <summary>Troca o valor cifrado (rotação). A referência continua a mesma.</summary>
    public void Replace(string keyId, string ciphertext, Guid? updatedBy, DateTimeOffset now)
    {
        KeyId = keyId;
        Ciphertext = ciphertext;
        Version++;
        UpdatedBy = updatedBy;
        UpdatedAt = now;
    }

    /// <summary>Valida o valor em claro antes de cifrar. A mensagem nunca reproduz o valor.</summary>
    /// <exception cref="ChannelsValidationException">O valor é vazio, longo demais, tem espaço nas pontas ou caractere de controle.</exception>
    public static string ValidateValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength)
        {
            throw new ChannelsValidationException($"O valor da credencial deve ter de 1 a {MaxValueLength} caracteres.");
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value.Any(char.IsControl))
        {
            throw new ChannelsValidationException("O valor da credencial não pode ter espaço nas pontas nem caractere de controle.");
        }

        return value;
    }
}
