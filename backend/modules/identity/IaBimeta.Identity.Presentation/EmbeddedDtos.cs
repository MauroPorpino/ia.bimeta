namespace IaBimeta.Identity.Presentation;

public sealed record CreateConnectionRequest(Guid OrganizationId, string? Name, string? Mode, string? Provisioning, string[]? AllowedOrigins, bool AllowStartConversation);

public sealed record UpdateConnectionRequest(string? Name, string? Mode, string? Provisioning, string[]? AllowedOrigins, bool AllowStartConversation);

public sealed record RotateKeyRequest(double? GraceHours);

public sealed record MapIdentityRequest(string? ExternalUserId, Guid UserId);

public sealed record RevokeEmbeddedSessionsRequest(string? ExternalUserId);

/// <summary>O cliente do ERP em foco (opcional) nas chamadas assinadas do servidor do ERP.</summary>
public sealed record EmbedCustomer(string? Ref);

public sealed record LaunchTicketRequest(string? ExternalUserId, string? DisplayName, string? Challenge, string? ParentOrigin, EmbedCustomer? Customer);

public sealed record ContextTicketRequest(string? ExternalUserId, Guid? SessionId, EmbedCustomer? Customer);

public sealed record RevokeByIntegrationRequest(string? ExternalUserId);

public sealed record ExchangeRequest(string? Ticket, string? Verifier, string? ParentOrigin);

public sealed record RedeemContextRequest(string? Ticket);

/// <summary>Os nomes das políticas de limite de taxa do Embedded. O host registra as políticas com estes nomes: sem elas a rota falha em vez de ficar sem limite.</summary>
public static class EmbeddedRateLimits
{
    /// <summary>Chamadas assinadas do servidor do ERP (por chave).</summary>
    public const string IntegrationPolicy = "embed-integration";

    /// <summary>Troca do ticket no iframe (por origem de rede).</summary>
    public const string ExchangePolicy = "embed-exchange";
}
