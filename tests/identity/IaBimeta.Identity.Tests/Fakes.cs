using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Tests;

internal sealed class RelogioManual : TimeProvider
{
    private DateTimeOffset _agora;

    public RelogioManual(DateTimeOffset inicio)
    {
        _agora = inicio;
    }

    public override DateTimeOffset GetUtcNow() => _agora;

    public void Avancar(TimeSpan intervalo) => _agora += intervalo;
}

internal sealed record EventoDeAuditoria(AuditEntry Entrada, bool AntesDoContexto, TenantId? Tenant, Guid? Ator);

internal sealed class AuditoriaEmMemoria : IAuditWriter
{
    public List<EventoDeAuditoria> Eventos { get; } = new();

    public IEnumerable<AuditEntry> Entradas => Eventos.Select(e => e.Entrada);

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Eventos.Add(new EventoDeAuditoria(entry, false, null, null));
        return Task.CompletedTask;
    }

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Eventos.Add(new EventoDeAuditoria(entry, true, tenantId, actorUserId));
        return Task.CompletedTask;
    }
}

internal sealed class TenantsEmMemoria : ITenantDirectory
{
    private readonly List<TenantInfo> _tenants = new();

    public TenantInfo Adicionar(string slug, TenantStatus status = TenantStatus.Active)
    {
        var info = new TenantInfo(new TenantId(Guid.NewGuid()), slug, $"Tenant {slug}", status);
        _tenants.Add(info);
        return info;
    }

    public TenantInfo Adicionar(TenantId id, string slug)
    {
        var info = new TenantInfo(id, slug, $"Tenant {slug}", TenantStatus.Active);
        _tenants.Add(info);
        return info;
    }

    /// <summary>Marca a reativação da empresa no instante dado (a empresa continua ativa): o que nasceu antes dele deixou de valer.</summary>
    public void Reativar(TenantId tenantId, DateTimeOffset reativadaEm)
    {
        var indice = _tenants.FindIndex(t => t.TenantId == tenantId);
        _tenants[indice] = _tenants[indice] with { ReactivatedAt = reativadaEm };
    }

    public bool Contem(TenantId tenantId) => _tenants.Any(t => t.TenantId == tenantId);

    public void Suspender(TenantId tenantId)
    {
        var indice = _tenants.FindIndex(t => t.TenantId == tenantId);
        _tenants[indice] = _tenants[indice] with { Status = TenantStatus.Suspended };
    }

    public Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenants.FirstOrDefault(t => t.Slug == (slug ?? string.Empty).Trim().ToLowerInvariant()));

    public Task<TenantInfo?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenants.FirstOrDefault(t => t.TenantId == tenantId));
}

internal sealed class OrganizacoesEmMemoria : IOrganizationDirectory
{
    public HashSet<Guid> Existentes { get; } = new();

    public Guid Adicionar()
    {
        var id = Guid.NewGuid();
        Existentes.Add(id);
        return id;
    }

    public Task<bool> ExistsAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Existentes.Contains(organizationId));
}

/// <summary>
/// Persistência de identidade em memória, para testes de regra. O isolamento por tenant e as restrições do banco são
/// provados com PostgreSQL real em <c>IaBimeta.IntegrationTests</c>.
/// </summary>
internal sealed class IdentidadeEmMemoria : IUserRepository, IGrantRepository, ISessionRepository, IAuthenticationStore
{
    public List<User> Usuarios { get; } = new();
    public List<UserOrganizationGrant> Concessoes { get; } = new();
    public List<Session> Sessoes { get; } = new();

    // IUserRepository

