using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>
/// Persistência do módulo. Toda consulta, exceto <see cref="ResolveRouteAsync"/>, só enxerga o tenant do contexto (TEN-2 e
/// TEN-3) e falha fechada sem contexto. As entidades devolvidas por <c>Find</c> são rastreadas: o caso de uso as altera e chama
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IChannelRepository
{
    /// <exception cref="ChannelIdentifierInUseException">O identificador da conta já está cadastrado.</exception>
    Task AddAccountAsync(ChannelAccount account, CancellationToken cancellationToken);

    Task<ChannelAccount?> FindAccountAsync(Guid id, CancellationToken cancellationToken);

    /// <param name="organizationIds">As organizações visíveis, ou <c>null</c> para todas as do tenant.</param>
    Task<IReadOnlyList<ChannelAccount>> ListAccountsAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChannelSecret>> ListSecretsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    Task<ChannelSecret?> FindSecretAsync(Guid accountId, ChannelSecretKind kind, CancellationToken cancellationToken);

    Task AddSecretAsync(ChannelSecret secret, CancellationToken cancellationToken);

    /// <summary>Grava a instância e a rota de entrada dela na mesma transação.</summary>
    /// <exception cref="ChannelIdentifierInUseException">O identificador do número já está cadastrado.</exception>
    Task AddInstanceAsync(ChannelInstance instance, CancellationToken cancellationToken);

    /// <summary>A instância com as unidades autorizadas, rastreada para alteração.</summary>
    Task<ChannelInstance?> FindInstanceAsync(ChannelInstanceId id, CancellationToken cancellationToken);

    /// <summary>A instância com as unidades autorizadas, só para leitura.</summary>
    Task<ChannelInstance?> FindInstanceReadOnlyAsync(ChannelInstanceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChannelInstance>> ListInstancesAsync(IReadOnlyCollection<Guid>? organizationIds, CancellationToken cancellationToken);

    /// <summary>Grava as alterações das entidades rastreadas.</summary>
    /// <exception cref="ChannelsConflictException">Outra operação alterou o mesmo recurso antes.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a rota de entrada a partir do identificador do provedor, SEM contexto de tenant: é a única consulta que roda antes
    /// de o tenant ser conhecido. Devolve só as chaves de roteamento (nunca configuração nem credencial).
    /// </summary>
    Task<ChannelRouteResolution?> ResolveRouteAsync(ChannelProvider provider, string externalId, CancellationToken cancellationToken);
}

/// <summary>
/// Cifra e decifra as credenciais. A chave mestra vive fora do banco (configuração do ambiente, depois um cofre). O contexto
/// amarra o texto cifrado ao tenant, à referência e ao papel: o mesmo texto cifrado não abre em outro lugar.
/// </summary>
public interface ISecretProtector
{
    /// <exception cref="ChannelSecretsNotConfiguredException">Não há chave ativa configurada.</exception>
    ProtectedSecret Protect(string plaintext, SecretBinding binding);

    /// <exception cref="ChannelSecretsNotConfiguredException">A chave que cifrou o valor não está configurada.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">O texto foi adulterado ou pertence a outro tenant, referência ou papel.</exception>
    string Unprotect(ProtectedSecret secret, SecretBinding binding);
}

public sealed record ProtectedSecret(string KeyId, string Ciphertext);

/// <summary>A que a credencial está amarrada: entra como dado autenticado adicional na cifra.</summary>
public readonly record struct SecretBinding(TenantId TenantId, Guid SecretId, ChannelSecretKind Kind);

/// <summary>
/// O resultado da resolução da rota de entrada: só o necessário para escolher o segredo da assinatura e, depois de validá-la,
/// montar o contexto de tenant.
/// </summary>
public sealed record ChannelRouteResolution(ChannelInstanceId ChannelInstanceId, TenantId TenantId, ChannelStatus Status);
