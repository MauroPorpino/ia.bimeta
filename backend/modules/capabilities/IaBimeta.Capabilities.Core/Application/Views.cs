using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;

namespace IaBimeta.Capabilities.Application;

/// <summary>A conexão como a administração a vê. NUNCA traz o segredo (só se há um, a versão e quando foi definido). Os cabeçalhos padrão são públicos: segredo vai só no segredo da conexão.</summary>
public sealed record ConnectionView(
    Guid Id,
    Guid? OrganizationId,
    string Key,
    string Name,
    string Kind,
    string BaseUrl,
    ConnectorAuthType AuthType,
    IReadOnlyDictionary<string, string> AuthSettings,
    IReadOnlyDictionary<string, string> DefaultHeaders,
    IReadOnlyList<string> AllowedHeaders,
    string HealthPath,
    int TimeoutSeconds,
    int MaxResponseBytes,
    bool Enabled,
    bool HasSecret,
    int SecretVersion,
    DateTimeOffset? SecretUpdatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision)
{
    public static ConnectionView From(IntegrationConnection c) => new(
        c.Id, c.OrganizationId, c.Key, c.Name, c.Kind, c.BaseUrl, c.AuthType, c.AuthSettings, c.DefaultHeaders, c.AllowedHeaders, c.HealthPath,
        c.TimeoutSeconds, c.MaxResponseBytes, c.Enabled, c.HasSecret, c.SecretVersion, c.SecretUpdatedAt, c.CreatedAt, c.UpdatedAt, c.Revision);
}

public sealed record DefinitionView(
    Guid Id,
    Guid ConnectionId,
    string Key,
    int Version,
    DefinitionStatus Status,
    string Name,
    string? Description,
    CapabilityEffect Effect,
    bool RequiresConfirmation,
    IReadOnlyList<string> AllowedCallers,
    bool Enabled,
    CapabilityOperationSpec Spec,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    int Revision,
    CapabilityDataClass DataClass = CapabilityDataClass.CustomerData)
{
    public static DefinitionView From(CapabilityDefinition d) => new(
        d.Id, d.ConnectionId, d.Key, d.Version, d.Status, d.Name, d.Description, d.Effect, d.RequiresConfirmation, CallerNames(d.AllowedCallers), d.Enabled, d.Spec, d.CreatedAt, d.PublishedAt, d.Revision, d.DataClass);

    public static IReadOnlyList<string> CallerNames(CapabilityCaller callers) =>
        Enum.GetValues<CapabilityCaller>().Where(c => c != CapabilityCaller.None && callers.HasFlag(c)).Select(c => c.ToString()).ToList();
}

/// <summary>Uma linha do histórico: nunca traz parâmetros nem resposta.</summary>
public sealed record ExecutionView(
    Guid Id,
    Guid DefinitionId,
    string DefinitionKey,
    int DefinitionVersion,
    Guid ConnectionId,
    CapabilityEffect Effect,
    IReadOnlyList<string> Caller,
    Guid? RequestedBy,
    Guid? ServiceSessionId,
    CapabilityExecutionStatus Status,
    string? ErrorCode,
    int? UpstreamStatus,
    int Attempts,
    long? DurationMs,
    bool IsTest,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    Guid? ConfirmedBy,
    DateTimeOffset? ConfirmationExpiresAt,
    string? CorrelationId = null)
{
    public static ExecutionView From(CapabilityExecution e) => new(
        e.Id, e.DefinitionId, e.DefinitionKey, e.DefinitionVersion, e.ConnectionId, e.Effect, DefinitionView.CallerNames(e.Caller), e.RequestedBy, e.ServiceSessionId, e.Status,
        e.ErrorCode, e.UpstreamStatus, e.Attempts, e.DurationMs, e.IsTest, e.CreatedAt, e.CompletedAt, e.ConfirmedBy, e.ConfirmationExpiresAt, e.CorrelationId);
}

public sealed record ExecutionPage(IReadOnlyList<ExecutionView> Items, int Total);

public sealed record ConnectionTestResult(bool Ok, string Outcome, int? UpstreamStatus, long ElapsedMs);

public sealed record ConnectionRequest(
    Guid? OrganizationId,
    string Key,
    string Name,
    string BaseUrl,
    ConnectorAuthType AuthType,
    IReadOnlyDictionary<string, string>? AuthSettings,
    IReadOnlyDictionary<string, string>? DefaultHeaders,
    IReadOnlyList<string>? AllowedHeaders,
    string? HealthPath,
    int? TimeoutSeconds,
    int? MaxResponseBytes);

public sealed record ConnectionUpdate(
    string Name,
    string BaseUrl,
    ConnectorAuthType AuthType,
    IReadOnlyDictionary<string, string>? AuthSettings,
    IReadOnlyDictionary<string, string>? DefaultHeaders,
    IReadOnlyList<string>? AllowedHeaders,
    string? HealthPath,
    int? TimeoutSeconds,
    int? MaxResponseBytes,
    int ExpectedRevision);

/// <summary>O que o administrador escreve ao salvar o rascunho de uma operação. <see cref="Idempotency"/> é obrigatória numa escrita (ARQ-21): declarar <c>None</c> é uma declaração, esquecer não é.</summary>
public sealed record DefinitionRequest(
    Guid ConnectionId,
    string Key,
    string Name,
    string? Description,
    CapabilityEffect Effect,
    bool? RequiresConfirmation,
    IReadOnlyList<CapabilityCaller>? AllowedCallers,
    CapabilityOperationSpec Spec,
    int? ExpectedRevision,
    CapabilityDataClass? DataClass = null);