    public Task AddAsync(User user, UserOrganizationGrant initialGrant, CancellationToken cancellationToken)
    {
        if (Usuarios.Any(u => u.TenantId == user.TenantId && u.Email == user.Email))
        {
            throw new DuplicateUserEmailException();
        }

        Usuarios.Add(user);
        Concessoes.Add(initialGrant);
        return Task.CompletedTask;
    }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Usuarios.FirstOrDefault(u => u.Id == id));

    // Em memória, a instância guardada já é a "rastreada": alterá-la basta.
    public Task<User?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Usuarios.FirstOrDefault(u => u.Id == id));

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(Usuarios.FirstOrDefault(u => u.Email == normalizedEmail));

    // IGrantRepository

    public Task<UserOrganizationGrant?> FindAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken) =>
        Task.FromResult(Concessoes.FirstOrDefault(g => g.UserId == userId && g.OrganizationId == organizationId));

    public Task AddAsync(UserOrganizationGrant grant, CancellationToken cancellationToken)
    {
        Concessoes.Add(grant);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(UserOrganizationGrant grant, CancellationToken cancellationToken)
    {
        Concessoes.Remove(grant);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserOrganizationGrant>> ListByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserOrganizationGrant>>(Concessoes.Where(g => g.UserId == userId).ToList());

    public Task<IReadOnlyList<UserAccess>> ListAccessAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var acessos = Concessoes
            .Where(g => g.OrganizationId == organizationId || g.OrganizationId is null)
            .Select(g => new UserAccess(Usuarios.First(u => u.Id == g.UserId), g))
            .ToList();
        return Task.FromResult<IReadOnlyList<UserAccess>>(acessos);
    }

    // ISessionRepository (explícito, pois o nome colide com IUserRepository)

    Task<Session?> ISessionRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Sessoes.FirstOrDefault(s => s.Id == id));

    // IAuthenticationStore

    public Task<User?> FindUserByEmailAsync(TenantId tenantId, string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(Usuarios.FirstOrDefault(u => u.TenantId == tenantId && u.Email == normalizedEmail));

    public Task<User?> FindUserByIdAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Usuarios.FirstOrDefault(u => u.TenantId == tenantId && u.Id == userId));

    public int BuscasDeSessao { get; private set; }

    public Task<Session?> FindSessionByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        BuscasDeSessao++;
        return Task.FromResult(Sessoes.FirstOrDefault(s => s.TokenHash.AsSpan().SequenceEqual(tokenHash)));
    }

    public Task<bool> AddSessionAsync(Session session, CancellationToken cancellationToken)
    {
        // Como o banco: só grava a sessão se o usuário continua ativo.
        if (Usuarios.FirstOrDefault(u => u.TenantId == session.TenantId && u.Id == session.UserId) is not { Status: UserStatus.Active })
        {
            return Task.FromResult(false);
        }

        Sessoes.Add(session);
        return Task.FromResult(true);
    }

    public Task UpdatePasswordHashAsync(TenantId tenantId, Guid userId, string newHash, CancellationToken cancellationToken)
    {
        Usuarios.First(u => u.TenantId == tenantId && u.Id == userId).ReplacePasswordHash(newHash);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserOrganizationGrant>> ListGrantsAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserOrganizationGrant>>(Concessoes.Where(g => g.TenantId == tenantId && g.UserId == userId).ToList());


    public List<UserPermissionOverride> Ajustes { get; } = new();

    /// <summary>A política de usuários por tenant (a existência da entrada é a decisão explícita; sem entrada, empresa anterior à Etapa 01).</summary>
    public Dictionary<TenantId, UserPolicy> Politicas { get; } = new();

    public void DefinirPolitica(TenantId tenant, int? maxUsers, bool cadastroHabilitado) => Politicas[tenant] = new UserPolicy(maxUsers, cadastroHabilitado, true);

    public Task<IReadOnlyList<UserPermissionOverride>> ListOverridesAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserPermissionOverride>>(Ajustes.Where(o => o.TenantId == tenantId && o.UserId == userId).ToList());

    public Task<bool> ActivatePendingUserAsync(TenantId tenantId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var usuario = Usuarios.FirstOrDefault(u => u.TenantId == tenantId && u.Id == userId && u.Status == UserStatus.Pending);
        if (usuario is null)
        {
            return Task.FromResult(false);
        }

        usuario.Activate(now);
        return Task.FromResult(true);
    }
    public Dictionary<(TenantId Tenant, string Chave), LoginThrottle> Contagens { get; } = new();

    public Task<LoginThrottle?> FindLoginThrottleAsync(TenantId tenantId, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Contagens.GetValueOrDefault((tenantId, key)));

    public Task<bool> RecordLoginFailureAsync(TenantId tenantId, string key, DateTimeOffset now, LoginThrottlePolicy policy, CancellationToken cancellationToken)
    {
        if (!Contagens.TryGetValue((tenantId, key), out var contagem))
        {
            contagem = LoginThrottle.Create(tenantId, key, now);
            Contagens[(tenantId, key)] = contagem;
        }

        return Task.FromResult(contagem.RegisterFailure(now, policy));
    }

    public Task ClearLoginThrottleAsync(TenantId tenantId, string key, CancellationToken cancellationToken)
    {
        Contagens.Remove((tenantId, key));
        return Task.CompletedTask;
    }

    public Task<bool> RevokeSessionAsync(TenantId tenantId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessao = Sessoes.FirstOrDefault(s => s.TenantId == tenantId && s.Id == sessionId && s.RevokedAt is null);
        return Task.FromResult(sessao is not null && sessao.Revoke(now));
    }

    public Task<int> RevokeAllSessionsAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revogadas = 0;
        foreach (var sessao in Sessoes.Where(s => s.TenantId == tenantId && s.IsActiveAt(now)))
        {
            sessao.Revoke(now);
            revogadas++;
        }

        return Task.FromResult(revogadas);
    }

    public Task<int> RevokeSessionsAsync(TenantId tenantId, Guid userId, Guid? exceptSessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var revogadas = 0;
        foreach (var sessao in Sessoes.Where(s => s.TenantId == tenantId && s.UserId == userId && s.Id != exceptSessionId && s.IsActiveAt(now)))
        {
            sessao.Revoke(now);
            revogadas++;
        }

        return Task.FromResult(revogadas);
    }

    public List<PasswordResetToken> TokensDeRedefinicao { get; } = new();

    // Em memória o token é imutável; a marca de uso e de revogação ficam em dicionários ao lado.
    public Dictionary<Guid, DateTimeOffset> TokensUsados { get; } = new();

    public Dictionary<Guid, DateTimeOffset> TokensRevogados { get; } = new();

    public Task AddPasswordResetTokenAsync(PasswordResetToken token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var anterior in TokensDeRedefinicao.Where(t => t.TenantId == token.TenantId && t.UserId == token.UserId && !TokensUsados.ContainsKey(t.Id) && !TokensRevogados.ContainsKey(t.Id)))
        {
            TokensRevogados[anterior.Id] = now;
        }

        TokensDeRedefinicao.Add(token);
        return Task.CompletedTask;
    }

    public Task RevokePasswordResetTokensAsync(TenantId tenantId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var t in TokensDeRedefinicao.Where(t => t.TenantId == tenantId && t.UserId == userId && !TokensUsados.ContainsKey(t.Id) && !TokensRevogados.ContainsKey(t.Id)))
        {
            TokensRevogados[t.Id] = now;
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryConsumePasswordResetTokenAsync(TenantId tenantId, Guid userId, byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = TokensDeRedefinicao.FirstOrDefault(t => t.TenantId == tenantId && t.UserId == userId && t.TokenHash.AsSpan().SequenceEqual(tokenHash));
        if (token is null || TokensUsados.ContainsKey(token.Id) || TokensRevogados.ContainsKey(token.Id) || token.ExpiresAt <= now)
        {
            return Task.FromResult(false);
        }

        TokensUsados[token.Id] = now;
        return Task.FromResult(true);
    }
}

