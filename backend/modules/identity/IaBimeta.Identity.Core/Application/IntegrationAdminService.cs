using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Administração das conexões de integração do Embedded (ADR 0039): conexões, chaves, vínculos de identidade externa e revogação de sessões embutidas. Só quem tem <see cref="Permission.EmbedManage"/> (concessão
/// de escopo do tenant) administra; o tenant vem do contexto confiável. Toda mudança é auditada SEM segredo, sem ticket e sem token. O segredo de uma chave aparece uma única vez, na criação.
/// </summary>
public sealed class IntegrationAdminService
{
    private readonly IEmbeddedStore _store;
    private readonly IAuthenticationStore _auth;
    private readonly IOrganizationDirectory _organizations;
    private readonly IIntegrationSecretProtector _protector;
    private readonly IdentityOptions _options;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public IntegrationAdminService(
        IEmbeddedStore store,
        IAuthenticationStore auth,
        IOrganizationDirectory organizations,
        IIntegrationSecretProtector protector,
        IdentityOptions options,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit)
    {
        _store = store;
        _auth = auth;
        _organizations = organizations;
        _protector = protector;
        _options = options;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    public async Task<ConnectionView> CreateConnectionAsync(
        Guid organizationId, string? name, EmbeddedMode mode, ProvisioningMode provisioning, IEnumerable<string>? allowedOrigins, bool allowStartConversation, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.connection.create", "organization", organizationId.ToString(), cancellationToken);
        if (!await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            throw new IdentityResourceNotFoundException("Organização");
        }

        var conexao = IntegrationConnection.Create(contexto.TenantId, organizationId, name, mode, provisioning, allowedOrigins, allowStartConversation, _clock.GetUtcNow(), contexto.Principal?.UserId);
        await _store.AddConnectionAsync(conexao, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("identity.embed.connection.create", "integration_connection", conexao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ConnectionView.From(conexao);
    }

    public async Task<IReadOnlyList<ConnectionView>> ListConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.connection.list", "integration_connection", null, cancellationToken);
        return (await _store.ListConnectionsAsync(contexto.TenantId, cancellationToken)).Select(ConnectionView.From).ToList();
    }

    public async Task<ConnectionView> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.connection.get", "integration_connection", connectionId.ToString(), cancellationToken);
        return ConnectionView.From(await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken));
    }

    public async Task<ConnectionView> UpdateConnectionAsync(
        Guid connectionId, string? name, EmbeddedMode mode, ProvisioningMode provisioning, IEnumerable<string>? allowedOrigins, bool allowStartConversation, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.connection.update", "integration_connection", connectionId.ToString(), cancellationToken);
        var conexao = await LoadAsync(contexto.TenantId, connectionId, forUpdate: true, cancellationToken);
        conexao.UpdateSettings(name, mode, provisioning, allowedOrigins, allowStartConversation, _clock.GetUtcNow());
        await _store.SaveChangesAsync(contexto.TenantId, cancellationToken);
        await _audit.WriteAsync(new AuditEntry("identity.embed.connection.update", "integration_connection", conexao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ConnectionView.From(conexao);
    }

    /// <summary>Desativar a conexão revoga também as sessões embutidas dela (e as chaves deixam de autenticar na hora).</summary>
    public async Task<ConnectionView> SetConnectionStatusAsync(Guid connectionId, ConnectionStatus status, CancellationToken cancellationToken = default)
    {
        var operacao = status == ConnectionStatus.Active ? "identity.embed.connection.enable" : "identity.embed.connection.disable";
        var contexto = await RequireAdminAsync(operacao, "integration_connection", connectionId.ToString(), cancellationToken);
        var conexao = await LoadAsync(contexto.TenantId, connectionId, forUpdate: true, cancellationToken);
        var agora = _clock.GetUtcNow();
        if (conexao.SetStatus(status, agora))
        {
            await _store.SaveChangesAsync(contexto.TenantId, cancellationToken);
        }

        if (status == ConnectionStatus.Disabled)
        {
            await _store.RevokeEmbeddedSessionsAsync(contexto.TenantId, connectionId, null, agora, cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry(operacao, "integration_connection", conexao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ConnectionView.From(conexao);
    }

    /// <summary>Cria uma chave. O segredo é devolvido aqui, uma única vez.</summary>
    /// <exception cref="EmbeddedConflictException">A conexão já tem o número máximo de chaves ativas.</exception>
    public Task<CreatedKeyView> CreateKeyAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        AddKeyAsync(connectionId, rotate: false, grace: null, cancellationToken);

    /// <summary>
    /// Rotação: cria a chave nova e programa o fim das antigas utilizáveis (carência padrão de 24 h, no máximo 7 dias), para o ERP trocar sem parada.
    /// </summary>
    public Task<CreatedKeyView> RotateKeyAsync(Guid connectionId, TimeSpan? grace, CancellationToken cancellationToken = default) =>
        AddKeyAsync(connectionId, rotate: true, grace, cancellationToken);

    public async Task<IReadOnlyList<KeyView>> ListKeysAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.key.list", "integration_connection", connectionId.ToString(), cancellationToken);
        _ = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);
        return (await _store.ListKeysAsync(contexto.TenantId, connectionId, cancellationToken)).Select(KeyView.From).ToList();
    }

    public async Task RevokeKeyAsync(Guid connectionId, string? keyId, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.key.revoke", "integration_connection", connectionId.ToString(), cancellationToken);
        _ = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);
        if (!IntegrationKey.TryParsePublicId(keyId, out var id) || !await _store.RevokeKeyAsync(contexto.TenantId, connectionId, id, _clock.GetUtcNow(), cancellationToken))
        {
            throw new IdentityResourceNotFoundException("Chave");
        }

        await _audit.WriteAsync(new AuditEntry("identity.embed.key.revoke", "integration_key", id.ToString("N"), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>
    /// Mapeia, explicitamente, um usuário do ERP a um usuário interno EXISTENTE. Nunca por coincidência de e-mail: o administrador indica o usuário. Conta administrativa, sem acesso à organização da conexão ou federada
    /// é recusada.
    /// </summary>
    /// <exception cref="ExternalIdentityTargetNotAllowedException">O alvo não pode ser vinculado.</exception>
    /// <exception cref="EmbeddedConflictException">O usuário externo ou o interno já têm vínculo nesta conexão.</exception>
    public async Task<ExternalIdentityView> MapIdentityAsync(Guid connectionId, string? externalUserId, Guid userId, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.identity.map", "integration_connection", connectionId.ToString(), cancellationToken);
        var conexao = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);

        var externo = ExternalIdentity.NormalizeExternalUserId(externalUserId);
        var usuario = await _auth.FindUserByIdAsync(contexto.TenantId, userId, cancellationToken) ?? throw new IdentityResourceNotFoundException("Usuário");
        if (usuario.AuthMode == UserAuthMode.Federated)
        {
            await DenyAsync("identity.embed.identity.map", userId, cancellationToken);
            throw new ExternalIdentityTargetNotAllowedException("Usuário federado só é vinculado pela própria criação.");
        }

        var concessoes = await _auth.ListGrantsAsync(contexto.TenantId, userId, cancellationToken);
        if (!EmbeddedSessionPolicy.IsEligible(concessoes, conexao.OrganizationId, out var motivo))
        {
            await DenyAsync("identity.embed.identity.map", userId, cancellationToken);
            throw new ExternalIdentityTargetNotAllowedException(motivo);
        }

        var vinculo = ExternalIdentity.Create(contexto.TenantId, connectionId, externo, userId, IdentitySource.Mapped, _clock.GetUtcNow(), contexto.Principal?.UserId);
        if (!await _store.TryAddIdentityAsync(vinculo, cancellationToken))
        {
            throw new EmbeddedConflictException("Este usuário externo ou este usuário interno já tem vínculo nesta conexão.");
        }

        await _audit.WriteAsync(new AuditEntry("identity.embed.identity.map", "external_identity", vinculo.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ExternalIdentityView.From(vinculo);
    }

    public async Task<IReadOnlyList<ExternalIdentityView>> ListIdentitiesAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.identity.list", "integration_connection", connectionId.ToString(), cancellationToken);
        _ = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);
        return (await _store.ListIdentitiesAsync(contexto.TenantId, connectionId, cancellationToken)).Select(ExternalIdentityView.From).ToList();
    }

    /// <summary>Ativa ou desativa o vínculo. Desativar revoga as sessões embutidas desse usuário externo.</summary>
    public async Task SetIdentityStatusAsync(Guid connectionId, Guid identityId, ExternalIdentityStatus status, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.identity.status", "integration_connection", connectionId.ToString(), cancellationToken);
        _ = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);
        var vinculo = await _store.FindIdentityByIdAsync(contexto.TenantId, connectionId, identityId, cancellationToken) ?? throw new IdentityResourceNotFoundException("Vínculo");

        await _store.SetIdentityStatusAsync(contexto.TenantId, connectionId, identityId, status, cancellationToken);
        if (status == ExternalIdentityStatus.Disabled)
        {
            await _store.RevokeEmbeddedSessionsAsync(contexto.TenantId, connectionId, vinculo.ExternalUserId, _clock.GetUtcNow(), cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry("identity.embed.identity.status", "external_identity", identityId.ToString(), AuditOutcome.Success), cancellationToken);
    }

    /// <summary>Revoga as sessões embutidas ativas da conexão (de um usuário externo, se informado). Devolve quantas revogou.</summary>
    public async Task<int> RevokeSessionsAsync(Guid connectionId, string? externalUserId, CancellationToken cancellationToken = default)
    {
        var contexto = await RequireAdminAsync("identity.embed.session.revoke", "integration_connection", connectionId.ToString(), cancellationToken);
        _ = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);
        var externo = string.IsNullOrWhiteSpace(externalUserId) ? null : ExternalIdentity.NormalizeExternalUserId(externalUserId);
        var total = await _store.RevokeEmbeddedSessionsAsync(contexto.TenantId, connectionId, externo, _clock.GetUtcNow(), cancellationToken);
        await _audit.WriteAsync(new AuditEntry("identity.embed.session.revoke", "integration_connection", connectionId.ToString(), AuditOutcome.Success), cancellationToken);
        return total;
    }

    private async Task<CreatedKeyView> AddKeyAsync(Guid connectionId, bool rotate, TimeSpan? grace, CancellationToken cancellationToken)
    {
        var operacao = rotate ? "identity.embed.key.rotate" : "identity.embed.key.create";
        var contexto = await RequireAdminAsync(operacao, "integration_connection", connectionId.ToString(), cancellationToken);
        _ = await LoadAsync(contexto.TenantId, connectionId, forUpdate: false, cancellationToken);

        if (!_protector.IsConfigured)
        {
            throw new EmbeddedUnavailableException("secrets-unavailable");
        }

        var agora = _clock.GetUtcNow();
        DateTimeOffset? fimDasAntigas = null;
        if (rotate)
        {
            var carencia = grace ?? _options.IntegrationKeyRotationGrace;
            if (carencia <= TimeSpan.Zero || carencia > TimeSpan.FromDays(7))
            {
                throw new IdentityValidationException("A carência da rotação deve ser positiva e de no máximo 7 dias.");
            }

            fimDasAntigas = agora + carencia;
        }

        var (id, segredo) = IntegrationKey.GenerateIdentifiers();
        var protegido = _protector.Protect(segredo, new IntegrationSecretBinding(contexto.TenantId, connectionId, id));
        var chave = IntegrationKey.Create(contexto.TenantId, connectionId, id, protegido.MasterKeyId, protegido.Ciphertext, agora, contexto.Principal?.UserId);
        if (!await _store.TryAddKeyAsync(chave, fimDasAntigas, agora, cancellationToken))
        {
            throw new EmbeddedConflictException($"A conexão já tem {IntegrationKey.MaxActivePerConnection} chaves ativas: revogue uma antes.");
        }

        await _audit.WriteAsync(new AuditEntry(operacao, "integration_key", id.ToString("N"), AuditOutcome.Success), cancellationToken);
        return new CreatedKeyView(chave.PublicId, segredo, agora, fimDasAntigas);
    }

    private async Task<IntegrationConnection> LoadAsync(TenantId tenantId, Guid connectionId, bool forUpdate, CancellationToken cancellationToken) =>
        await _store.FindConnectionAsync(tenantId, connectionId, forUpdate, cancellationToken) ?? throw new IdentityResourceNotFoundException("Conexão");

    private async Task<RequestContext> RequireAdminAsync(string operation, string resourceType, string? resourceId, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        await AuditedAccess.RequireAsync(_audit, contexto, Permission.EmbedManage, null, operation, resourceType, resourceId, cancellationToken);
        return contexto;
    }

    private Task DenyAsync(string operation, Guid userId, CancellationToken cancellationToken) =>
        _audit.WriteAsync(new AuditEntry(operation, "user", userId.ToString(), AuditOutcome.Denied), cancellationToken);
}
