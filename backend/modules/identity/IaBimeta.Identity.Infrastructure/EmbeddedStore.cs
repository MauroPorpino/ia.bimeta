using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// Persistência do Embedded. Como o <see cref="AuthenticationStore"/>, contorna o filtro global de forma ESTREITA: toda leitura leva o predicado explícito do tenant informado (as duas exceções, a chave pública e o
/// hash do ticket, são identificadores únicos e não enumeráveis, e o tenant é o da linha achada), e toda gravação roda dentro do escopo de tenant da autenticação, que o <c>SaveChanges</c> continua verificando.
/// </summary>
internal sealed class EmbeddedStore : IEmbeddedStore
{
    private readonly IdentityDbContext _db;

    public EmbeddedStore(IdentityDbContext db)
    {
        _db = db;
    }

    public async Task AddConnectionAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(connection.TenantId);
        _db.IntegrationConnections.Add(connection);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<IntegrationConnection?> FindConnectionAsync(TenantId tenantId, Guid connectionId, bool forUpdate, CancellationToken cancellationToken)
    {
        var consulta = _db.IntegrationConnections.IgnoreQueryFilters().Where(c => c.TenantId == tenantId && c.Id == connectionId);
        return (forUpdate ? consulta : consulta.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationConnection>> ListConnectionsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await _db.IntegrationConnections.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task SaveChangesAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(tenantId);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IntegrationKeyLookup?> FindKeyAsync(Guid keyId, CancellationToken cancellationToken)
    {
        var chave = await _db.IntegrationKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (chave is null)
        {
            return null;
        }

        var conexao = await _db.IntegrationConnections.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == chave.TenantId && c.Id == chave.ConnectionId, cancellationToken);
        return conexao is null ? null : new IntegrationKeyLookup(chave, conexao);
    }

    public async Task<IReadOnlyList<IntegrationKey>> ListKeysAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await _db.IntegrationKeys.IgnoreQueryFilters().AsNoTracking()
            .Where(k => k.TenantId == tenantId && k.ConnectionId == connectionId)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryAddKeyAsync(IntegrationKey key, DateTimeOffset? expireOthersAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(key.TenantId);
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Trava a conexão: duas criações simultâneas de chave se enfileiram, e o limite de chaves ativas vale.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM identity.integration_connections WHERE tenant_id = {key.TenantId.Value} AND id = {key.ConnectionId} FOR UPDATE",
            cancellationToken);

        var utilizaveis = await _db.IntegrationKeys.IgnoreQueryFilters()
            .Where(k => k.TenantId == key.TenantId && k.ConnectionId == key.ConnectionId && k.Status == IntegrationKeyStatus.Active)
            .ToListAsync(cancellationToken);
        utilizaveis = utilizaveis.Where(k => k.IsUsableAt(now)).ToList();
        if (utilizaveis.Count >= IntegrationKey.MaxActivePerConnection)
        {
            return false;
        }

        if (expireOthersAt is { } fim)
        {
            foreach (var antiga in utilizaveis)
            {
                antiga.ExpireAt(fim);
            }
        }

        _db.IntegrationKeys.Add(key);
        await _db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeKeyAsync(TenantId tenantId, Guid connectionId, Guid keyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alteradas = await _db.IntegrationKeys.IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId && k.ConnectionId == connectionId && k.Id == keyId && k.Status == IntegrationKeyStatus.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(k => k.Status, IntegrationKeyStatus.Revoked).SetProperty(k => k.RevokedAt, now), cancellationToken);
        return alteradas == 1;
    }