/// <summary>Escritor de auditoria que sempre falha, para provar que a falha nunca vira sucesso silencioso.</summary>
internal sealed class AuditoriaQueFalha : IAuditWriter
{
    public const string Mensagem = "auditoria indisponível (simulada)";

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Mensagem);

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Mensagem);
}

internal static class Senhas
{
    public const string Valida = "Senha-Longa-123!";
    public const string Outra = "Outra-Senha-456!";

    public static IdentityOptions Rapidas() => new() { PasswordHashIterations = IdentityOptions.MinimumPasswordHashIterations };
}

/// <summary>
/// A administração de usuários em memória, sobre os mesmos dados do <see cref="IdentidadeEmMemoria"/>. Não há trava nem transação (a concorrência é provada com PostgreSQL real em <c>IaBimeta.IntegrationTests</c>); as regras de vagas,
/// status e permissões são as mesmas que o serviço aplica.
/// </summary>
internal sealed class AdminEmMemoria : IUserAdminStore
{
    private readonly IdentidadeEmMemoria _dados;
    private readonly RequestContextHolder _holder;

    public AdminEmMemoria(IdentidadeEmMemoria dados, RequestContextHolder holder)
    {
        _dados = dados;
        _holder = holder;
    }

    /// <summary>Os vínculos externos (do fake do Embedded), para contar a vaga do usuário federado só enquanto houver vínculo ativo.</summary>
    public Func<IEnumerable<ExternalIdentity>>? Vinculos { get; set; }

