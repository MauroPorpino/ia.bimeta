using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Domain;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Emissão de tickets pelo servidor do ERP, já autenticado por assinatura (<see cref="AuthenticatedIntegration"/>). Quem resolve o usuário INTERNO é o Identity: o ERP só informa o identificador externo. Usuário
/// mapeado pelo administrador é conferido de novo a cada emissão (conta administrativa nunca entra); usuário novo só nasce em conexão <see cref="ProvisioningMode.JitAttendant"/>, como usuário federado novo com o papel de
/// atendente na organização da conexão. Coincidência de e-mail NUNCA vincula nem cria vínculo.
/// </summary>
public sealed class EmbeddedTicketService
{
    private readonly IEmbeddedStore _store;
    private readonly IAuthenticationStore _auth;
    private readonly IdentityOptions _options;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public EmbeddedTicketService(IEmbeddedStore store, IAuthenticationStore auth, IdentityOptions options, TimeProvider clock, IAuditWriter audit)
    {
        _store = store;
        _auth = auth;
        _options = options;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="IdentityValidationException">Parâmetros inválidos (challenge, origem do pai fora da lista da conexão, identificadores).</exception>
    /// <exception cref="ExternalIdentityNotAllowedException">O usuário externo não pode entrar por esta conexão.</exception>
    public async Task<TicketResult> IssueLaunchTicketAsync(AuthenticatedIntegration integration, IssueLaunchTicketRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(request);

        var externo = ExternalIdentity.NormalizeExternalUserId(request.ExternalUserId);
        if (!PkceVerifier.IsValidChallenge(request.Challenge))
        {
            throw new IdentityValidationException("O challenge deve ser o SHA-256 do verifier em base64url (43 caracteres).");
        }

        if (!IntegrationConnection.TryNormalizeOrigin(request.ParentOrigin, out var origem) || !integration.AllowedOrigins.Contains(origem, StringComparer.Ordinal))
        {
            await _audit.WriteForAuthenticationAsync(integration.TenantId, null, new AuditEntry("identity.embed.ticket.issue", "integration_connection", integration.ConnectionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new IdentityValidationException("A origem do pai não está entre as origens permitidas da conexão.");
        }

        var cliente = LaunchTicket.NormalizeCustomerRef(request.CustomerRef);
        var agora = _clock.GetUtcNow();
        var vinculo = await ResolveIdentityAsync(integration, externo, request.DisplayName, agora, cancellationToken);

        var (token, hash) = TicketTokens.Generate();
        var ticket = LaunchTicket.CreateLaunch(integration.TenantId, integration.ConnectionId, hash, request.Challenge!, origem, externo, vinculo.UserId, cliente, agora, _options.EmbeddedTicketLifetime, integration.KeyId);
        await AddTicketOrDenyAsync(integration, ticket, "identity.embed.ticket.issue", agora, cancellationToken);

        await _audit.WriteForAuthenticationAsync(integration.TenantId, vinculo.UserId, new AuditEntry("identity.embed.ticket.issue", "launch_ticket", ticket.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new TicketResult(token, ticket.ExpiresAt);
    }

    /// <summary>
    /// Ticket de contexto: muda o cliente em foco de UMA sessão embutida já aberta, a EXATA que o ERP informa (<paramref name="sessionId"/>): o ticket fica preso a tenant, conexão, usuário externo e a essa sessão, e nenhuma outra sessão do mesmo usuário o resgata.
    /// Só para vínculo já existente e ativo (nunca provisiona) e sessão ainda ativa da mesma conexão e do mesmo usuário externo.
    /// </summary>
    public async Task<TicketResult> IssueContextTicketAsync(AuthenticatedIntegration integration, string? externalUserId, Guid? sessionId, string? customerRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integration);

        var externo = ExternalIdentity.NormalizeExternalUserId(externalUserId);
        var cliente = LaunchTicket.NormalizeCustomerRef(customerRef) ?? throw new IdentityValidationException("O cliente do ERP é obrigatório.");
        var agora = _clock.GetUtcNow();

        var vinculo = await _store.FindIdentityAsync(integration.TenantId, integration.ConnectionId, externo, cancellationToken);
        if (vinculo is null || !vinculo.IsActive || !await IsEligibleAsync(integration, vinculo.UserId, cancellationToken))
        {
            await _audit.WriteForAuthenticationAsync(integration.TenantId, vinculo?.UserId, new AuditEntry("identity.embed.context-ticket.issue", "integration_connection", integration.ConnectionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new ExternalIdentityNotAllowedException();
        }

        // A sessão que vai resgatar: tem de existir neste tenant, ser embutida, da MESMA conexão e do MESMO usuário externo, e estar ativa (não revogada, não expirada, não atingida pela marca de revogação).
        var sessaoAlvo = sessionId is { } alvoId ? await _store.FindSessionAsync(integration.TenantId, alvoId, cancellationToken) : null;
        if (sessaoAlvo is not { Kind: SessionKind.Embedded }
            || sessaoAlvo.IntegrationId != integration.ConnectionId
            || !string.Equals(sessaoAlvo.ExternalUserId, externo, StringComparison.Ordinal)
            || sessaoAlvo.UserId != vinculo.UserId
            || !sessaoAlvo.IsActiveAt(agora)
            || sessaoAlvo.CreatedAt <= vinculo.SessionsRevokedAt)
        {
            await _audit.WriteForAuthenticationAsync(integration.TenantId, vinculo.UserId, new AuditEntry("identity.embed.context-ticket.issue", "integration_connection", integration.ConnectionId.ToString(), AuditOutcome.Denied), cancellationToken);
            throw new ExternalIdentityNotAllowedException();
        }

        var (token, hash) = TicketTokens.Generate();
        var ticket = LaunchTicket.CreateContext(integration.TenantId, integration.ConnectionId, hash, externo, vinculo.UserId, cliente, agora, _options.EmbeddedTicketLifetime, integration.KeyId, sessaoAlvo.Id);
        await AddTicketOrDenyAsync(integration, ticket, "identity.embed.context-ticket.issue", agora, cancellationToken);

        await _audit.WriteForAuthenticationAsync(integration.TenantId, vinculo.UserId, new AuditEntry("identity.embed.context-ticket.issue", "launch_ticket", ticket.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new TicketResult(token, ticket.ExpiresAt);
    }

    /// <summary>Logout do ERP: revoga as sessões embutidas ativas do usuário externo nesta conexão. Devolve quantas revogou.</summary>
    public async Task<int> RevokeSessionsAsync(AuthenticatedIntegration integration, string? externalUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integration);

        var externo = ExternalIdentity.NormalizeExternalUserId(externalUserId);
        var total = await _store.RevokeEmbeddedSessionsAsync(integration.TenantId, integration.ConnectionId, externo, _clock.GetUtcNow(), cancellationToken);
        await _audit.WriteForAuthenticationAsync(integration.TenantId, null, new AuditEntry("identity.embed.session.revoke-by-integration", "integration_connection", integration.ConnectionId.ToString(), AuditOutcome.Success), cancellationToken);
        return total;
    }

    /// <summary>A chave autenticou a chamada, mas pode ter sido revogada antes da gravação: a gravação confere sob trava, e a chave morta não emite (mesma resposta de qualquer falha de autenticação).</summary>
    private async Task AddTicketOrDenyAsync(AuthenticatedIntegration integration, LaunchTicket ticket, string action, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await _store.AddTicketAsync(ticket, now, cancellationToken))
        {
            return;
        }

        await _audit.WriteForAuthenticationAsync(integration.TenantId, ticket.UserId, new AuditEntry(action, "integration_connection", integration.ConnectionId.ToString(), AuditOutcome.Denied), cancellationToken);
        throw new EmbeddedAuthenticationException();
    }

    private async Task<ExternalIdentity> ResolveIdentityAsync(AuthenticatedIntegration integration, string externalUserId, string? displayName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existente = await _store.FindIdentityAsync(integration.TenantId, integration.ConnectionId, externalUserId, cancellationToken);
        if (existente is not null)
        {
            if (!existente.IsActive || !await IsEligibleAsync(integration, existente.UserId, cancellationToken))
            {
                await DenyAsync(integration, existente.UserId, cancellationToken);
                throw new ExternalIdentityNotAllowedException();
            }

            return existente;
        }

        if (integration.Provisioning != ProvisioningMode.JitAttendant)
        {
            await DenyAsync(integration, null, cancellationToken);
            throw new ExternalIdentityNotAllowedException();
        }

        // Provisionamento na primeira entrada: usuário federado NOVO (e-mail sintético, sem senha utilizável), papel de atendente só na organização da conexão.
        var usuario = User.CreateFederated(integration.TenantId, displayName, now);
        var concessao = UserOrganizationGrant.Create(integration.TenantId, usuario.Id, integration.OrganizationId, Role.Attendant, now, null);
        var vinculo = ExternalIdentity.Create(integration.TenantId, integration.ConnectionId, externalUserId, usuario.Id, IdentitySource.Jit, now, null);
        if (await _store.TryProvisionFederatedUserAsync(usuario, concessao, vinculo, cancellationToken))
        {
            await _audit.WriteForAuthenticationAsync(integration.TenantId, usuario.Id, new AuditEntry("identity.embed.identity.provision", "external_identity", vinculo.Id.ToString(), AuditOutcome.Success), cancellationToken);
            return vinculo;
        }

        // Corrida entre duas primeiras entradas do mesmo usuário externo: vale o vínculo que ganhou.
        var vencedor = await _store.FindIdentityAsync(integration.TenantId, integration.ConnectionId, externalUserId, cancellationToken);
        if (vencedor is null || !vencedor.IsActive || !await IsEligibleAsync(integration, vencedor.UserId, cancellationToken))
        {
            await DenyAsync(integration, vencedor?.UserId, cancellationToken);
            throw new ExternalIdentityNotAllowedException();
        }

        return vencedor;
    }

    private async Task<bool> IsEligibleAsync(AuthenticatedIntegration integration, Guid userId, CancellationToken cancellationToken)
    {
        var usuario = await _auth.FindUserByIdAsync(integration.TenantId, userId, cancellationToken);
        if (usuario is null)
        {
            return false;
        }

        var concessoes = await _auth.ListGrantsAsync(integration.TenantId, userId, cancellationToken);
        return EmbeddedSessionPolicy.IsEligible(concessoes, integration.OrganizationId, out _);
    }

    private Task DenyAsync(AuthenticatedIntegration integration, Guid? userId, CancellationToken cancellationToken) =>
        _audit.WriteForAuthenticationAsync(integration.TenantId, userId, new AuditEntry("identity.embed.ticket.issue", "integration_connection", integration.ConnectionId.ToString(), AuditOutcome.Denied), cancellationToken);
}