    public async Task<bool> TryRecordNonceAsync(TenantId tenantId, Guid keyId, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        // Atômico: o índice único (key_id, nonce) tem no máximo um vencedor.
        var inseridas = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO identity.integration_nonces (tenant_id, key_id, nonce, expires_at)
               VALUES ({tenantId.Value}, {keyId}, {nonce}, {expiresAt})
               ON CONFLICT (key_id, nonce) DO NOTHING",
            cancellationToken);
        return inseridas == 1;
    }

    public Task<ExternalIdentity?> FindIdentityAsync(TenantId tenantId, Guid connectionId, string externalUserId, CancellationToken cancellationToken) =>
        _db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.ExternalUserId == externalUserId, cancellationToken);

    public async Task<IReadOnlyList<ExternalIdentity>> ListIdentitiesAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        await _db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.ConnectionId == connectionId)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<ExternalIdentity?> FindIdentityByIdAsync(TenantId tenantId, Guid connectionId, Guid identityId, CancellationToken cancellationToken) =>
        _db.ExternalIdentities.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.Id == identityId, cancellationToken);

    public async Task<bool> TryAddIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(identity.TenantId);
        _db.ExternalIdentities.Add(identity);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsIdentityConflict(ex))
        {
            _db.Entry(identity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> SetIdentityStatusAsync(TenantId tenantId, Guid connectionId, Guid identityId, ExternalIdentityStatus status, CancellationToken cancellationToken)
    {
        var alteradas = await _db.ExternalIdentities.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.Id == identityId)
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.Status, status), cancellationToken);
        return alteradas == 1;
    }

    public async Task<bool> TryProvisionFederatedUserAsync(User user, UserOrganizationGrant grant, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(identity.TenantId);
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        _db.Users.Add(user);
        _db.Grants.Add(grant);
        _db.ExternalIdentities.Add(identity);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsIdentityConflict(ex))
        {
            await transacao.RollbackAsync(cancellationToken);
            _db.Entry(identity).State = EntityState.Detached;
            _db.Entry(grant).State = EntityState.Detached;
            _db.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    public async Task AddTicketAsync(LaunchTicket ticket, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(ticket.TenantId);
        _db.LaunchTickets.Add(ticket);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LaunchTicket?> ConsumeTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Atômico: o UPDATE condicional tem no máximo um vencedor (UsedAt é a marca de uso; ExpiresAt fecha o resto).
        var alteradas = await _db.LaunchTickets.IgnoreQueryFilters()
            .Where(t => t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), cancellationToken);
        if (alteradas != 1)
        {
            return null;
        }

        return await _db.LaunchTickets.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
    }

    public async Task AddEmbeddedSessionAsync(Session session, string? customerRef, int maxActivePerUser, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(session.TenantId);
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        _db.Sessions.Add(session);
        if (customerRef is not null)
        {
            _db.SessionCustomerGrants.Add(SessionCustomerGrant.Create(session.TenantId, session.Id, customerRef, now));
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Várias abas do ERP: mantém só as mais recentes do mesmo usuário externo na conexão; as mais antigas são revogadas.
        var excedentes = await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.TenantId == session.TenantId && s.IntegrationId == session.IntegrationId && s.ExternalUserId == session.ExternalUserId && s.Kind == SessionKind.Embedded && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip(Math.Max(1, maxActivePerUser))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (excedentes.Count > 0)
        {
            await _db.Sessions.IgnoreQueryFilters()
                .Where(s => s.TenantId == session.TenantId && excedentes.Contains(s.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
        }

        await transacao.CommitAsync(cancellationToken);
    }

    public Task<Session?> FindSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        _db.Sessions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken);

    public async Task<int> RevokeEmbeddedSessionsAsync(TenantId tenantId, Guid connectionId, string? externalUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Kind == SessionKind.Embedded && s.IntegrationId == connectionId && (externalUserId == null || s.ExternalUserId == externalUserId) && s.RevokedAt == null && s.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);

    public async Task AddCustomerGrantAsync(TenantId tenantId, Guid sessionId, string customerRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO identity.session_customer_grants (tenant_id, session_id, customer_ref, granted_at)
               VALUES ({tenantId.Value}, {sessionId}, {customerRef}, {now})
               ON CONFLICT (tenant_id, session_id, customer_ref) DO UPDATE SET granted_at = EXCLUDED.granted_at",
            cancellationToken);

        // Mantém só os mais recentes.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM identity.session_customer_grants
               WHERE tenant_id = {tenantId.Value} AND session_id = {sessionId}
                 AND customer_ref NOT IN (
                     SELECT customer_ref FROM identity.session_customer_grants
                     WHERE tenant_id = {tenantId.Value} AND session_id = {sessionId}
                     ORDER BY granted_at DESC, customer_ref LIMIT {SessionCustomerGrant.MaxPerSession})",
            cancellationToken);

        await transacao.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListCustomerGrantsAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        await _db.SessionCustomerGrants.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.SessionId == sessionId)
            .OrderByDescending(g => g.GrantedAt).ThenBy(g => g.CustomerRef)
            .Select(g => g.CustomerRef)
            .ToListAsync(cancellationToken);

    private static bool IsIdentityConflict(DbUpdateException ex) =>
        PostgresErrors.IsUniqueViolation(ex, ExternalIdentityConfiguration.UniqueExternalUserIndex)
        || PostgresErrors.IsUniqueViolation(ex, ExternalIdentityConfiguration.UniqueUserIndex);
}