    private TenantId Tenant => _holder.GetRequired().TenantId;

    public Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) => action(cancellationToken);

    /// <summary>Roda ao ENTRAR na seção exclusiva (depois de a trava ser obtida): permite ao teste fazer o tempo passar enquanto a requisição "esperava" a trava.</summary>
    public Func<Task>? AoEntrarNaTrava { get; set; }

    public async Task<T> RunExclusiveAsync<T>(TenantId tenantId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (AoEntrarNaTrava is not null)
        {
            await AoEntrarNaTrava();
        }

        return await action(cancellationToken);
    }

    private UserDetail Detail(User u, DateTimeOffset now) => new(
        u,
        _dados.Concessoes.Where(g => g.UserId == u.Id).ToList(),
        _dados.Ajustes.Where(o => o.UserId == u.Id).ToList(),
        _dados.TokensDeRedefinicao.Any(t => t.UserId == u.Id && !_dados.TokensUsados.ContainsKey(t.Id) && !_dados.TokensRevogados.ContainsKey(t.Id) && t.ExpiresAt > now),
        Vinculos?.Invoke().Any(i => i.UserId == u.Id && i.IsActive) == true);

    public Task<IReadOnlyDictionary<Guid, SeatSummary>> GetSeatSummariesAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyDictionary<Guid, SeatSummary>)new Dictionary<Guid, SeatSummary>());

    public Task<UserPolicy> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_dados.Politicas.TryGetValue(tenantId, out var politica) ? politica : UserPolicy.Legacy);

    public Task<(UserPolicy Previous, UserPolicy Current)> SetPolicyAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var anterior = _dados.Politicas.TryGetValue(tenantId, out var existente) ? existente : UserPolicy.Legacy;
        _dados.DefinirPolitica(tenantId, maxUsers, registrationEnabled);
        return Task.FromResult((anterior, _dados.Politicas[tenantId]));
    }

    public Task<(UserPolicy Previous, UserPolicy Current)> ChangePolicyAsync(TenantId tenantId, bool changeMaxUsers, int? maxUsers, bool? registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var anterior = _dados.Politicas.TryGetValue(tenantId, out var existente) ? existente : UserPolicy.Legacy;
        _dados.DefinirPolitica(tenantId, changeMaxUsers ? maxUsers : anterior.MaxUsers, registrationEnabled ?? anterior.RegistrationEnabled);
        return Task.FromResult((anterior, _dados.Politicas[tenantId]));
    }

    public Task<bool> EnsurePolicyAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_dados.Politicas.ContainsKey(tenantId))
        {
            return Task.FromResult(false);
        }

        _dados.DefinirPolitica(tenantId, maxUsers, registrationEnabled);
        return Task.FromResult(true);
    }

    public Task<SeatUsage> GetSeatUsageAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var detalhes = _dados.Usuarios.Where(u => u.TenantId == tenantId).Select(u => Detail(u, now)).ToList();
        var uso = new SeatUsage(
            detalhes.Count(d => d.DisplayStatus == "Active"),
            detalhes.Count(d => d.DisplayStatus == "Inactive"),
            detalhes.Count(d => d.DisplayStatus == "Pending"),
            detalhes.Count(d => d.DisplayStatus == "Expired"),
            detalhes.Count(d => d.DisplayStatus == "Cancelled"));
        return Task.FromResult(uso);
    }

    public Task<UserPage> ListUsersAsync(UserListQuery query, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var itens = _dados.Usuarios.Where(u => u.TenantId == Tenant).Select(u => Detail(u, now))
            .Where(d => query.VisibleOrganizations is null || d.Grants.Any(g => g.OrganizationId is null || query.VisibleOrganizations.Contains(g.OrganizationId.Value)))
            .Where(d => string.IsNullOrWhiteSpace(query.Search)
                || d.User.Email.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || d.User.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(d => query.OrganizationId is null || d.Grants.Any(g => g.OrganizationId == query.OrganizationId || g.OrganizationId is null))
            .Where(d => query.Role is null || d.Grants.Any(g => g.Role == query.Role))
            .Where(d => string.IsNullOrEmpty(query.Status) || string.Equals(d.DisplayStatus, query.Status, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.User.DisplayName).ThenBy(d => d.User.Email).ToList();
        var pagina = itens.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        return Task.FromResult(new UserPage(pagina, itens.Count));
    }

    public Task<UserDetail?> GetDetailAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var usuario = _dados.Usuarios.FirstOrDefault(u => u.TenantId == Tenant && u.Id == userId);
        return Task.FromResult(usuario is null ? null : Detail(usuario, now));
    }

    public Task<IReadOnlyList<UserDetail>> ListTenantScopeUsersAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserDetail>>(_dados.Usuarios.Where(u => u.TenantId == Tenant && _dados.Concessoes.Any(g => g.UserId == u.Id && g.OrganizationId is null)).Select(u => Detail(u, now)).ToList());

    public Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid? organizationId, Permission permission, CancellationToken cancellationToken) =>
        Task.FromResult(_dados.Ajustes.FirstOrDefault(o => o.UserId == userId && o.OrganizationId == organizationId && o.Permission == permission));

    public Task AddOverrideAsync(UserPermissionOverride item, CancellationToken cancellationToken)
    {
        _dados.Ajustes.Add(item);
        return Task.CompletedTask;
    }

    public Task RemoveOverrideAsync(UserPermissionOverride item, CancellationToken cancellationToken)
    {
        _dados.Ajustes.Remove(item);
        return Task.CompletedTask;
    }

    public Task<int> RemoveOverridesAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken) =>
        Task.FromResult(_dados.Ajustes.RemoveAll(o => o.UserId == userId && o.OrganizationId == organizationId));

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Monta, sobre as fakes em memória, os serviços da administração de usuários com as mesmas dependências de produção.</summary>
internal static class AdminFabrica
{
    public static AdminEmMemoria Loja(IdentidadeEmMemoria dados, RequestContextHolder holder) => new(dados, holder);

