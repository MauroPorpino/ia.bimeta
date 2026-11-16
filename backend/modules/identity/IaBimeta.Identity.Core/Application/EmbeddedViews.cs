using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

public sealed record ConnectionView(
    Guid Id, Guid OrganizationId, string Name, EmbeddedMode Mode, ProvisioningMode Provisioning, IReadOnlyList<string> AllowedOrigins, bool AllowStartConversation,
    ConnectionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    internal static ConnectionView From(IntegrationConnection c) =>
        new(c.Id, c.OrganizationId, c.Name, c.Mode, c.Provisioning, c.AllowedOrigins, c.AllowStartConversation, c.Status, c.CreatedAt, c.UpdatedAt);
}

/// <summary>A chave sem o segredo (que nunca mais é mostrado).</summary>
public sealed record KeyView(string KeyId, IntegrationKeyStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)
{
    internal static KeyView From(IntegrationKey k) => new(k.PublicId, k.Status, k.CreatedAt, k.ExpiresAt, k.RevokedAt);
}

/// <summary>A chave recém-criada: o segredo aparece aqui UMA vez. <see cref="OthersExpireAt"/> traz o fim programado das chaves antigas numa rotação.</summary>
public sealed record CreatedKeyView(string KeyId, string Secret, DateTimeOffset CreatedAt, DateTimeOffset? OthersExpireAt);

public sealed record ExternalIdentityView(Guid Id, Guid ConnectionId, string ExternalUserId, Guid UserId, IdentitySource Source, ExternalIdentityStatus Status, DateTimeOffset CreatedAt)
{
    internal static ExternalIdentityView From(ExternalIdentity i) => new(i.Id, i.ConnectionId, i.ExternalUserId, i.UserId, i.Source, i.Status, i.CreatedAt);
}

/// <summary>A conexão que uma chamada assinada do servidor do ERP autenticou. O tenant vem da conexão, nunca da requisição.</summary>
public sealed record AuthenticatedIntegration(
    TenantId TenantId, Guid ConnectionId, Guid KeyId, Guid OrganizationId, EmbeddedMode Mode, ProvisioningMode Provisioning, IReadOnlyList<string> AllowedOrigins, bool AllowStartConversation);

/// <summary>A chamada assinada como o host a viu (o corpo é o bruto, antes de interpretar).</summary>
public sealed record IntegrationSignedRequest(string? KeyId, string? Timestamp, string? Nonce, string? Signature, string Method, string Path, byte[] Body);

public sealed record IssueLaunchTicketRequest(string? ExternalUserId, string? DisplayName, string? Challenge, string? ParentOrigin, string? CustomerRef);

public sealed record TicketResult(string Ticket, DateTimeOffset ExpiresAt);

public sealed record ExchangeUserView(Guid UserId, string DisplayName);

public sealed record ExchangeResult(string Token, DateTimeOffset ExpiresAt, EmbeddedMode Mode, Guid OrganizationId, string? CustomerRef, ExchangeUserView User);

/// <summary>Os clientes em foco da sessão embutida corrente, para a interface.</summary>
public sealed record EmbeddedSessionView(Guid SessionId, EmbeddedMode Mode, Guid OrganizationId, IReadOnlyList<string> CustomerRefs, DateTimeOffset ExpiresAt);

/// <summary>
/// O teto de permissões de uma sessão embutida (ADR 0039, seção 9): só o atendimento, e só na organização da conexão. Mesmo que o usuário tenha mais concessões (outras organizações, escopo de tenant, papéis
/// administrativos), a sessão embutida nunca as carrega. O que o atendente PODE ver continua decidido pelas concessões e pelas filas; isto só limita por cima.
/// </summary>
public static class EmbeddedSessionPolicy
{
    /// <summary>As únicas permissões que uma sessão embutida pode ter. Ampliar esta lista exige ADR (e o teste de arquitetura a protege).</summary>
    public static IReadOnlySet<Permission> AllowedPermissions { get; } = new HashSet<Permission>
    {
        Permission.OrganizationRead,
        Permission.StructureRead,
        Permission.ConversationRead,
        Permission.ConversationManage,
        Permission.ChannelRead,
        Permission.QueueRead,
    };

    /// <summary>As concessões da sessão embutida: as do usuário, só da organização da conexão (nunca escopo de tenant) e só as permissões permitidas.</summary>
    public static IReadOnlyList<PermissionGrant> Filter(IEnumerable<PermissionGrant> userGrants, Guid connectionOrganizationId) =>
        userGrants
            .Where(g => g.OrganizationId == connectionOrganizationId && AllowedPermissions.Contains(g.Permission))
            .Distinct()
            .ToList();

    /// <summary>
    /// Se uma conta pode usar o Embedded: precisa ter concessão na organização da conexão e NÃO pode ser administrativa (papel <see cref="Role.OrganizationAdmin"/> em qualquer organização ou qualquer concessão de
    /// escopo de tenant). Conta administrativa nunca entra por federação, nem mapeada.
    /// </summary>
    public static bool IsEligible(IReadOnlyList<UserOrganizationGrant> grants, Guid connectionOrganizationId, out string reason)
    {
        if (grants.Any(g => g.OrganizationId is null || g.Role == Role.OrganizationAdmin))
        {
            reason = "Conta administrativa não pode ser usada por federação.";
            return false;
        }

        if (!grants.Any(g => g.OrganizationId == connectionOrganizationId))
        {
            reason = "O usuário não tem acesso à organização da conexão.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
