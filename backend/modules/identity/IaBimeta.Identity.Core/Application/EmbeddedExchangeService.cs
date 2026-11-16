using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// A troca do ticket por sessão embutida (anônima, no iframe) e o resgate do ticket de contexto (com a sessão embutida). O ticket é consumido de forma atômica ANTES de qualquer outra conferência: uma tentativa errada
/// (verifier, origem do pai, conexão ou usuário) também o queima. Toda recusa é a mesma <see cref="EmbeddedAuthenticationException"/>. O token da sessão só existe na resposta e só em memória do iframe.
/// </summary>
public sealed class EmbeddedExchangeService
{
    private readonly IEmbeddedStore _store;
    private readonly IAuthenticationStore _auth;
    private readonly ITenantDirectory _tenants;
    private readonly IdentityOptions _options;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public EmbeddedExchangeService(
        IEmbeddedStore store, IAuthenticationStore auth, ITenantDirectory tenants, IdentityOptions options, IRequestContextAccessor context, TimeProvider clock, IAuditWriter audit)
    {
        _store = store;
        _auth = auth;
        _tenants = tenants;
        _options = options;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="EmbeddedAuthenticationException">Qualquer recusa (sempre a mesma).</exception>
    public async Task<ExchangeResult> ExchangeAsync(string? ticket, string? verifier, string? parentOrigin, CancellationToken cancellationToken = default)
    {
        if (!TicketTokens.TryHash(ticket, out var hash) || !PkceVerifier.IsValidVerifier(verifier) || !IntegrationConnection.TryNormalizeOrigin(parentOrigin, out var origem))
        {
            throw new EmbeddedAuthenticationException();
        }

        var agora = _clock.GetUtcNow();
        var consumido = await _store.ConsumeTicketAsync(hash, agora, cancellationToken) ?? throw new EmbeddedAuthenticationException();
        var tenantId = consumido.TenantId;

        try
        {
            if (consumido.Kind != TicketKind.Launch
                || consumido.Challenge is null
                || consumido.ParentOrigin is null
                || !PkceVerifier.Matches(consumido.Challenge, verifier!)
                || !string.Equals(consumido.ParentOrigin, origem, StringComparison.Ordinal))
            {
                throw new EmbeddedAuthenticationException();
            }

            var conexao = await _store.FindConnectionAsync(tenantId, consumido.ConnectionId, forUpdate: false, cancellationToken);
            var tenant = await _tenants.FindByIdAsync(tenantId, cancellationToken);
            if (conexao is null || !conexao.IsActive || !conexao.IsOriginAllowed(origem) || tenant is not { Status: TenantStatus.Active })
            {
                throw new EmbeddedAuthenticationException();
            }

            var vinculo = await _store.FindIdentityAsync(tenantId, conexao.Id, consumido.ExternalUserId, cancellationToken);
            var usuario = await _auth.FindUserByIdAsync(tenantId, consumido.UserId, cancellationToken);
            if (vinculo is not { IsActive: true } || vinculo.UserId != consumido.UserId || usuario is null)
            {
                throw new EmbeddedAuthenticationException();
            }

            var concessoes = await _auth.ListGrantsAsync(tenantId, usuario.Id, cancellationToken);
            if (!EmbeddedSessionPolicy.IsEligible(concessoes, conexao.OrganizationId, out _))
            {
                throw new EmbeddedAuthenticationException();
            }

            var (token, tokenHash) = SessionTokens.Generate();
            var sessao = Session.CreateEmbedded(tenantId, usuario.Id, tokenHash, agora, _options.EmbeddedSessionLifetime, conexao.Id, consumido.ExternalUserId, origem);
            await _store.AddEmbeddedSessionAsync(sessao, consumido.CustomerRef, _options.MaxEmbeddedSessionsPerUser, agora, cancellationToken);

            await _audit.WriteForAuthenticationAsync(tenantId, usuario.Id, new AuditEntry("identity.embed.exchange", "session", sessao.Id.ToString(), AuditOutcome.Success), cancellationToken);
            return new ExchangeResult(token, sessao.ExpiresAt, conexao.Mode, conexao.OrganizationId, consumido.CustomerRef, new ExchangeUserView(usuario.Id, usuario.DisplayName));
        }
        catch (EmbeddedAuthenticationException)
        {
            await _audit.WriteForAuthenticationAsync(tenantId, consumido.UserId, new AuditEntry("identity.embed.exchange", "launch_ticket", consumido.Id.ToString(), AuditOutcome.Denied), cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Resgata o ticket de contexto com a sessão embutida corrente e acrescenta o cliente em foco. O ticket precisa ser do mesmo usuário externo e da mesma conexão da sessão.
    /// </summary>
    /// <exception cref="EmbeddedAuthenticationException">O ticket é inválido ou não pertence a esta sessão.</exception>
    public async Task<string> RedeemContextTicketAsync(string? ticket, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var principal = contexto.RequirePrincipal();

        if (!TicketTokens.TryHash(ticket, out var hash))
        {
            throw new EmbeddedAuthenticationException();
        }

        var agora = _clock.GetUtcNow();
        var consumido = await _store.ConsumeTicketAsync(hash, agora, cancellationToken) ?? throw new EmbeddedAuthenticationException();
        var sessao = await _store.FindSessionAsync(contexto.TenantId, principal.SessionId, cancellationToken);

        // O ticket de OUTRO tenant nunca vale aqui (o consumo é global por hash, a conferência é do tenant da sessão).
        if (consumido.TenantId != contexto.TenantId
            || consumido.Kind != TicketKind.Context
            || sessao is not { Kind: SessionKind.Embedded }
            || sessao.IntegrationId != consumido.ConnectionId
            || sessao.UserId != consumido.UserId
            || !string.Equals(sessao.ExternalUserId, consumido.ExternalUserId, StringComparison.Ordinal)
            || consumido.CustomerRef is null)
        {
            await _audit.WriteAsync(new AuditEntry("identity.embed.context", "session", principal.SessionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new EmbeddedAuthenticationException();
        }

        await _store.AddCustomerGrantAsync(contexto.TenantId, sessao.Id, consumido.CustomerRef, agora, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("identity.embed.context", "session", sessao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return consumido.CustomerRef;
    }

    /// <summary>A sessão embutida corrente (modo, organização e clientes em foco), para a interface. Sessão interativa devolve <c>null</c>.</summary>
    public async Task<EmbeddedSessionView?> GetCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var principal = contexto.RequirePrincipal();
        var sessao = await _store.FindSessionAsync(contexto.TenantId, principal.SessionId, cancellationToken);
        if (sessao is not { Kind: SessionKind.Embedded } || sessao.IntegrationId is not { } conexaoId)
        {
            return null;
        }

        var conexao = await _store.FindConnectionAsync(contexto.TenantId, conexaoId, forUpdate: false, cancellationToken);
        if (conexao is null)
        {
            return null;
        }

        var clientes = await _store.ListCustomerGrantsAsync(contexto.TenantId, sessao.Id, cancellationToken);
        return new EmbeddedSessionView(sessao.Id, conexao.Mode, conexao.OrganizationId, clientes, sessao.ExpiresAt);
    }
}
