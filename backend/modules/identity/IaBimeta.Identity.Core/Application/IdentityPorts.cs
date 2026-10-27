using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Identity.Application;

/// <summary>Um usuário com a concessão que lhe dá acesso a uma organização.</summary>
public sealed record UserAccess(User User, UserOrganizationGrant Grant);

/// <summary>
/// Porta de persistência dos usuários. Opera sempre dentro do tenant do contexto da operação: um usuário de outro
/// tenant nunca é encontrado nem listado.
/// </summary>
public interface IUserRepository
{
    /// <summary>Grava o usuário e a primeira concessão juntos, ou nenhum dos dois.</summary>
    /// <exception cref="DuplicateUserEmailException">Já existe um usuário com este e-mail neste tenant.</exception>
    Task AddAsync(User user, UserOrganizationGrant initialGrant, CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
}

/// <summary>Porta de persistência das concessões, dentro do tenant do contexto da operação.</summary>
public interface IGrantRepository
{
    /// <summary>A concessão do usuário no escopo informado (organização, ou nulo para o tenant), já rastreada para alteração.</summary>
    Task<UserOrganizationGrant?> FindAsync(Guid userId, Guid? organizationId, CancellationToken cancellationToken);

    Task AddAsync(UserOrganizationGrant grant, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task RemoveAsync(UserOrganizationGrant grant, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserOrganizationGrant>> ListByUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Os usuários com acesso à organização: concessões da própria organização e as de escopo do tenant.</summary>
    Task<IReadOnlyList<UserAccess>> ListAccessAsync(Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>Porta de persistência das sessões, dentro do tenant do contexto da operação.</summary>
public interface ISessionRepository
{
    /// <summary>A sessão, já rastreada para alteração.</summary>
    Task<Session?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Operações anteriores à existência de um contexto (autenticação): o login procura o usuário de um tenant e cria a
/// sessão, e a validação do token precisa achar a sessão para descobrir o tenant. Cada método opera só dentro do
/// tenant informado, ou do tenant da sessão encontrada, e nunca enumera outro tenant.
/// </summary>
public interface IAuthenticationStore
{
    Task<User?> FindUserByEmailAsync(TenantId tenantId, string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindUserByIdAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Busca pelo hash do token (único e não enumerável). O tenant vem da própria sessão encontrada.</summary>
    Task<Session?> FindSessionByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    Task AddSessionAsync(Session session, CancellationToken cancellationToken);

    Task UpdatePasswordHashAsync(TenantId tenantId, Guid userId, string newHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserOrganizationGrant>> ListGrantsAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken);
}
