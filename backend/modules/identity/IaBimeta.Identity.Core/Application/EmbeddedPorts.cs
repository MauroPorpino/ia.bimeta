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
    Task AddConnectionAsync(IntegrationConnection connection, PendingAudit audit, CancellationToken cancellationToken);

    /// <summary>A conexão do tenant, ou <c>null</c>. Com <paramref name="forUpdate"/> vem rastreada para alteração (grave com <see cref="SaveChangesAsync"/>).</summary>
    Task<IntegrationConnection?> FindConnectionAsync(TenantId tenantId, Guid connectionId, bool forUpdate, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationConnection>> ListConnectionsAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task SaveChangesAsync(TenantId tenantId, PendingAudit audit, CancellationToken cancellationToken);

    /// <summary>
    /// Grava a mudança de estado da conexão (rastreada) e, se <paramref name="revokeSessionsAt"/> vier, avança a marca de revogação (monotônica) e a auditoria na MESMA transação: ou os três confirmam, ou nenhum.
    /// A limpeza física das sessões é outro passo, repetível.
    /// </summary>
    Task SaveConnectionStatusAsync(TenantId tenantId, Guid connectionId, PendingAudit audit, DateTimeOffset? revokeSessionsAt, CancellationToken cancellationToken);

    /// <summary>A chave e a conexão pelo identificador público da chave (único). Não filtra por tenant: o tenant é o da linha.</summary>
    Task<IntegrationKeyLookup?> FindKeyAsync(Guid keyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationKey>> ListKeysAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Cria a chave sob trava da conexão: recusa se já há <see cref="IntegrationKey.MaxActivePerConnection"/> utilizáveis e, se <paramref name="expireOthersAt"/> for informado, programa o fim das outras utilizáveis
    /// (rotação). Devolve <c>false</c> quando o limite foi atingido.
    /// </summary>
    Task<bool> TryAddKeyAsync(IntegrationKey key, DateTimeOffset? expireOthersAt, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revoga a chave. Devolve <c>false</c> se não existe na conexão do tenant ou já estava revogada.</summary>
    Task<bool> RevokeKeyAsync(TenantId tenantId, Guid connectionId, Guid keyId, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Registra o nonce de forma atômica. Devolve <c>false</c> se já foi usado nesta chave (repetição).</summary>
    Task<bool> TryRecordNonceAsync(TenantId tenantId, Guid keyId, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<ExternalIdentity?> FindIdentityAsync(TenantId tenantId, Guid connectionId, string externalUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalIdentity>> ListIdentitiesAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task<ExternalIdentity?> FindIdentityByIdAsync(TenantId tenantId, Guid connectionId, Guid identityId, CancellationToken cancellationToken);

    /// <summary>Grava o vínculo. Devolve <c>false</c> se o identificador externo ou o usuário já têm vínculo nesta conexão.</summary>
    Task<bool> TryAddIdentityAsync(ExternalIdentity identity, PendingAudit audit, CancellationToken cancellationToken);

    /// <summary>Muda o estado do vínculo. Ao DESATIVAR, o estado, a marca de revogação (monotônica, em <paramref name="now"/>) e a auditoria confirmam na MESMA transação; a limpeza física das sessões é outro passo.</summary>
    /// <param name="admit">Se informada (reativar o vínculo de um usuário federado, que volta a ocupar vaga), é chamada DENTRO da transação e da trava de admissão do tenant; se lançar, nada é gravado.</param>
    Task<bool> SetIdentityStatusAsync(TenantId tenantId, Guid connectionId, Guid identityId, ExternalIdentityStatus status, PendingAudit audit, DateTimeOffset now, Func<CancellationToken, Task>? admit, CancellationToken cancellationToken);

    /// <summary>Cria, juntos ou nenhum, o usuário federado, a concessão e o vínculo. Devolve <c>false</c> se o vínculo já existe (corrida entre duas primeiras entradas): quem chama relê o existente.</summary>
    /// <param name="admit">A verificação de ingresso (habilitação e vaga), chamada DENTRO da transação e da trava de admissão do tenant; se lançar, nada é gravado e a exceção sobe.</param>
    Task<bool> TryProvisionFederatedUserAsync(User user, UserOrganizationGrant grant, ExternalIdentity identity, PendingAudit audit, Func<CancellationToken, Task> admit, CancellationToken cancellationToken);

    /// <summary>
    /// Grava o ticket SOB a trava da chave que o emite: a revogação da chave e a gravação do ticket se enfileiram, então um ticket nunca nasce de uma chave já revogada. Devolve <c>false</c> (e não grava) quando a chave
    /// deixou de ser utilizável (revogada, expirada ou de outra conexão) entre a autenticação da chamada e a gravação.
    /// </summary>
    Task<bool> AddTicketAsync(LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Queima o ticket de forma ATÔMICA: só devolve o ticket para quem o marcou como usado (não usado, não expirado). Usado quando a conferência RECUSA a tentativa (verifier, origem, usuário etc.): uma tentativa errada
    /// também queima o ticket. O caminho de sucesso consome dentro da transação que conclui a operação (<see cref="AddEmbeddedSessionAsync"/>, <see cref="AddCustomerGrantAsync"/>). Não filtra por tenant: o hash é único e não enumerável.
    /// </summary>
    Task<LaunchTicket?> ConsumeTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Lê o ticket pendente (não usado, não expirado) SEM consumi-lo. A conferência do que o ticket diz acontece antes; o consumo é parte da transação que conclui a operação.</summary>
    Task<LaunchTicket?> PeekTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Consome o ticket (<paramref name="ticket"/>) e grava a sessão embutida (e o cliente em foco, se houver) E a entrada de auditoria do sucesso (<paramref name="audit"/>, outbox) na MESMA transação, depois de reconferir que a chave do ticket não foi revogada (devolve <c>false</c> sem gravar se foi), e revoga as mais antigas do mesmo usuário externo na conexão além do limite. Se a gravação falha
    /// (contenção, <see cref="EmbeddedBusyException"/>), nada acontece e o ticket continua pendente: repetir funciona. Devolve <c>false</c> (sem gravar) quando o ticket já não está pendente (outra troca o consumiu antes).
    /// </summary>
    Task<bool> AddEmbeddedSessionAsync(Session session, string? customerRef, int maxActivePerUser, LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken);

    Task<Session?> FindSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Revoga as sessões embutidas ATIVAS da conexão (de um usuário externo, se informado). Devolve quantas revogou.</summary>
    Task<int> RevokeEmbeddedSessionsAsync(TenantId tenantId, Guid connectionId, string? externalUserId, PendingAudit? audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Consome o ticket de contexto (<paramref name="ticket"/>) e acrescenta o cliente em foco à sessão (ou renova a data dele), mantendo no máximo <see cref="SessionCustomerGrant.MaxPerSession"/>, na MESMA transação: se falha por
    /// contenção, o ticket continua pendente. Devolve <c>false</c> (sem gravar) quando o ticket já não está pendente.
    /// </summary>
    Task<bool> AddCustomerGrantAsync(TenantId tenantId, Guid sessionId, string customerRef, LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListCustomerGrantsAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// A entrega, à trilha de auditoria, das entradas do Embedded guardadas na transação do consumo do ticket (<c>identity.audit_outbox</c>, E1-R quarta rodada). Só o Worker a usa. <see cref="FlushAsync"/> roda no contexto de UM
/// tenant (criado pelo host), entrega pelo menos uma vez e só apaga a linha depois de a trilha aceitar; <see cref="ListTenantsWithPendingAsync"/> e <see cref="PendingCountAsync"/> são de sistema (todos os tenants).
/// </summary>
public interface IIdentityAuditOutbox
{
    /// <summary>Tenants com ao menos uma entrada ELEGÍVEL agora (as que esperam o recuo de uma falha não contam), as de pendência mais antiga primeiro.</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithPendingAsync(int take, CancellationToken cancellationToken);

    /// <summary>Quantidade, idade da mais antiga e falhas das entradas ainda não entregues (todos os tenants; sem conteúdo).</summary>
    Task<AuditOutboxStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Entrega até <paramref name="take"/> entradas elegíveis do tenant do contexto. Devolve quantas entregou. Uma entrada que a trilha recusa NÃO é descartada nem trava as demais: registra a falha (contagem, tipo do erro, próxima tentativa
    /// com recuo crescente) e a entrega segue para a próxima.
    /// </summary>
    Task<int> FlushAsync(int take, CancellationToken cancellationToken);
}

/// <summary>Indicadores da outbox de auditoria do Identity: <c>Pending</c> (todas as não entregues), <c>Failing</c> (com ao menos uma falha de entrega), <c>MaxAttempts</c> e <c>OldestCreatedAt</c> (nulo se vazia).</summary>
public sealed record AuditOutboxStatus(int Pending, int Failing, int MaxAttempts, DateTimeOffset? OldestCreatedAt)
{
    public TimeSpan OldestAge(DateTimeOffset now) => OldestCreatedAt is { } mais ? now - mais : TimeSpan.Zero;
}
