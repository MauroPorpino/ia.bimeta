using System.Text.RegularExpressions;
using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// O catálogo dos modelos suportados (ADR 0017, seção D.1), montado a partir da CONFIGURAÇÃO: provedor, modelo, perfil, janela de contexto, preço por tipo
/// de token e situação. Só um modelo do catálogo pode ser escolhido por um tenant. O catálogo é imutável depois de criado, e uma entrada inválida (preço
/// negativo, modelo repetido, identificador sem sentido) falha na criação, na partida do módulo, e não numa chamada. O código não conhece provedor pelo nome:
/// um provedor novo é um adaptador e novas entradas aqui.
/// </summary>
public sealed class AiModelCatalog
{
    private static readonly Regex ProviderIdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,49}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModelIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProfilePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CurrencyPattern = new(@"^[A-Z]{3}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<(string ProviderId, string ModelId), AiModelDescriptor> _models;

    private AiModelCatalog(Dictionary<(string ProviderId, string ModelId), AiModelDescriptor> models)
    {
        _models = models;
        Models = models.Values.OrderBy(m => m.ProviderId, StringComparer.Ordinal).ThenBy(m => m.ModelId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Todos os modelos do catálogo, em ordem estável (provedor e modelo), inclusive os desativados.</summary>
    public IReadOnlyList<AiModelDescriptor> Models { get; }

    /// <summary>Os provedores que têm ao menos um modelo no catálogo.</summary>
    public IReadOnlyList<string> ProviderIds => Models.Select(m => m.ProviderId).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Indica se o texto é um identificador de provedor válido (minúsculas, números e <c>._-</c>; de 1 a 50 caracteres).</summary>
    public static bool IsValidProviderId(string? providerId) => providerId is not null && ProviderIdPattern.IsMatch(providerId);

    /// <exception cref="AiGatewayConfigurationException">Alguma entrada é inválida ou repetida.</exception>
    public static AiModelCatalog Create(IEnumerable<AiModelDescriptor> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var porChave = new Dictionary<(string, string), AiModelDescriptor>();
        foreach (var modelo in models)
        {
            if (modelo is null)
            {
                throw new AiGatewayConfigurationException("O catálogo de modelos tem uma entrada nula.");
            }

            Validate(modelo);
            if (!porChave.TryAdd((modelo.ProviderId, modelo.ModelId), modelo))
            {
                throw new AiGatewayConfigurationException($"O modelo '{modelo.ModelId}' do provedor '{modelo.ProviderId}' aparece mais de uma vez no catálogo.");
            }
        }

        return new AiModelCatalog(porChave);
    }

    /// <summary>O modelo do provedor, ou nulo se ele não faz parte do catálogo. A busca diferencia maiúsculas de minúsculas.</summary>
    public AiModelDescriptor? Find(string? providerId, string? modelId) =>
        providerId is null || modelId is null ? null : _models.GetValueOrDefault((providerId, modelId));

    /// <summary>Os modelos de um provedor que podem ser escolhidos hoje (<see cref="AiModelStatus.Active"/>).</summary>
    public IReadOnlyList<AiModelDescriptor> ActiveModelsOf(string providerId) =>
        Models.Where(m => m.ProviderId == providerId && m.Status == AiModelStatus.Active).ToList();

    private static void Validate(AiModelDescriptor modelo)
    {
        var nome = $"{modelo.ProviderId}/{modelo.ModelId}";

        if (!IsValidProviderId(modelo.ProviderId))
        {
            throw new AiGatewayConfigurationException($"O identificador de provedor '{modelo.ProviderId}' do catálogo é inválido (minúsculas, números e '._-', de 1 a 50 caracteres).");
        }

        if (modelo.ModelId is null || !ModelIdPattern.IsMatch(modelo.ModelId))
        {
            throw new AiGatewayConfigurationException($"O identificador de modelo '{modelo.ModelId}' do catálogo é inválido.");
        }

        if (modelo.Profile is null || !ProfilePattern.IsMatch(modelo.Profile))
        {
            throw new AiGatewayConfigurationException($"O perfil do modelo '{nome}' é inválido.");
        }

        if (modelo.ContextWindowTokens <= 0 || modelo.MaxOutputTokens <= 0 || modelo.MaxOutputTokens > modelo.ContextWindowTokens)
        {
            throw new AiGatewayConfigurationException($"A janela de contexto e o teto de saída do modelo '{nome}' precisam ser positivos, com o teto de saída dentro da janela.");
        }

        if (!Enum.IsDefined(modelo.Status))
        {
            throw new AiGatewayConfigurationException($"A situação do modelo '{nome}' é desconhecida.");
        }

        var precos = modelo.Prices ?? throw new AiGatewayConfigurationException($"O modelo '{nome}' não tem tabela de preços.");
        if (precos.Currency is null || !CurrencyPattern.IsMatch(precos.Currency))
        {
            throw new AiGatewayConfigurationException($"A moeda dos preços do modelo '{nome}' deve ter três letras maiúsculas.");
        }

        if (precos.InputPerMillion < 0 || precos.CachedInputPerMillion < 0 || precos.CacheWritePerMillion < 0 || precos.OutputPerMillion < 0)
        {
            throw new AiGatewayConfigurationException($"Os preços do modelo '{nome}' não podem ser negativos.");
        }
    }
}
