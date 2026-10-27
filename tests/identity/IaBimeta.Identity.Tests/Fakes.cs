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

    public Task AddSessionAsync(Session session, CancellationToken cancellationToken)
    {
        Sessoes.Add(session);
        return Task.CompletedTask;
    }

    public Task UpdatePasswordHashAsync(TenantId tenantId, Guid userId, string newHash, CancellationToken cancellationToken)
    {
        Usuarios.First(u => u.TenantId == tenantId && u.Id == userId).ReplacePasswordHash(newHash);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserOrganizationGrant>> ListGrantsAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserOrganizationGrant>>(Concessoes.Where(g => g.TenantId == tenantId && g.UserId == userId).ToList());
}

internal static class Senhas
{
    public const string Valida = "Senha-Longa-123!";
    public const string Outra = "Outra-Senha-456!";

    public static IdentityOptions Rapidas() => new() { PasswordHashIterations = IdentityOptions.MinimumPasswordHashIterations };
}
