using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>A chave e a conexão achadas pelo identificador público da chave (antes de existir contexto).</summary>
public sealed record IntegrationKeyLookup(IntegrationKey Key, IntegrationConnection Connection);

/// <summary>A que um segredo de integração pertence. Entra como dado autenticado da cifra: o texto cifrado não abre sob outro tenant, outra conexão nem outra chave.</summary>
public sealed record IntegrationSecretBinding(TenantId TenantId, Guid ConnectionId, Guid KeyId);

public sealed record ProtectedIntegrationSecret(string MasterKeyId, string Ciphertext);

/// <summary>A cifra autenticada dos segredos das chaves de integração (chaves mestras próprias, <c>Identity:Secrets</c>). Sem chave ativa configurada, falha fechada.</summary>
public interface IIntegrationSecretProtector
{
    bool IsConfigured { get; }

    /// <exception cref="EmbeddedUnavailableException">Não há chave mestra ativa configurada.</exception>
    ProtectedIntegrationSecret Protect(string plaintext, IntegrationSecretBinding binding);

    /// <exception cref="EmbeddedUnavailableException">A chave mestra não está configurada ou o texto foi alterado ou pertence a outro contexto.</exception>
    string Unprotect(ProtectedIntegrationSecret secret, IntegrationSecretBinding binding);
}

/// <summary>
/// Porta de persistência do Embedded. Toda operação leva o tenant EXPLÍCITO (predicado explícito, sem filtro global, como o <c>IAuthenticationStore</c>), porque a federação roda antes de existir contexto de
/// usuário e a chave de uma conexão define o tenant. As únicas buscas sem tenant são por identificadores não enumeráveis: a chave pública (<see cref="FindKeyAsync"/>) e o hash do ticket
/// (<see cref="ConsumeTicketAsync"/>); o tenant vem da linha encontrada.
/// </summary>
public interface IEmbeddedStore
{
    Task AddConnectionAsync(IntegrationConnection connection, CancellationToken cancellationToken);

    /// <summary>A conexão do tenant, ou <c>null</c>. Com <paramref name="forUpdate"/> vem rastreada para alteração (grave com <see cref="SaveChangesAsync"/>).</summary>
    Task<IntegrationConnection?> FindConnectionAsync(TenantId tenantId, Guid connectionId, bool forUpdate, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationConnection>> ListConnectionsAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task SaveChangesAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>A chave e a conexão pelo identificador público da chave (único). Não filtra por tenant: o tenant é o da linha.</summary>
    Task<IntegrationKeyLookup?> FindKeyAsync(Guid keyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationKey>> ListKeysAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Cria a chave sob trava da conexão: recusa se já há <see cref="IntegrationKey.MaxActivePerConnection"/> utilizáveis e, se <paramref name="expireOthersAt"/> for informado, programa o fim das outras utilizáveis
    /// (rotação). Devolve <c>false</c> quando o limite foi atingido.
    /// </summary>
    Task<bool> TryAddKeyAsync(IntegrationKey key, DateTimeOffset? expireOthersAt, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revoga a chave. Devolve <c>false</c> se não existe na conexão do tenant ou já estava revogada.</summary>
    Task<bool> RevokeKeyAsync(TenantId tenantId, Guid connectionId, Guid keyId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Registra o nonce de forma atômica. Devolve <c>false</c> se já foi usado nesta chave (repetição).</summary>
    Task<bool> TryRecordNonceAsync(TenantId tenantId, Guid keyId, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<ExternalIdentity?> FindIdentityAsync(TenantId tenantId, Guid connectionId, string externalUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalIdentity>> ListIdentitiesAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task<ExternalIdentity?> FindIdentityByIdAsync(TenantId tenantId, Guid connectionId, Guid identityId, CancellationToken cancellationToken);

    /// <summary>Grava o vínculo. Devolve <c>false</c> se o identificador externo ou o usuário já têm vínculo nesta conexão.</summary>
    Task<bool> TryAddIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken);

    Task<bool> SetIdentityStatusAsync(TenantId tenantId, Guid connectionId, Guid identityId, ExternalIdentityStatus status, CancellationToken cancellationToken);

    /// <summary>Cria, juntos ou nenhum, o usuário federado, a concessão e o vínculo. Devolve <c>false</c> se o vínculo já existe (corrida entre duas primeiras entradas): quem chama relê o existente.</summary>
    Task<bool> TryProvisionFederatedUserAsync(User user, UserOrganizationGrant grant, ExternalIdentity identity, CancellationToken cancellationToken);

    Task AddTicketAsync(LaunchTicket ticket, CancellationToken cancellationToken);

    /// <summary>
    /// Consome o ticket de forma ATÔMICA: só devolve o ticket para quem o marcou como usado (não usado, não expirado). Duas trocas simultâneas têm no máximo um vencedor; uma tentativa errada também queima o
    /// ticket (quem chama confere o resto depois de consumir). Não filtra por tenant: o hash é único e não enumerável.
    /// </summary>
    Task<LaunchTicket?> ConsumeTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Grava a sessão embutida (e o cliente em foco, se houver) e revoga as mais antigas do mesmo usuário externo na conexão além do limite.</summary>
    Task AddEmbeddedSessionAsync(Session session, string? customerRef, int maxActivePerUser, DateTimeOffset now, CancellationToken cancellationToken);

    Task<Session?> FindSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Revoga as sessões embutidas ATIVAS da conexão (de um usuário externo, se informado). Devolve quantas revogou.</summary>
    Task<int> RevokeEmbeddedSessionsAsync(TenantId tenantId, Guid connectionId, string? externalUserId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Acrescenta o cliente em foco à sessão (ou renova a data dele) e mantém no máximo <see cref="SessionCustomerGrant.MaxPerSession"/>.</summary>
    Task AddCustomerGrantAsync(TenantId tenantId, Guid sessionId, string customerRef, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListCustomerGrantsAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken);
}
