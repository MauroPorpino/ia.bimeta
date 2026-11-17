using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Tests;

/// <summary>
/// O <see cref="IEmbeddedStore"/> em memória: a MESMA semântica da persistência (tenant explícito, consumo único do ticket, vínculo único, limite de chaves), sem banco. A atomicidade e a concorrência reais são provadas com
/// PostgreSQL em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
internal sealed class EmbeddedEmMemoria : IEmbeddedStore
{
    public List<IntegrationConnection> Conexoes { get; } = new();

    public List<IntegrationKey> Chaves { get; } = new();

    public List<ExternalIdentity> Vinculos { get; } = new();

    public List<LaunchTicket> Tickets { get; } = new();

    public List<Session> Sessoes { get; } = new();

    public List<(TenantId Tenant, Guid Sessao, string Cliente)> Clientes { get; } = new();

    public List<(Guid Chave, string Nonce)> Nonces { get; } = new();

    public List<(User Usuario, UserOrganizationGrant Concessao)> Provisionados { get; } = new();

    public IdentidadeEmMemoria? Identidade { get; set; }

    public Task AddConnectionAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        Conexoes.Add(connection);
        return Task.CompletedTask;
    }

    public Task<IntegrationConnection?> FindConnectionAsync(TenantId tenantId, Guid connectionId, bool forUpdate, CancellationToken cancellationToken) =>
        Task.FromResult(Conexoes.FirstOrDefault(c => c.TenantId == tenantId && c.Id == connectionId));

    public Task<IReadOnlyList<IntegrationConnection>> ListConnectionsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationConnection>>(Conexoes.Where(c => c.TenantId == tenantId).ToList());

    public Task SaveChangesAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IntegrationKeyLookup?> FindKeyAsync(Guid keyId, CancellationToken cancellationToken)
    {
        var chave = Chaves.FirstOrDefault(k => k.Id == keyId);
        var conexao = chave is null ? null : Conexoes.FirstOrDefault(c => c.TenantId == chave.TenantId && c.Id == chave.ConnectionId);
        return Task.FromResult(chave is null || conexao is null ? null : new IntegrationKeyLookup(chave, conexao));
    }

    public Task<IReadOnlyList<IntegrationKey>> ListKeysAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationKey>>(Chaves.Where(k => k.TenantId == tenantId && k.ConnectionId == connectionId).ToList());

    public Task<bool> TryAddKeyAsync(IntegrationKey key, DateTimeOffset? expireOthersAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utilizaveis = Chaves.Where(k => k.TenantId == key.TenantId && k.ConnectionId == key.ConnectionId && k.IsUsableAt(now)).ToList();
        if (utilizaveis.Count >= IntegrationKey.MaxActivePerConnection)
        {
            return Task.FromResult(false);
        }

        if (expireOthersAt is { } fim)
        {
            utilizaveis.ForEach(k => k.ExpireAt(fim));
        }

        Chaves.Add(key);
        return Task.FromResult(true);
    }

    public Task<bool> RevokeKeyAsync(TenantId tenantId, Guid connectionId, Guid keyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var chave = Chaves.FirstOrDefault(k => k.TenantId == tenantId && k.ConnectionId == connectionId && k.Id == keyId);
        var revogada = chave is not null && chave.Revoke(now);
        if (revogada)
        {
            Sessoes.Where(s => s.TenantId == tenantId && s.IssuerKeyId == keyId).ToList().ForEach(s => s.Revoke(now));
            Tickets.Where(t => t.TenantId == tenantId && t.KeyId == keyId).ToList().ForEach(t => TicketsUsados.TryAdd(t.Id, now));
        }

        return Task.FromResult(revogada);
    }

    public Task<bool> TryRecordNonceAsync(TenantId tenantId, Guid keyId, string nonce, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (Nonces.Contains((keyId, nonce)))
        {
            return Task.FromResult(false);
        }

        Nonces.Add((keyId, nonce));
        return Task.FromResult(true);
    }

    public Task<ExternalIdentity?> FindIdentityAsync(TenantId tenantId, Guid connectionId, string externalUserId, CancellationToken cancellationToken) =>
        Task.FromResult(Vinculos.FirstOrDefault(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.ExternalUserId == externalUserId));

    public Task<IReadOnlyList<ExternalIdentity>> ListIdentitiesAsync(TenantId tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalIdentity>>(Vinculos.Where(i => i.TenantId == tenantId && i.ConnectionId == connectionId).ToList());

    public Task<ExternalIdentity?> FindIdentityByIdAsync(TenantId tenantId, Guid connectionId, Guid identityId, CancellationToken cancellationToken) =>
        Task.FromResult(Vinculos.FirstOrDefault(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.Id == identityId));

    public Task<bool> TryAddIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        if (Vinculos.Any(i => i.TenantId == identity.TenantId && i.ConnectionId == identity.ConnectionId && (i.ExternalUserId == identity.ExternalUserId || i.UserId == identity.UserId)))
        {
            return Task.FromResult(false);
        }

        Vinculos.Add(identity);
        return Task.FromResult(true);
    }

    public Task<bool> SetIdentityStatusAsync(TenantId tenantId, Guid connectionId, Guid identityId, ExternalIdentityStatus status, CancellationToken cancellationToken)
    {
        var vinculo = Vinculos.FirstOrDefault(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.Id == identityId);
        vinculo?.SetStatus(status);
        return Task.FromResult(vinculo is not null);
    }

    public Task<bool> TryProvisionFederatedUserAsync(User user, UserOrganizationGrant grant, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        if (Vinculos.Any(i => i.TenantId == identity.TenantId && i.ConnectionId == identity.ConnectionId && i.ExternalUserId == identity.ExternalUserId))
        {
            return Task.FromResult(false);
        }

        Identidade?.Usuarios.Add(user);
        Identidade?.Concessoes.Add(grant);
        Provisionados.Add((user, grant));
        Vinculos.Add(identity);
        return Task.FromResult(true);
    }

    public Task<bool> AddTicketAsync(LaunchTicket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ticket.KeyId is { } chaveId && Chaves.FirstOrDefault(k => k.Id == chaveId) is { } chave && !chave.IsUsableAt(now))
        {
            return Task.FromResult(false);
        }

        Tickets.Add(ticket);
        return Task.FromResult(true);
    }

    public Dictionary<Guid, DateTimeOffset> TicketsUsados { get; } = new();

    public Task<LaunchTicket?> ConsumeTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ticket = Tickets.FirstOrDefault(t => t.TokenHash.AsSpan().SequenceEqual(tokenHash));
        if (ticket is null || TicketsUsados.ContainsKey(ticket.Id) || ticket.ExpiresAt <= now)
        {
            return Task.FromResult<LaunchTicket?>(null);
        }

        TicketsUsados[ticket.Id] = now;
        return Task.FromResult<LaunchTicket?>(ticket);
    }

    /// <summary>Quando definida, a gravação que conclui a troca/resgate falha com ela (como uma contenção do banco). Modela o rollback: nada é consumido nem gravado.</summary>
    public Exception? FalhaAoConcluir { get; set; }

    public Task<LaunchTicket?> PeekTicketAsync(byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ticket = Tickets.FirstOrDefault(t => t.TokenHash.AsSpan().SequenceEqual(tokenHash));
        return Task.FromResult(ticket is null || TicketsUsados.ContainsKey(ticket.Id) || ticket.ExpiresAt <= now ? null : ticket);
    }

    private bool ConsumirNaTransacao(Guid ticketId, DateTimeOffset now)
    {
        var ticket = Tickets.FirstOrDefault(t => t.Id == ticketId);
        if (ticket is null || TicketsUsados.ContainsKey(ticketId) || ticket.ExpiresAt <= now)
        {
            return false;
        }

        TicketsUsados[ticketId] = now;
        return true;
    }

    public Task<bool> AddEmbeddedSessionAsync(Session session, string? customerRef, int maxActivePerUser, Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (FalhaAoConcluir is { } falha)
        {
            throw falha;
        }

        if (!ConsumirNaTransacao(ticketId, now))
        {
            return Task.FromResult(false);
        }

        Sessoes.Add(session);
        Identidade?.Sessoes.Add(session);
        if (customerRef is not null)
        {
            Clientes.Add((session.TenantId, session.Id, customerRef));
        }

        return Task.FromResult(true);
    }

    public Task<Session?> FindSessionAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Sessoes.FirstOrDefault(s => s.TenantId == tenantId && s.Id == sessionId));

    public Task<int> RevokeEmbeddedSessionsAsync(TenantId tenantId, Guid connectionId, string? externalUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alvo = Sessoes.Where(s => s.TenantId == tenantId && s.IntegrationId == connectionId && (externalUserId is null || s.ExternalUserId == externalUserId) && s.IsActiveAt(now)).ToList();
        if (externalUserId is null)
        {
            Conexoes.FirstOrDefault(c => c.TenantId == tenantId && c.Id == connectionId)?.MarkSessionsRevoked(now);
        }
        else
        {
            Vinculos.FirstOrDefault(i => i.TenantId == tenantId && i.ConnectionId == connectionId && i.ExternalUserId == externalUserId)?.MarkSessionsRevoked(now);
        }

        Tickets.Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId && (externalUserId is null || t.ExternalUserId == externalUserId)).ToList().ForEach(t => TicketsUsados.TryAdd(t.Id, now));
        alvo.ForEach(s => s.Revoke(now));
        return Task.FromResult(alvo.Count);
    }

    public Task<bool> AddCustomerGrantAsync(TenantId tenantId, Guid sessionId, string customerRef, Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (FalhaAoConcluir is { } falha)
        {
            throw falha;
        }

        if (!ConsumirNaTransacao(ticketId, now))
        {
            return Task.FromResult(false);
        }

        if (!Clientes.Contains((tenantId, sessionId, customerRef)))
        {
            Clientes.Add((tenantId, sessionId, customerRef));
        }

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> ListCustomerGrantsAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Clientes.Where(g => g.Tenant == tenantId && g.Sessao == sessionId).Select(g => g.Cliente).ToList());
}

/// <summary>Um protetor de segredo reversível só para teste (não é cifra): confere o vínculo, para provar que o texto não abre sob outro tenant, conexão ou chave.</summary>
internal sealed class ProtetorDeTeste : IIntegrationSecretProtector
{
    public bool IsConfigured { get; set; } = true;

    public ProtectedIntegrationSecret Protect(string plaintext, IntegrationSecretBinding binding)
    {
        if (!IsConfigured)
        {
            throw new EmbeddedUnavailableException("secrets-unavailable");
        }

        return new ProtectedIntegrationSecret("t1", $"v1:{binding.TenantId.Value:N}:{binding.ConnectionId:N}:{binding.KeyId:N}:{plaintext}");
    }

    public string Unprotect(ProtectedIntegrationSecret secret, IntegrationSecretBinding binding)
    {
        var partes = secret.Ciphertext.Split(':', 5);
        if (partes.Length != 5 || partes[1] != binding.TenantId.Value.ToString("N") || partes[2] != binding.ConnectionId.ToString("N") || partes[3] != binding.KeyId.ToString("N"))
        {
            throw new EmbeddedUnavailableException("secret-invalid");
        }

        return partes[4];
    }
}
