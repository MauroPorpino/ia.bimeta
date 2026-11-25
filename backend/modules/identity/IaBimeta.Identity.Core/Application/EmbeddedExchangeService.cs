using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// A troca do ticket por sessão embutida (anônima, no iframe) e o resgate do ticket de contexto (com a sessão embutida). O ticket é lido sem ser consumido; se a conferência recusa, a tentativa errada
/// (verifier, origem do pai, conexão ou usuário) o queima; se aprova, o consumo e a gravação da sessão (ou do cliente em foco) são a MESMA transação, então uma contenção do banco (503) não gasta o ticket e a repetição funciona. Toda recusa é a mesma <see cref="EmbeddedAuthenticationException"/>. O token da sessão só existe na resposta e só em memória do iframe.
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
        if (!TicketTokens.TryHash(ticket, out var hash))
        {
            throw new EmbeddedAuthenticationException();
        }

        var agora = _clock.GetUtcNow();
        var origemValida = IntegrationConnection.TryNormalizeOrigin(parentOrigin, out var origem);
        if (!PkceVerifier.IsValidVerifier(verifier) || !origemValida)
        {
            // Verifier malformado ou origem inválida com um ticket que PODE ser identificado: é uma tentativa recusada como qualquer outra, e queima o ticket (uso único; a regra é uma só). A resposta é a mesma de sempre.
            // Ticket desconhecido, vencido ou já usado não revela nada: não há o que queimar nem tenant a auditar.
            if (await _store.ConsumeTicketAsync(hash, agora, cancellationToken) is { } queimado)
            {
                await _audit.WriteForAuthenticationAsync(queimado.TenantId, queimado.UserId, new AuditEntry("identity.embed.exchange", "launch_ticket", queimado.Id.ToString(), AuditOutcome.Denied), cancellationToken);
            }

            throw new EmbeddedAuthenticationException();
        }

        var consumido = await _store.PeekTicketAsync(hash, agora, cancellationToken) ?? throw new EmbeddedAuthenticationException();
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
            // Usuário inativo (ou reativado DEPOIS de o ticket ser emitido) não abre sessão: um ticket anterior à última mudança de situação nunca é reaproveitado.
            if (vinculo is not { IsActive: true } || vinculo.UserId != consumido.UserId || usuario is not { Status: UserStatus.Active }
                || (usuario.StatusChangedAt is { } mudouEm && consumido.CreatedAt < mudouEm))
            {
                throw new EmbeddedAuthenticationException();
            }

            // Ticket emitido ATÉ a marca de revogação (logout do ERP, emergência) não vale; chave revogada não abre sessão. Junto com a mesma checagem a cada requisição, fecha a corrida entre a troca e a revogação.
            if (consumido.CreatedAt <= conexao.SessionsRevokedAt || consumido.CreatedAt <= vinculo.SessionsRevokedAt
                || (consumido.KeyId is { } chaveId && (await _store.FindKeyAsync(chaveId, cancellationToken) is not { } chave || chave.Key.Status == IntegrationKeyStatus.Revoked || chave.Key.ConnectionId != conexao.Id)))
            {
                throw new EmbeddedAuthenticationException();
            }

            var concessoes = await _auth.ListGrantsAsync(tenantId, usuario.Id, cancellationToken);
            if (!EmbeddedSessionPolicy.IsEligible(concessoes, conexao.OrganizationId, out _))
            {
                throw new EmbeddedAuthenticationException();
            }

            var (token, tokenHash) = SessionTokens.GenerateEmbedded();
            var sessao = Session.CreateEmbedded(tenantId, usuario.Id, tokenHash, agora, _options.EmbeddedSessionLifetime, conexao.Id, consumido.ExternalUserId, origem, consumido.KeyId);
            // O consumo do ticket e a gravação da sessão são UMA transação: contenção (503) deixa o ticket pendente e o ERP repete; perder a corrida para outra troca do mesmo ticket é recusa.
            // A auditoria do sucesso vai junto, na MESMA transação (outbox): nenhuma chamada fica DEPOIS do commit, então uma falha da trilha nunca mais deixa o ticket consumido com a resposta perdida (o Worker entrega depois).
            var auditoria = PendingAudit.Create(tenantId, new AuditEntry("identity.embed.exchange", "session", sessao.Id.ToString(), AuditOutcome.Success), usuario.Id, PendingAudit.AuthenticationOrigin, agora);
            if (!await _store.AddEmbeddedSessionAsync(sessao, consumido.CustomerRef, _options.MaxEmbeddedSessionsPerUser, consumido, auditoria, agora, cancellationToken))
            {
                throw new EmbeddedAuthenticationException();
            }

            return new ExchangeResult(token, sessao.ExpiresAt, conexao.Mode, conexao.OrganizationId, consumido.CustomerRef, new ExchangeUserView(usuario.Id, usuario.DisplayName), sessao.Id);
        }
        catch (EmbeddedAuthenticationException)
        {
            // Tentativa recusada queima o ticket (uso único): quem errou o verifier ou a origem não tenta de novo com o mesmo ticket.
            await _store.ConsumeTicketAsync(hash, agora, cancellationToken);
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
        var consumido = await _store.PeekTicketAsync(hash, agora, cancellationToken) ?? throw new EmbeddedAuthenticationException();
        var sessao = await _store.FindSessionAsync(contexto.TenantId, principal.SessionId, cancellationToken);

        // O ticket de OUTRO tenant nunca vale aqui (o consumo é global por hash, a conferência é do tenant da sessão).
        if (consumido.TenantId != contexto.TenantId
            || consumido.Kind != TicketKind.Context
            || sessao is not { Kind: SessionKind.Embedded }
            || consumido.SessionId != sessao.Id
            || sessao.IntegrationId != consumido.ConnectionId
            || sessao.UserId != consumido.UserId
            || !string.Equals(sessao.ExternalUserId, consumido.ExternalUserId, StringComparison.Ordinal)
            || consumido.CustomerRef is null)
        {
            await _store.ConsumeTicketAsync(hash, agora, cancellationToken); // tentativa recusada queima o ticket (uso único)
            await _audit.WriteAsync(new AuditEntry("identity.embed.context", "session", principal.SessionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new EmbeddedAuthenticationException();
        }

        // Mesma regra da troca: ticket de chave revogada, de conexão inativa ou emitido até a marca de revogação (logout, emergência) não vale, mesmo que a sessão que o resgata siga viva (aberta por OUTRA chave).
        var conexao = await _store.FindConnectionAsync(contexto.TenantId, consumido.ConnectionId, forUpdate: false, cancellationToken);
        var vinculo = await _store.FindIdentityAsync(contexto.TenantId, consumido.ConnectionId, consumido.ExternalUserId, cancellationToken);
        if (conexao is null || !conexao.IsActive
            || vinculo is not { IsActive: true }
            || consumido.CreatedAt <= conexao.SessionsRevokedAt || consumido.CreatedAt <= vinculo.SessionsRevokedAt
            || (consumido.KeyId is { } chaveId && (await _store.FindKeyAsync(chaveId, cancellationToken) is not { } chave || chave.Key.Status == IntegrationKeyStatus.Revoked || chave.Key.ConnectionId != conexao.Id)))
        {
            await _store.ConsumeTicketAsync(hash, agora, cancellationToken); // tentativa recusada queima o ticket (uso único)
            await _audit.WriteAsync(new AuditEntry("identity.embed.context", "session", principal.SessionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new EmbeddedAuthenticationException();
        }

        // Consumo do ticket e gravação do cliente em foco são UMA transação: contenção (503) deixa o ticket pendente e o ERP repete.
        // A auditoria do sucesso vai junto, na MESMA transação (outbox), com o ator e a origem do contexto desta requisição: nenhuma chamada fica depois do commit.
        var auditoria = PendingAudit.Create(contexto.TenantId, new AuditEntry("identity.embed.context", "session", sessao.Id.ToString(), AuditOutcome.Success), principal.UserId, contexto.Origin, agora);
        if (!await _store.AddCustomerGrantAsync(contexto.TenantId, sessao.Id, consumido.CustomerRef, consumido, auditoria, agora, cancellationToken))
        {
            // Outra requisição consumiu o ticket antes, ou a chave foi revogada entre a conferência e a gravação: recusa e queima (se ainda pendente).
            await _store.ConsumeTicketAsync(hash, agora, cancellationToken);
            await _audit.WriteAsync(new AuditEntry("identity.embed.context", "session", principal.SessionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new EmbeddedAuthenticationException();
        }

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
