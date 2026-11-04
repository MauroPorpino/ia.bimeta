using IaBimeta.AiGateway.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// As sobrescritas dos limites padrão da plataforma para UM tenant. Só os valores preenchidos sobrescrevem; o resto vale o padrão da plataforma
/// (<see cref="AiDefaultLimits"/>). Quem as define é o operador da plataforma (contexto de sistema), nunca o tenant.
/// </summary>
public sealed record AiTenantLimitOverrides(
    int? MaxConcurrentCalls = null,
    int? MaxCallsPerPeriod = null,
    long? MaxTokensPerPeriod = null,
    TimeSpan? Period = null,
    int? MaxInputMessages = null,
    int? MaxOutputTokens = null,
    long? MaxTokensPerCall = null)
{
    /// <summary>Nenhuma sobrescrita: vale o padrão da plataforma.</summary>
    public static AiTenantLimitOverrides None { get; } = new();

    /// <summary>Verdadeiro quando cada valor preenchido é positivo.</summary>
    public bool IsValid =>
        (MaxConcurrentCalls is null or > 0)
        && (MaxCallsPerPeriod is null or > 0)
        && (MaxTokensPerPeriod is null or > 0)
        && (Period is null || Period > TimeSpan.Zero)
        && (MaxInputMessages is null or > 0)
        && (MaxOutputTokens is null or > 0)
        && (MaxTokensPerCall is null or > 0);
}

/// <summary>
/// Os limites padrão da PLATAFORMA (configuração, seção <c>AiGateway:DefaultLimits</c>): o valor que vale para um tenant que não tem sobrescrita. Referência a calibrar, nunca embutida no
/// código. Um tenant só tem limites utilizáveis se, para cada limite, ou a sobrescrita dele ou este padrão existe.
/// </summary>
public sealed class AiDefaultLimits
{
    public int? MaxConcurrentCalls { get; set; }

    public int? MaxCallsPerPeriod { get; set; }

    public long? MaxTokensPerPeriod { get; set; }

    /// <summary>A duração do período, em segundos (janela fixa em UTC; o padrão inicial é de 24 horas).</summary>
    public int? PeriodSeconds { get; set; }

    public int? MaxInputMessages { get; set; }

    public int? MaxOutputTokens { get; set; }

    public long? MaxTokensPerCall { get; set; }

    /// <summary>
    /// Funde a sobrescrita do tenant com este padrão. Devolve limites INVÁLIDOS (zerados) quando algum limite obrigatório não existe em nenhum dos dois: o seletor os recusa
    /// (<c>limits-invalid</c>), e o tenant não chama nada com limite indefinido.
    /// </summary>
    public AiTenantLimits Merge(AiTenantLimitOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var periodo = overrides.Period ?? (PeriodSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null);
        var concorrentes = overrides.MaxConcurrentCalls ?? MaxConcurrentCalls;
        var chamadas = overrides.MaxCallsPerPeriod ?? MaxCallsPerPeriod;
        var tokens = overrides.MaxTokensPerPeriod ?? MaxTokensPerPeriod;
        var mensagens = overrides.MaxInputMessages ?? MaxInputMessages;
        var saida = overrides.MaxOutputTokens ?? MaxOutputTokens;

        if (concorrentes is null || chamadas is null || tokens is null || periodo is null || mensagens is null || saida is null)
        {
            return new AiTenantLimits(0, 0, 0, TimeSpan.Zero, 0, 0);
        }

        return new AiTenantLimits(concorrentes.Value, chamadas.Value, tokens.Value, periodo.Value, mensagens.Value, saida.Value, overrides.MaxTokensPerCall ?? MaxTokensPerCall);
    }
}