    public static UserAdmissionGuard Guard(IdentidadeEmMemoria dados, RequestContextHolder holder, EmbeddedEmMemoria? embedded = null) =>
        new(new AdminEmMemoria(dados, holder) { Vinculos = embedded is null ? null : () => embedded.Vinculos });

    public static UserAdminService Admin(
        IdentidadeEmMemoria dados, ITenantDirectory tenants, IOrganizationDirectory organizacoes, PasswordHasher hasher, IdentityOptions opcoes,
        RequestContextHolder holder, TimeProvider relogio, IAuditWriter auditoria)
    {
        var loja = Loja(dados, holder);
        return new UserAdminService(dados, dados, dados, loja, organizacoes, new UserAdmissionGuard(loja), hasher, opcoes, holder, relogio, auditoria);
    }

    public static UserService Usuarios(
        IdentidadeEmMemoria dados, ITenantDirectory tenants, IOrganizationDirectory organizacoes, PasswordHasher hasher, IdentityOptions opcoes,
        RequestContextHolder holder, TimeProvider relogio, IAuditWriter auditoria)
    {
        var loja = Loja(dados, holder);
        var guard = new UserAdmissionGuard(loja);
        var admin = new UserAdminService(dados, dados, dados, loja, organizacoes, guard, hasher, opcoes, holder, relogio, auditoria);
        return new UserService(dados, dados, organizacoes, hasher, holder, relogio, auditoria, admin, loja, guard, dados, opcoes);
    }

    public static PasswordService Senha(
        IdentidadeEmMemoria dados, ITenantDirectory tenants, IOrganizationDirectory organizacoes, PasswordHasher hasher, IdentityOptions opcoes,
        RequestContextHolder holder, TimeProvider relogio, IAuditWriter auditoria, Func<Task>? aoEntrarNaTrava = null)
    {
        var loja = Loja(dados, holder);
        loja.AoEntrarNaTrava = aoEntrarNaTrava;
        var guard = new UserAdmissionGuard(loja);
        var admin = new UserAdminService(dados, dados, dados, loja, organizacoes, guard, hasher, opcoes, holder, relogio, auditoria);
        return new PasswordService(tenants, dados, dados, organizacoes, hasher, opcoes, relogio, auditoria, holder, admin, loja, guard);
    }
}
