using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.Identity.Infrastructure.Configurations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IaBimeta.Identity.Infrastructure;

/// <summary>
/// Persistência do Embedded. Como o <see cref="AuthenticationStore"/>, contorna o filtro global de forma ESTREITA: toda leitura leva o predicado explícito do tenant informado (as duas exceções, a chave pública e o
/// hash do ticket, são identificadores únicos e não enumeráveis, e o tenant é o da linha achada), e toda gravação roda dentro do escopo de tenant da autenticação, que o <c>SaveChanges</c> continua verificando.
/// </summary>
internal sealed class EmbeddedStore : IEmbeddedStore
{
    private readonly IdentityDbContext _db;
    private readonly IdentityOptions _options;

    public EmbeddedStore(IdentityDbContext db, IdentityOptions options)
    {
        _db = db;
        _options = options;
    }

    // ORDEM CANÔNICA DE TRAVAS (E1-R, segunda rodada). Toda operação que trava mais de uma linha segue a mesma ordem, então duas delas nunca se esperam em ciclo (deadlock):
    //   1. a conexão (integration_connections: FOR UPDATE na criação de chave, FOR KEY SHARE na emissão de ticket, o mesmo modo da FK do INSERT), 2. a chave (integration_keys), 3. o vínculo do usuário externo (external_identities), 4. os tickets (launch_tickets, por id), 5. as sessões (sessions, por id). Travas IMPLÍCITAS de chave estrangeira contam: o INSERT de ticket e de chave toma FOR KEY SHARE na conexão.
    // Cada linha de ticket e de sessão é travada por SELECT ... ORDER BY id FOR UPDATE, nunca na ordem do plano de um UPDATE
    // multilinha. A SEGURANÇA da revogação não depende dessas atualizações em massa: ela está nas marcas (sessions_revoked_at) e na conferência da chave a cada uso; as atualizações só limpam o que já não vale.