/// <summary>
/// A configuração de IA GUARDADA de um tenant (uma por tenant): o provedor, o modelo, a origem da credencial, a situação e as sobrescritas de limite. A chave é <c>(tenant_id, id)</c>
/// (TEN-3) e há uma só por tenant. A credencial NÃO mora aqui (<see cref="StoredAiCredential"/>). A <see cref="Version"/> serve à concorrência otimista.
/// </summary>
public sealed class AiTenantSettings : ITenantOwned
{
    private AiTenantSettings()
    {
        TenantId = null!;
        ProviderId = null!;
        ModelId = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public string ProviderId { get; private set; }

    public string ModelId { get; private set; }

    public AiCredentialSource CredentialSource { get; private set; }

    public AiTenantStatus Status { get; private set; }

    public int? MaxConcurrentCalls { get; private set; }

    public int? MaxCallsPerPeriod { get; private set; }

    public long? MaxTokensPerPeriod { get; private set; }

    public int? PeriodSeconds { get; private set; }

    public int? MaxInputMessages { get; private set; }

    public int? MaxOutputTokens { get; private set; }

    public long? MaxTokensPerCall { get; private set; }

    /// <summary>Cresce a cada alteração. É o token de concorrência: duas alterações do mesmo ponto de partida não se sobrescrevem em silêncio.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public AiTenantLimitOverrides LimitOverrides =>
        new(MaxConcurrentCalls, MaxCallsPerPeriod, MaxTokensPerPeriod, PeriodSeconds is { } s ? TimeSpan.FromSeconds(s) : null, MaxInputMessages, MaxOutputTokens, MaxTokensPerCall);

    public static AiTenantSettings Create(TenantId tenantId, string providerId, string modelId, AiTenantStatus status, DateTimeOffset now, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        Validate(providerId, modelId, status);

        return new AiTenantSettings
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProviderId = providerId,
            ModelId = modelId,
            CredentialSource = AiCredentialSource.Byok,
            Status = status,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = updatedBy,
        };
    }

    /// <summary>Troca o provedor, o modelo e a situação (nunca <see cref="AiTenantStatus.Blocked"/> pelo tenant: quem bloqueia é a plataforma, em <see cref="SetStatus"/>).</summary>
    public void Configure(string providerId, string modelId, AiTenantStatus status, DateTimeOffset now, Guid? updatedBy)
    {
        Validate(providerId, modelId, status);
        ProviderId = providerId;
        ModelId = modelId;
        Status = status;
        Touch(now, updatedBy);
    }

    public void SetStatus(AiTenantStatus status, DateTimeOffset now, Guid? updatedBy)
    {
        if (!Enum.IsDefined(status))
        {
            throw new AiGatewayValidationException("A situação da IA do tenant é desconhecida.");
        }

        Status = status;
        Touch(now, updatedBy);
    }

    public void SetLimitOverrides(AiTenantLimitOverrides overrides, DateTimeOffset now, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (!overrides.IsValid)
        {
            throw new AiGatewayValidationException("Cada limite sobrescrito precisa ser positivo.");
        }

        MaxConcurrentCalls = overrides.MaxConcurrentCalls;
        MaxCallsPerPeriod = overrides.MaxCallsPerPeriod;
        MaxTokensPerPeriod = overrides.MaxTokensPerPeriod;
        PeriodSeconds = overrides.Period is { } p ? (int)Math.Ceiling(p.TotalSeconds) : null;
        MaxInputMessages = overrides.MaxInputMessages;
        MaxOutputTokens = overrides.MaxOutputTokens;
        MaxTokensPerCall = overrides.MaxTokensPerCall;
        Touch(now, updatedBy);
    }

    private void Touch(DateTimeOffset now, Guid? updatedBy)
    {
        Version++;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }

    private static void Validate(string providerId, string modelId, AiTenantStatus status)
    {
        if (!Domain.AiModelCatalog.IsValidProviderId(providerId) || string.IsNullOrWhiteSpace(modelId) || modelId.Length > 100)
        {
            throw new AiGatewayValidationException("O provedor e o modelo da configuração precisam ser válidos.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new AiGatewayValidationException("A situação da IA do tenant é desconhecida.");
        }
    }
}
