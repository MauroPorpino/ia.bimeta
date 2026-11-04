using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Application;

/// <summary>Os adaptadores de provedor registrados, indexados pelo identificador que cada um declara. O módulo não conhece provedor pelo nome.</summary>
public sealed class AiProviderRegistry
{
    private readonly Dictionary<string, IAiProvider> _providers = new(StringComparer.Ordinal);

    /// <exception cref="AiGatewayConfigurationException">Um adaptador declara identificador inválido ou dois declaram o mesmo.</exception>
    public AiProviderRegistry(IEnumerable<IAiProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        foreach (var provedor in providers)
        {
            if (provedor is null || !AiModelCatalog.IsValidProviderId(provedor.ProviderId))
            {
                throw new AiGatewayConfigurationException("Um adaptador de provedor declara um identificador inválido (minúsculas, números e '._-').");
            }

            if (!_providers.TryAdd(provedor.ProviderId, provedor))
            {
                throw new AiGatewayConfigurationException($"Mais de um adaptador declara o provedor '{provedor.ProviderId}'.");
            }
        }
    }

    public IAiProvider? Find(string? providerId) => providerId is null ? null : _providers.GetValueOrDefault(providerId);

    public IReadOnlyCollection<string> ProviderIds => _providers.Keys;
}

/// <summary>O que a seleção concluiu para uma configuração de tenant.</summary>
public enum AiSelectionOutcome
{
    /// <summary>A configuração é utilizável: o modelo está no catálogo e ativo, o adaptador está registrado e a origem da credencial é suportada.</summary>
    Selected = 1,

    /// <summary>O tenant não tem configuração de IA.</summary>
    NotConfigured = 2,

    /// <summary>A configuração lida pertence a OUTRO tenant: nunca é usada.</summary>
    TenantMismatch = 3,

    /// <summary>A origem da credencial não é suportada nesta fase (<see cref="AiCredentialSource.PlatformManaged"/> e qualquer valor desconhecido).</summary>
    CredentialSourceNotSupported = 4,

    /// <summary>Os limites do tenant são inválidos.</summary>
    InvalidLimits = 5,

    /// <summary>O modelo não faz parte do catálogo do provedor.</summary>
    ModelNotSupported = 6,

    /// <summary>O modelo faz parte do catálogo, mas foi desativado.</summary>
    ModelDisabled = 7,

    /// <summary>Nenhum adaptador está registrado para o provedor da configuração.</summary>
    ProviderNotRegistered = 8,

    /// <summary>A IA do tenant está desabilitada por ele ou bloqueada pela plataforma.</summary>
    TenantAiDisabled = 9,
}

/// <summary>A seleção: o desfecho e, quando <see cref="AiSelectionOutcome.Selected"/>, o modelo do catálogo e o adaptador escolhidos.</summary>
public sealed record AiSelection(AiSelectionOutcome Outcome, string? Code, AiTenantConfiguration? Configuration, AiModelDescriptor? Model, IAiProvider? Provider)
{
    public bool IsSelected => Outcome == AiSelectionOutcome.Selected;
}

/// <summary>
/// A seleção do provedor e do modelo de um tenant (ADR 0017, seção D). A mesma regra serve à chamada (a configuração guardada ainda é utilizável?) e, na
/// fatia de configuração, à validação do que o tenant propõe. O resultado depende só da configuração do TENANT, do catálogo e dos adaptadores
/// registrados: nunca há fallback para outro provedor ou modelo, e o tenant A jamais recebe o adaptador do tenant B.
/// </summary>
public sealed class AiModelSelector
{
    private readonly AiModelCatalog _catalog;
    private readonly AiProviderRegistry _providers;

    public AiModelSelector(AiModelCatalog catalog, AiProviderRegistry providers)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(providers);
        _catalog = catalog;
        _providers = providers;
    }

    public AiModelCatalog Catalog => _catalog;

    /// <param name="tenantId">O tenant do contexto confiável: a configuração precisa ser dele.</param>
    /// <param name="configuration">A configuração do tenant, ou nulo se não existe.</param>
    public AiSelection Select(TenantId tenantId, AiTenantConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (configuration is null)
        {
            return Refuse(AiSelectionOutcome.NotConfigured, AiFailureCodes.NotConfigured, null);
        }

        if (configuration.TenantId != tenantId)
        {
            // Nada da configuração alheia é usado nem devolvido.
            return Refuse(AiSelectionOutcome.TenantMismatch, AiFailureCodes.ConfigurationTenantMismatch, null);
        }

        // Habilitação: a IA desabilitada pelo tenant ou bloqueada pela plataforma nunca chega a escolher modelo nem a chamar nada (qualquer valor desconhecido também recusa).
        if (configuration.Status != AiTenantStatus.Enabled)
        {
            return Refuse(AiSelectionOutcome.TenantAiDisabled, AiFailureCodes.TenantAiDisabled, configuration);
        }

        // A origem da credencial é conferida antes do catálogo: PlatformManaged (reservada) e qualquer valor desconhecido nunca chegam a executar.
        if (configuration.CredentialSource != AiCredentialSource.Byok)
        {
            return Refuse(AiSelectionOutcome.CredentialSourceNotSupported, AiFailureCodes.CredentialSourceNotSupported, configuration);
        }

        if (configuration.Limits is null || !configuration.Limits.IsValid)
        {
            return Refuse(AiSelectionOutcome.InvalidLimits, AiFailureCodes.LimitsInvalid, configuration);
        }

        var modelo = _catalog.Find(configuration.ProviderId, configuration.ModelId);
        if (modelo is null)
        {
            return Refuse(AiSelectionOutcome.ModelNotSupported, AiFailureCodes.ModelNotSupported, configuration);
        }

        if (modelo.Status != AiModelStatus.Active)
        {
            return Refuse(AiSelectionOutcome.ModelDisabled, AiFailureCodes.ModelDisabled, configuration);
        }

        var provedor = _providers.Find(modelo.ProviderId);
        if (provedor is null)
        {
            return Refuse(AiSelectionOutcome.ProviderNotRegistered, AiFailureCodes.ProviderNotRegistered, configuration);
        }

        return new AiSelection(AiSelectionOutcome.Selected, null, configuration, modelo, provedor);
    }

    private static AiSelection Refuse(AiSelectionOutcome outcome, string code, AiTenantConfiguration? configuration) =>
        new(outcome, code, configuration, null, null);
}