    /// <summary>Abre a transação com teto de espera por trava: quem passa do tempo desiste (vira 503) em vez de segurar uma conexão do pool indefinidamente.</summary>
    private async Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);
        var ms = ((int)_options.EmbeddedLockTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('lock_timeout', {ms}, true)", cancellationToken);
        return transacao;
    }

    /// <summary>Contenção transitória do banco vira <see cref="EmbeddedBusyException"/> (503 com Retry-After), nunca 500.</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> operacao)
    {
        try
        {
            return await operacao();
        }
        catch (Exception ex) when (PostgresErrors.IsTransientContention(ex))
        {
            throw new EmbeddedBusyException(ex);
        }
    }

    /// <summary>
    /// Trava as linhas indicadas na ordem do id (ordem canônica) e devolve só as que ainda estão no estado esperado. Roda dentro da transação do chamador.
    /// </summary>
    private async Task LockSessionsAsync(TenantId tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var lista = ids.ToArray();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM identity.sessions WHERE tenant_id = {tenantId.Value} AND id = ANY({lista}) ORDER BY id FOR UPDATE",
            cancellationToken);
    }

    private async Task LockTicketsAsync(TenantId tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var lista = ids.ToArray();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM identity.launch_tickets WHERE tenant_id = {tenantId.Value} AND id = ANY({lista}) ORDER BY id FOR UPDATE",
            cancellationToken);
    }

    /// <summary>Consome (marca como usados) os tickets pendentes que casam com o filtro, travando-os na ordem do id. Cada chamada é uma transação curta própria.</summary>
    private async Task ConsumePendingTicketsAsync(TenantId tenantId, System.Linq.Expressions.Expression<Func<LaunchTicket, bool>> filtro, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transacao = await BeginAsync(cancellationToken);
        var ids = await _db.LaunchTickets.IgnoreQueryFilters().Where(t => t.TenantId == tenantId && t.UsedAt == null).Where(filtro).Select(t => t.Id).ToListAsync(cancellationToken);
        await LockTicketsAsync(tenantId, ids, cancellationToken);
        await _db.LaunchTickets.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && ids.Contains(t.Id) && t.UsedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), cancellationToken);
        await transacao.CommitAsync(cancellationToken);
    }

    /// <summary>Revoga as sessões embutidas ativas que casam com o filtro, travando-as na ordem do id. Devolve quantas revogou.</summary>
    private async Task<int> RevokeSessionsMatchingAsync(TenantId tenantId, System.Linq.Expressions.Expression<Func<Session, bool>> filtro, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transacao = await BeginAsync(cancellationToken);
        var ids = await _db.Sessions.IgnoreQueryFilters().Where(s => s.TenantId == tenantId && s.RevokedAt == null).Where(filtro).Select(s => s.Id).ToListAsync(cancellationToken);
        await LockSessionsAsync(tenantId, ids, cancellationToken);
        var total = await _db.Sessions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && ids.Contains(s.Id) && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        return total;
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

    public Task<bool> TryAddKeyAsync(IntegrationKey key, DateTimeOffset? expireOthersAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuardAsync(() => TryAddKeyCoreAsync(key, expireOthersAt, now, cancellationToken));

    private async Task<bool> TryAddKeyCoreAsync(IntegrationKey key, DateTimeOffset? expireOthersAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(key.TenantId);
        await using var transacao = await BeginAsync(cancellationToken);

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
        // A revogação em si é UMA linha: a da chave. É ela que vale. O UPDATE trava a linha, então a gravação de um ticket (que a trava em FOR SHARE) se enfileira com ela: depois do commit, nenhum ticket nasce desta chave.
        var revogada = await GuardAsync(async () =>
        {
            await using var transacao = await BeginAsync(cancellationToken);
            var alteradas = await _db.IntegrationKeys.IgnoreQueryFilters()
                .Where(k => k.TenantId == tenantId && k.ConnectionId == connectionId && k.Id == keyId && k.Status == IntegrationKeyStatus.Active)
                .ExecuteUpdateAsync(set => set.SetProperty(k => k.Status, IntegrationKeyStatus.Revoked).SetProperty(k => k.RevokedAt, now), cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            return alteradas == 1;
        });
        if (!revogada)
        {
            return false;
        }

        // O que nasceu da chave já não vale: a sessão é conferida contra a chave a cada requisição e os tickets, no uso (troca e resgate de contexto). A limpeza física abaixo só tira o que ficou pendente; cada passo é uma
        // transação curta, fora da trava da chave e na ordem canônica (tickets, depois sessões). Se ela esbarrar em contenção, a revogação continua válida e a limpeza é repetida por qualquer revogação seguinte.
        try
        {
            await ConsumePendingTicketsAsync(tenantId, t => t.KeyId == keyId, now, cancellationToken);
            await RevokeSessionsMatchingAsync(tenantId, s => s.IssuerKeyId == keyId, now, cancellationToken);
        }
        catch (Exception ex) when (PostgresErrors.IsTransientContention(ex))
        {
            // Segurança preservada pela conferência no uso; ver o comentário acima.
        }

        return true;
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

    public Task<bool> AddTicketAsync(LaunchTicket ticket, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            using var escopo = _db.UseAuthenticationTenant(ticket.TenantId);
            await using var transacao = await BeginAsync(cancellationToken);

            if (ticket.KeyId is { } chaveId)
            {
                // Ordem canônica: a conexão ANTES da chave. O INSERT do ticket exige a conexão por FK (FOR KEY SHARE); tomá-la antes evita o ciclo com a criação de chave, que trava a conexão (FOR UPDATE) e depois atualiza a chave.
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM identity.integration_connections WHERE tenant_id = {ticket.TenantId.Value} AND id = {ticket.ConnectionId} FOR KEY SHARE",
                    cancellationToken);

                // Trava a chave em modo compartilhado: emissões simultâneas não se bloqueiam, mas a revogação (que atualiza a linha) espera as emissões em andamento e vice-versa. Depois de obter a trava, relê o estado COMMITADO.
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM identity.integration_keys WHERE tenant_id = {ticket.TenantId.Value} AND id = {chaveId} FOR SHARE",
                    cancellationToken);
                var chave = await _db.IntegrationKeys.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(k => k.TenantId == ticket.TenantId && k.Id == chaveId, cancellationToken);
                if (chave is null || chave.ConnectionId != ticket.ConnectionId || !chave.IsUsableAt(now))
                {
                    return false;
                }
            }

            _db.LaunchTickets.Add(ticket);
            await _db.SaveChangesAsync(cancellationToken);
            await transacao.CommitAsync(cancellationToken);
            return true;
        });

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

    public Task<LaunchTicket?> PeekTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        _db.LaunchTickets.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now, cancellationToken);

    /// <summary>Consome o ticket pelo id DENTRO da transação corrente (UPDATE condicional: no máximo um vencedor). <c>false</c> = já usado, expirado ou inexistente.</summary>
    private async Task<bool> ConsumeTicketInTransactionAsync(TenantId tenantId, Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await _db.LaunchTickets.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && t.Id == ticketId && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), cancellationToken) == 1;

    /// <summary>
    /// Reconfere, DENTRO da transação final, que a chave que emitiu o ticket continua utilizável: trava a linha da chave em modo compartilhado (a revogação, que a atualiza, se enfileira com esta transação) e lê o estado commitado
    /// DEPOIS de obter a trava. Ordem canônica: a chave vem antes do vínculo, dos tickets e das sessões. Ticket sem chave (emitido pela API do próprio produto) não tem o que conferir.
    /// </summary>
    private async Task<bool> TicketKeyStillValidAsync(LaunchTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.KeyId is not { } chaveId)
        {
            return true;
        }

        var estados = await _db.Database
            .SqlQuery<string>(
                $"SELECT status AS \"Value\" FROM identity.integration_keys WHERE tenant_id = {ticket.TenantId.Value} AND id = {chaveId} AND connection_id = {ticket.ConnectionId} FOR SHARE")
            .ToListAsync(cancellationToken);
        return estados.Count == 1 && estados[0] != nameof(IntegrationKeyStatus.Revoked);
    }

    public Task<bool> AddEmbeddedSessionAsync(Session session, string? customerRef, int maxActivePerUser, LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuardAsync(() => AddEmbeddedSessionCoreAsync(session, customerRef, maxActivePerUser, ticket, audit, now, cancellationToken));

    private async Task<bool> AddEmbeddedSessionCoreAsync(Session session, string? customerRef, int maxActivePerUser, LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(session.TenantId);
        await using var transacao = await BeginAsync(cancellationToken);

        // A chave primeiro (ordem canônica): se foi revogada entre a conferência e agora, a troca é recusada e nada é gravado.
        if (!await TicketKeyStillValidAsync(ticket, cancellationToken))
        {
            return false;
        }

        // Trava o vínculo do usuário externo: duas trocas simultâneas dele se enfileiram, e o teto de sessões ativas vale (sem a trava, cada uma contaria antes de a outra gravar).
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM identity.external_identities WHERE tenant_id = {session.TenantId.Value} AND connection_id = {session.IntegrationId} AND external_user_id = {session.ExternalUserId} FOR UPDATE",
            cancellationToken);

        // O ticket é consumido na MESMA transação da sessão (ordem canônica: vínculo, ticket, sessões): se qualquer passo falha, o rollback devolve o ticket a pendente. Quem perde a corrida pelo ticket não grava nada.
        if (!await ConsumeTicketInTransactionAsync(session.TenantId, ticket.Id, now, cancellationToken))
        {
            return false;
        }

        // A auditoria do sucesso entra na MESMA transação (outbox): confirma junto com o efeito ou desfaz junto, e é entregue à trilha depois pelo Worker.
        _db.AuditOutbox.Add(audit);
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
            // Ordem canônica: o vínculo (travado acima) e DEPOIS as sessões por id.
            await LockSessionsAsync(session.TenantId, excedentes, cancellationToken);
            await _db.Sessions.IgnoreQueryFilters()
                .Where(s => s.TenantId == session.TenantId && excedentes.Contains(s.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
        }

        await transacao.CommitAsync(cancellationToken);
        return true;
    }

    public Task<Session?> FindSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        _db.Sessions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken);

    public Task<int> RevokeEmbeddedSessionsAsync(TenantId tenantId, Guid connectionId, string? externalUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            // A marca de revogação vem PRIMEIRO e sozinha (UMA linha, transação própria) e é MONOTÔNICA no banco (nunca retrocede, mesmo que requisições concorrentes terminem fora da ordem em que começaram: vale o maior instante): sessão ou ticket criado até ela não vale mais, mesmo que uma troca em andamento grave a sessão depois (conferência no uso).
            await using (var transacao = await BeginAsync(cancellationToken))
            {
                if (externalUserId is null)
                {
                    await _db.IntegrationConnections.IgnoreQueryFilters()
                        .Where(c => c.TenantId == tenantId && c.Id == connectionId)
                        .ExecuteUpdateAsync(set => set.SetProperty(c => c.SessionsRevokedAt, c => c.SessionsRevokedAt == null || c.SessionsRevokedAt < now ? now : c.SessionsRevokedAt), cancellationToken);
                }
                else
                {
                    await _db.ExternalIdentities.IgnoreQueryFilters()
                        .Where(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.ExternalUserId == externalUserId)
                        .ExecuteUpdateAsync(set => set.SetProperty(i => i.SessionsRevokedAt, i => i.SessionsRevokedAt == null || i.SessionsRevokedAt < now ? now : i.SessionsRevokedAt), cancellationToken);
                }

                await transacao.CommitAsync(cancellationToken);
            }

            // Limpeza física na ordem canônica, uma tabela por transação: tickets pendentes (de abertura e de contexto) e depois as sessões.
            await ConsumePendingTicketsAsync(tenantId, t => t.ConnectionId == connectionId && (externalUserId == null || t.ExternalUserId == externalUserId), now, cancellationToken);
            return await RevokeSessionsMatchingAsync(
                tenantId,
                s => s.Kind == SessionKind.Embedded && s.IntegrationId == connectionId && (externalUserId == null || s.ExternalUserId == externalUserId) && s.ExpiresAt > now,
                now,
                cancellationToken);
        });

    public Task<bool> AddCustomerGrantAsync(TenantId tenantId, Guid sessionId, string customerRef, LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken) =>
        GuardAsync(() => AddCustomerGrantCoreAsync(tenantId, sessionId, customerRef, ticket, audit, now, cancellationToken));

    private async Task<bool> AddCustomerGrantCoreAsync(TenantId tenantId, Guid sessionId, string customerRef, LaunchTicket ticket, PendingAudit audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var escopo = _db.UseAuthenticationTenant(tenantId);
        await using var transacao = await BeginAsync(cancellationToken);

        // A chave primeiro (ordem canônica: chave, tickets, sessões): revogada entre a conferência e agora, o resgate é recusado e nada é gravado.
        if (!await TicketKeyStillValidAsync(ticket, cancellationToken))
        {
            return false;
        }

        // O ticket depois e na MESMA transação do cliente em foco: contenção reverte o consumo e o ticket continua pendente.
        if (!await ConsumeTicketInTransactionAsync(tenantId, ticket.Id, now, cancellationToken))
        {
            return false;
        }

        // Trava a sessão: resgates simultâneos de tickets de contexto da MESMA sessão se enfileiram, e o teto de clientes em foco vale (sem a trava, cada um aparava sem ver o outro).
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM identity.sessions WHERE tenant_id = {tenantId.Value} AND id = {sessionId} FOR UPDATE",
            cancellationToken);

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

        // A auditoria do sucesso entra na MESMA transação (outbox): confirma junto ou desfaz junto, e é entregue à trilha depois pelo Worker.
        _db.AuditOutbox.Add(audit);
        await _db.SaveChangesAsync(cancellationToken);

        await transacao.CommitAsync(cancellationToken);
        return true;
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
