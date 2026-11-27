using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>
/// Valida o token de sessão apresentado e carrega o usuário e as permissões efetivas (TEN-1). Só a sessão no banco
/// decide o tenant: o token em si é opaco e não carrega nada. Recusa token malformado, desconhecido, expirado ou
/// revogado, de tenant suspenso e de usuário que já não existe. A resposta é sempre a mesma (<c>null</c>).
/// A sessão embutida (ADR 0039) é conferida a CADA requisição contra o estado atual: conexão desativada, vínculo
/// desativado ou conta que se tornou administrativa derrubam a sessão na hora, e as permissões nunca passam do teto
/// do Embedded (<see cref="EmbeddedSessionPolicy"/>: só o atendimento, só na organização da conexão).
/// </summary>
public sealed class SessionAuthenticator
{
    private readonly IAuthenticationStore _store;
    private readonly IEmbeddedStore _embedded;
    private readonly ITenantDirectory _tenants;
    private readonly TimeProvider _clock;

    public SessionAuthenticator(IAuthenticationStore store, IEmbeddedStore embedded, ITenantDirectory tenants, TimeProvider clock)
    {
        _store = store;
        _embedded = embedded;
        _tenants = tenants;
        _clock = clock;
    }

    public async Task<AuthenticatedPrincipal?> AuthenticateAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!SessionTokens.TryHash(token, out var hash, out var embutido))
        {
            return null;
        }

        var sessao = await _store.FindSessionByTokenHashAsync(hash, cancellationToken);
        // O formato do token e o tipo da sessão têm de coincidir (ibs_ só para interativa, ibe_ só para embutida): nunca um tipo se passa pelo outro.
        if (sessao is null || !sessao.IsActiveAt(_clock.GetUtcNow()) || (sessao.Kind == SessionKind.Embedded) != embutido)
        {
            return null;
        }

        var tenant = await _tenants.FindByIdAsync(sessao.TenantId, cancellationToken);
        if (tenant is not { Status: TenantStatus.Active })
        {
            return null;
        }

        // P1-05: uma sessão cuja autenticação COMEÇOU antes da última reativação da empresa nunca vale (a marca é o instante em que a autenticação foi iniciada, não o da gravação). Fecha a corrida em que um login iniciado
        // antes da suspensão grava a sessão depois das revogações em lote: a reativação não a ressuscita. Novas sessões, iniciadas depois, valem normalmente.
        if (tenant.ReactivatedAt is { } reativadaEm && sessao.CreatedAt < reativadaEm)
        {
            return null;
        }

        var usuario = await _store.FindUserByIdAsync(sessao.TenantId, sessao.UserId, cancellationToken);
        if (usuario is null)
        {
            return null;
        }

        // Só o usuário ATIVO acessa (inativo, pendente e cancelado nunca): a situação é conferida a cada requisição, e desativar derruba a sessão na próxima. Uma sessão criada ANTES da última mudança de situação (desativação e reativação) nunca
        // volta a valer, mesmo que tenha escapado da revogação por qualquer falha: a marca de situação do usuário é a segunda defesa (a primeira é gravar a sessão sob a trava da linha do usuário).
        if (usuario.Status != UserStatus.Active || (usuario.StatusChangedAt is { } mudouEm && sessao.CreatedAt < mudouEm))
        {
            return null;
        }

        var concessoes = await _store.ListGrantsAsync(sessao.TenantId, usuario.Id, cancellationToken);
        var ajustes = await _store.ListOverridesAsync(sessao.TenantId, usuario.Id, cancellationToken);

        if (sessao.Kind == SessionKind.Embedded && tenant.Kind == TenantKind.Platform)
        {
            return null; // o tenant técnico da plataforma não tem sessão embutida
        }

        if (sessao.Kind == SessionKind.Embedded)
        {
            return await AuthenticateEmbeddedAsync(sessao, usuario, concessoes, ajustes, cancellationToken);
        }

        // Usuário federado nunca tem sessão interativa (o login por senha o recusa); se aparecer uma, não vale.
        if (usuario.AuthMode == UserAuthMode.Federated)
        {
            return null;
        }

        // ADR 0043: as permissões da plataforma só existem no tenant técnico e, nele, só elas; em uma empresa contratante são descartadas, mesmo que apareçam por qualquer caminho.
        var plataforma = tenant.Kind == TenantKind.Platform;
        var permissoes = EffectivePermissions.Compute(concessoes, ajustes).Where(g => PlatformPermissions.IsPlatform(g.Permission) == plataforma).ToList();

        return new AuthenticatedPrincipal(sessao.TenantId, usuario.Id, sessao.Id, permissoes, IsPlatform: plataforma);
    }

    private async Task<AuthenticatedPrincipal?> AuthenticateEmbeddedAsync(Session sessao, User usuario, IReadOnlyList<UserOrganizationGrant> concessoes, IReadOnlyList<UserPermissionOverride> ajustes, CancellationToken cancellationToken)
    {
        if (sessao.IntegrationId is not { } conexaoId || sessao.ExternalUserId is null)
        {
            return null;
        }

        var conexao = await _embedded.FindConnectionAsync(sessao.TenantId, conexaoId, forUpdate: false, cancellationToken);
        if (conexao is null || !conexao.IsActive)
        {
            return null;
        }

        // Origens autorizadas: a sessão só vale enquanto a origem do pai comprovada na troca CONTINUA na lista da conexão (o administrador tirou a origem = a sessão cai).
        if (sessao.ParentOrigin is null || !conexao.IsOriginAllowed(sessao.ParentOrigin))
        {
            return null;
        }

        var vinculo = await _embedded.FindIdentityAsync(sessao.TenantId, conexaoId, sessao.ExternalUserId, cancellationToken);
        if (vinculo is not { IsActive: true } || vinculo.UserId != usuario.Id)
        {
            return null;
        }

        // Revogação (logout do ERP, emergência): sessão criada até a marca não vale, mesmo que tenha sido gravada DEPOIS da revogação por uma troca que corria junto.
        if (sessao.CreatedAt <= conexao.SessionsRevokedAt || sessao.CreatedAt <= vinculo.SessionsRevokedAt)
        {
            return null;
        }

        // Chave revogada: a sessão aberta com um ticket dela cai (chave só expirada pela rotação não derruba as sessões).
        if (sessao.IssuerKeyId is { } chaveId
            && (await _embedded.FindKeyAsync(chaveId, cancellationToken) is not { } chave || chave.Key.Status == IntegrationKeyStatus.Revoked || chave.Key.ConnectionId != conexaoId))
        {
            return null;
        }

        // Conta que passou a ser administrativa (ou perdeu o acesso à organização) não mantém sessão embutida.
        if (!EmbeddedSessionPolicy.IsEligible(concessoes, conexao.OrganizationId, out _))
        {
            return null;
        }

        // Modo CustomerOnly: nenhuma permissão por concessão e escopo que nega também as vias fora das concessões (fila, responsável, recibo) até existir a autorização por cliente (E2): bloqueio seguro por padrão.
        var somenteCliente = conexao.Mode == EmbeddedMode.CustomerOnly;
        var permissoes = somenteCliente
            ? new List<PermissionGrant>()
            : EmbeddedSessionPolicy.Filter(EffectivePermissions.Compute(concessoes, ajustes), conexao.OrganizationId);

        return new AuthenticatedPrincipal(
            sessao.TenantId, usuario.Id, sessao.Id, permissoes, SessionKind.Embedded, conexaoId, new EmbeddedScope(conexao.OrganizationId, somenteCliente));
    }
}
