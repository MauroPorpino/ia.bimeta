using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Application;

/// <summary>O armazenamento das conexões do tenant do contexto (filtro global por tenant). As entidades voltam RASTREADAS para a alteração.</summary>
public interface IConnectionStore
{
    Task<IntegrationConnection?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationConnection>> ListAsync(CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    void Add(IntegrationConnection connection);

    /// <exception cref="CapabilitiesConflictException"><c>key-in-use</c> ou <c>changed</c>.</exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IDefinitionStore
{
    Task<CapabilityDefinition?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<CapabilityDefinition?> FindPublishedAsync(string key, CancellationToken cancellationToken);

    Task<CapabilityDefinition?> FindDraftAsync(string key, CancellationToken cancellationToken);

    /// <summary>Todas as versões de uma chave, da mais nova para a mais antiga.</summary>
    Task<IReadOnlyList<CapabilityDefinition>> ListVersionsAsync(string key, CancellationToken cancellationToken);

    /// <summary>A versão mais recente de cada chave (publicada, ou o rascunho, ou a aposentada mais nova).</summary>
    Task<IReadOnlyList<CapabilityDefinition>> ListLatestAsync(Guid? connectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityDefinition>> ListPublishedAsync(CancellationToken cancellationToken);

    Task<int> CountKeysAsync(CancellationToken cancellationToken);

    Task<int> MaxVersionAsync(string key, CancellationToken cancellationToken);

    void Add(CapabilityDefinition definition);

    /// <exception cref="CapabilitiesConflictException"><c>key-in-use</c> (já há rascunho ou publicada) ou <c>changed</c>.</exception>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Executa o trabalho numa transação do banco (tudo ou nada). Publicar usa: a anterior sai de cena e a nova entra juntas.</summary>
    Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken);
}

public sealed record ExecutionFilter(Guid? ConnectionId, string? Key, CapabilityExecutionStatus? Status, bool? IsTest);

public sealed record ConnectionStats(Guid ConnectionId, int Total, int Failed, int Unknown, DateTimeOffset? LastAt);

public interface IExecutionStore
{
    /// <summary>Grava a execução. Se a chave de idempotência já existe no tenant, NÃO grava e devolve a existente (a disputa entre duas chamadas simultâneas é decidida pelo índice único do banco).</summary>
    Task<(bool Added, CapabilityExecution Execution)> TryAddAsync(CapabilityExecution execution, CancellationToken cancellationToken);

    Task<CapabilityExecution?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <exception cref="CapabilitiesConflictException"><c>already-confirmed</c> (outra operação mudou a execução primeiro).</exception>
    Task SaveAsync(CancellationToken cancellationToken);

    Task<int> CountSinceAsync(DateTimeOffset since, Guid? connectionId, bool writesOnly, CancellationToken cancellationToken);

    Task<int> CountInFlightAsync(Guid connectionId, DateTimeOffset notBefore, CancellationToken cancellationToken);

    Task<IReadOnlyList<CapabilityExecution>> ListAsync(ExecutionFilter filter, IReadOnlySet<Guid>? organizations, int take, int skip, CancellationToken cancellationToken);

    Task<int> CountAsync(ExecutionFilter filter, IReadOnlySet<Guid>? organizations, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionStats>> StatsAsync(DateTimeOffset since, IReadOnlySet<Guid>? organizations, CancellationToken cancellationToken);
}

/// <summary>A que um segredo cifrado pertence. Entra como dado autenticado: o mesmo texto cifrado não abre sob outro tenant, outro recurso, outra finalidade nem outra versão.</summary>
public sealed record SecretBinding(string Purpose, TenantId TenantId, Guid ResourceId, string Discriminator);

public sealed record ProtectedSecret(string KeyId, string Ciphertext);

/// <summary>A cifra autenticada dos segredos do módulo (chaves próprias, <c>Capabilities:Secrets</c>). Sem chave configurada, falha fechada.</summary>
public interface ISecretProtector
{
    /// <exception cref="CapabilitiesUnavailableException">Não há chave ativa configurada.</exception>
    ProtectedSecret Protect(string plaintext, SecretBinding binding);

    /// <exception cref="CapabilitiesUnavailableException">A chave não está configurada ou o texto foi alterado ou pertence a outro contexto.</exception>
    string Unprotect(ProtectedSecret secret, SecretBinding binding);

    /// <summary>A impressão digital (HMAC-SHA256 com a chave ativa, ou SHA-256 se não houver) de um texto canônico.</summary>
    string Fingerprint(string canonical);

    bool IsConfigured { get; }
}

/// <summary>Os tipos de conector registrados (<c>rest-json</c> e os adaptadores futuros).</summary>
public interface IConnectorRegistry
{
    ICapabilityConnector? Get(string kind);
}

internal sealed class ConnectorRegistry : IConnectorRegistry
{
    private readonly IReadOnlyDictionary<string, ICapabilityConnector> _connectors;

    public ConnectorRegistry(IEnumerable<ICapabilityConnector> connectors)
    {
        _connectors = connectors.ToDictionary(c => c.Kind, StringComparer.Ordinal);
    }

    public ICapabilityConnector? Get(string kind) => _connectors.GetValueOrDefault(kind);
}
