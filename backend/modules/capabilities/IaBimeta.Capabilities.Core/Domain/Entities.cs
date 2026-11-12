using System.Text.Json;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Domain;

/// <summary>
/// A conexão com um sistema externo (ADR 0035, §1): endereço, autenticação, limites e o segredo CIFRADO. Não contém nenhuma regra de operação. Pertence a um tenant (e, opcionalmente, a uma organização: sem
/// organização, é do tenant inteiro e exige concessão de escopo do tenant). Nasce DESABILITADA: só uma pessoa autorizada a habilita, depois de definir o segredo.
/// </summary>
public sealed class IntegrationConnection : ITenantOwned
{
    public const string RestJsonKind = "rest-json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IntegrationConnection()
    {
        Key = string.Empty;
        Name = string.Empty;
        Kind = RestJsonKind;
        BaseUrl = string.Empty;
        HealthPath = "/";
        AuthSettingsJson = "{}";
        DefaultHeadersJson = "{}";
        AllowedHeadersJson = "[]";
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public string Key { get; private set; }

    public string Name { get; private set; }

    public string Kind { get; private set; }

    public string BaseUrl { get; private set; }

    public ConnectorAuthType AuthType { get; private set; }

    public string AuthSettingsJson { get; private set; }

    public string DefaultHeadersJson { get; private set; }

    public string AllowedHeadersJson { get; private set; }

    public string HealthPath { get; private set; }

    public int TimeoutSeconds { get; private set; }

    public int MaxResponseBytes { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>O segredo cifrado (AES-256-GCM). Nunca sai do módulo: nenhuma visão o traz.</summary>
    public string? SecretCiphertext { get; private set; }

    public string? SecretKeyId { get; private set; }

    public int SecretVersion { get; private set; }

    public DateTimeOffset? SecretUpdatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public bool HasSecret => !string.IsNullOrEmpty(SecretCiphertext);

    public IReadOnlyDictionary<string, string> AuthSettings => JsonSerializer.Deserialize<Dictionary<string, string>>(AuthSettingsJson, Json) ?? [];

    public IReadOnlyDictionary<string, string> DefaultHeaders => JsonSerializer.Deserialize<Dictionary<string, string>>(DefaultHeadersJson, Json) ?? [];

    public IReadOnlyList<string> AllowedHeaders => JsonSerializer.Deserialize<List<string>>(AllowedHeadersJson, Json) ?? [];

    public static IntegrationConnection Create(
        TenantId tenantId,
        Guid? organizationId,
        string key,
        string name,
        string baseUrl,
        ConnectorAuthType authType,
        IReadOnlyDictionary<string, string> authSettings,
        IReadOnlyDictionary<string, string> defaultHeaders,
        IReadOnlyList<string> allowedHeaders,
        string healthPath,
        int timeoutSeconds,
        int maxResponseBytes,
        Guid? createdBy,
        DateTimeOffset now)
    {
        var conexao = new IntegrationConnection
        {
            TenantId = tenantId,
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Key = key,
            CreatedBy = createdBy,
            CreatedAt = now,
            Enabled = false,
        };
        conexao.Apply(name, baseUrl, authType, authSettings, defaultHeaders, allowedHeaders, healthPath, timeoutSeconds, maxResponseBytes, now);
        conexao.Revision = 1;
        return conexao;
    }

    /// <summary>Altera a configuração (nunca a chave, a organização nem o segredo). Mudar o tipo de autenticação invalida o segredo e desabilita a conexão: o novo tipo exige um segredo novo.</summary>
    public void Update(
        string name,
        string baseUrl,
        ConnectorAuthType authType,
        IReadOnlyDictionary<string, string> authSettings,
        IReadOnlyDictionary<string, string> defaultHeaders,
        IReadOnlyList<string> allowedHeaders,
        string healthPath,
        int timeoutSeconds,
        int maxResponseBytes,
        DateTimeOffset now)
    {
        if (authType != AuthType)
        {
            SecretCiphertext = null;
            SecretKeyId = null;
            SecretUpdatedAt = null;
            Enabled = false;
        }

        Apply(name, baseUrl, authType, authSettings, defaultHeaders, allowedHeaders, healthPath, timeoutSeconds, maxResponseBytes, now);
        Revision++;
    }

    private void Apply(
        string name,
        string baseUrl,
        ConnectorAuthType authType,
        IReadOnlyDictionary<string, string> authSettings,
        IReadOnlyDictionary<string, string> defaultHeaders,
        IReadOnlyList<string> allowedHeaders,
        string healthPath,
        int timeoutSeconds,
        int maxResponseBytes,
        DateTimeOffset now)
    {
        Name = name;
        BaseUrl = baseUrl;
        AuthType = authType;
        AuthSettingsJson = JsonSerializer.Serialize(authSettings, Json);
        DefaultHeadersJson = JsonSerializer.Serialize(defaultHeaders, Json);
        AllowedHeadersJson = JsonSerializer.Serialize(allowedHeaders, Json);
        HealthPath = healthPath;
        TimeoutSeconds = timeoutSeconds;
        MaxResponseBytes = maxResponseBytes;
        UpdatedAt = now;
    }

    public void SetSecret(string ciphertext, string keyId, DateTimeOffset now)
    {
        SecretCiphertext = ciphertext;
        SecretKeyId = keyId;
        SecretVersion++;
        SecretUpdatedAt = now;
        UpdatedAt = now;
        Revision++;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now;
        Revision++;
    }
}

public enum DefinitionStatus
{
    Draft = 1,
    Published = 2,
    Retired = 3,
}

/// <summary>
/// Uma versão de uma operação do catálogo do tenant (ADR 0035, §1). Rascunho se edita; publicada se executa e NUNCA muda (só se liga e desliga); aposentada fica no histórico. O molde (<see cref="SpecJson"/>) é a única
/// origem do que sai na chamada. Uma chave tem no máximo um rascunho e uma versão publicada (índices únicos parciais no banco).
/// </summary>
public sealed class CapabilityDefinition : ITenantOwned
{
    private CapabilityDefinition()
    {
        Key = string.Empty;
        Name = string.Empty;
        SpecJson = "{}";
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    public string Key { get; private set; }

    public int Version { get; private set; }

    public DefinitionStatus Status { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public CapabilityEffect Effect { get; private set; }

    public bool RequiresConfirmation { get; private set; }

    /// <summary>A classe do dado que a operação devolve (ADR 0036). Decisão do administrador; o padrão seguro é <see cref="CapabilityDataClass.CustomerData"/>.</summary>
    public CapabilityDataClass DataClass { get; private set; } = CapabilityDataClass.CustomerData;

    public CapabilityCaller AllowedCallers { get; private set; }

    public bool Enabled { get; private set; }

    public string SpecJson { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public int Revision { get; private set; } = 1;

    public CapabilityOperationSpec Spec => CapabilitySpecJson.Deserialize(SpecJson);

    public static CapabilityDefinition CreateDraft(
        TenantId tenantId,
        Guid connectionId,
        string key,
        int version,
        string name,
        string? description,
        CapabilityEffect effect,
        bool requiresConfirmation,
        CapabilityCaller allowedCallers,
        CapabilityOperationSpec spec,
        Guid? createdBy,
        DateTimeOffset now,
        CapabilityDataClass dataClass = CapabilityDataClass.CustomerData)
    {
        var definicao = new CapabilityDefinition
        {
            TenantId = tenantId,
            Id = Guid.NewGuid(),
            ConnectionId = connectionId,
            Key = key,
            Version = version,
            Status = DefinitionStatus.Draft,
            Enabled = true,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        definicao.ApplyContent(name, description, effect, requiresConfirmation, allowedCallers, spec, now, dataClass);
        return definicao;
    }

    /// <summary>Edita o RASCUNHO. Uma versão publicada nunca é alterada: a mudança vira uma nova versão.</summary>
    public void UpdateDraft(string name, string? description, CapabilityEffect effect, bool requiresConfirmation, CapabilityCaller allowedCallers, CapabilityOperationSpec spec, DateTimeOffset now, CapabilityDataClass dataClass = CapabilityDataClass.CustomerData)
    {
        if (Status != DefinitionStatus.Draft)
        {
            throw new CapabilitiesConflictException("not-draft", "Só o rascunho pode ser editado: uma versão publicada nunca muda.");
        }

        ApplyContent(name, description, effect, requiresConfirmation, allowedCallers, spec, now, dataClass);
        Revision++;
    }

    private void ApplyContent(string name, string? description, CapabilityEffect effect, bool requiresConfirmation, CapabilityCaller allowedCallers, CapabilityOperationSpec spec, DateTimeOffset now, CapabilityDataClass dataClass)
    {
        DataClass = dataClass;
        Name = name;
        Description = description;
        Effect = effect;
        RequiresConfirmation = effect == CapabilityEffect.Write && requiresConfirmation;
        AllowedCallers = allowedCallers;
        SpecJson = CapabilitySpecJson.Serialize(spec);
        UpdatedAt = now;
    }

    public void Publish(Guid? by, DateTimeOffset now)
    {
        if (Status != DefinitionStatus.Draft)
        {
            throw new CapabilitiesConflictException("not-draft", "Só um rascunho pode ser publicado.");
        }

        Status = DefinitionStatus.Published;
        PublishedAt = now;
        PublishedBy = by;
        UpdatedAt = now;
        Revision++;
    }

    public void Retire(DateTimeOffset now)
    {
        if (Status != DefinitionStatus.Published)
        {
            throw new CapabilitiesConflictException("changed", "Só uma versão publicada pode ser aposentada.");
        }

        Status = DefinitionStatus.Retired;
        UpdatedAt = now;
        Revision++;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now;
        Revision++;
    }
}

/// <summary>
/// O registro de UMA execução (ou de uma escrita que espera confirmação): quem pediu, de qual origem, o atendimento correlacionado, a operação e a versão, o resultado, o código de erro e a duração. NUNCA guarda
/// parâmetros nem resposta; a exceção são os parâmetros de uma confirmação pendente, cifrados e apagados ao concluir. Escrita tem chave de idempotência única por tenant.
/// </summary>
public sealed class CapabilityExecution : ITenantOwned
{
    private CapabilityExecution()
    {
        DefinitionKey = string.Empty;
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid DefinitionId { get; private set; }

    public string DefinitionKey { get; private set; }

    public int DefinitionVersion { get; private set; }

    public Guid ConnectionId { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public CapabilityEffect Effect { get; private set; }

    public CapabilityCaller Caller { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public Guid? ServiceSessionId { get; private set; }

    /// <summary>A correlação opaca de quem chamou (o TurnId de um turno de IA), só para a trilha.</summary>
    public string? CorrelationId { get; private set; }

    public string? IdempotencyKey { get; private set; }

    /// <summary>A impressão digital dos parâmetros (HMAC com a chave do módulo): detecta a mesma chave com parâmetros diferentes sem guardar nem expor os parâmetros.</summary>
    public string? Fingerprint { get; private set; }

    public CapabilityExecutionStatus Status { get; private set; }

    public string? ErrorCode { get; private set; }

    public int? UpstreamStatus { get; private set; }

    public int Attempts { get; private set; }

    public long? DurationMs { get; private set; }

    public bool IsTest { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? ConfirmedBy { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? ConfirmationExpiresAt { get; private set; }

    public string? PendingCiphertext { get; private set; }

    public string? PendingKeyId { get; private set; }

    public int Revision { get; private set; } = 1;

    public static CapabilityExecution Start(
        TenantId tenantId,
        CapabilityDefinition definition,
        Guid? connectionOrganizationId,
        CapabilityCaller caller,
        Guid? requestedBy,
        Guid? serviceSessionId,
        string? idempotencyKey,
        string? fingerprint,
        bool isTest,
        DateTimeOffset now,
        string? correlationId = null) => new()
    {
        CorrelationId = correlationId,
        TenantId = tenantId,
        Id = Guid.NewGuid(),
        DefinitionId = definition.Id,
        DefinitionKey = definition.Key,
        DefinitionVersion = definition.Version,
        ConnectionId = definition.ConnectionId,
        OrganizationId = connectionOrganizationId,
        Effect = definition.Effect,
        Caller = caller,
        RequestedBy = requestedBy,
        ServiceSessionId = serviceSessionId,
        IdempotencyKey = idempotencyKey,
        Fingerprint = fingerprint,
        Status = CapabilityExecutionStatus.InProgress,
        IsTest = isTest,
        CreatedAt = now,
    };

    public void AwaitConfirmation(string ciphertext, string keyId, DateTimeOffset expiresAt)
    {
        Status = CapabilityExecutionStatus.AwaitingConfirmation;
        PendingCiphertext = ciphertext;
        PendingKeyId = keyId;
        ConfirmationExpiresAt = expiresAt;
    }

    /// <summary>Awaiting → em curso. A troca é protegida por <see cref="Revision"/>: duas confirmações simultâneas, só uma vence.</summary>
    public void Confirm(Guid confirmedBy, DateTimeOffset now)
    {
        if (Status != CapabilityExecutionStatus.AwaitingConfirmation)
        {
            throw new CapabilitiesConflictException("already-confirmed", "A execução não está mais aguardando confirmação.");
        }

        Status = CapabilityExecutionStatus.InProgress;
        ConfirmedBy = confirmedBy;
        ConfirmedAt = now;
        // Os parâmetros já foram lidos: a cópia cifrada some AGORA (não sobra dado pessoal guardado enquanto a chamada corre).
        PendingCiphertext = null;
        PendingKeyId = null;
        Revision++;
    }

    public void Complete(CapabilityExecutionStatus status, string? errorCode, int? upstreamStatus, int attempts, long durationMs, DateTimeOffset now)
    {
        Status = status;
        ErrorCode = errorCode;
        UpstreamStatus = upstreamStatus;
        Attempts = attempts;
        DurationMs = durationMs;
        CompletedAt = now;
        PendingCiphertext = null;
        PendingKeyId = null;
        Revision++;
    }

    /// <summary>Cancelada (por uma pessoa ou por validade vencida). Nada foi enviado ao sistema externo.</summary>
    public void Cancel(string? reason, DateTimeOffset now) => Complete(CapabilityExecutionStatus.Cancelled, reason, null, 0, 0, now);
}
