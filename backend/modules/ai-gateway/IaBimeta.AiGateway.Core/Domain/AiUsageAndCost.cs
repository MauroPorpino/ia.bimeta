using IaBimeta.AiGateway.Contracts;

namespace IaBimeta.AiGateway.Domain;

/// <summary>
/// Garante as invariantes do consumo normalizado antes de ele servir ao custo e ao registro: nenhum valor negativo, as parcelas de cache dentro do total
/// de entrada e o raciocínio dentro da saída. Quem informa o consumo é o adaptador, que pode errar; o que não obedece é CORRIGIDO para o valor coerente
/// mais próximo, e nunca produz custo negativo nem entrada não cacheada negativa.
/// </summary>
public static class AiUsageNormalizer
{
    public static AiUsage Normalize(AiUsage? usage)
    {
        if (usage is null)
        {
            return AiUsage.None;
        }

        var entrada = Math.Max(usage.InputTokens, 0);
        var emCache = Math.Min(Math.Max(usage.CachedInputTokens, 0), entrada);
        var escritaDeCache = Math.Min(Math.Max(usage.CacheWriteTokens, 0), entrada - emCache);
        var saida = Math.Max(usage.OutputTokens, 0);
        int? raciocinio = usage.ReasoningTokens is { } r ? Math.Min(Math.Max(r, 0), saida) : null;

        return new AiUsage(entrada, emCache, escritaDeCache, saida, raciocinio);
    }
}

/// <summary>
/// A estimativa de custo (ADR 0017, seção I.1): o consumo normalizado vezes a tabela de preços do modelo no catálogo. É ESTIMATIVA, não fatura (o
/// preço real, o desconto e a cobrança são do provedor). Cada tipo de token tem o seu preço: a entrada comum, a lida do cache, a escrita no cache e a
/// saída (que inclui o raciocínio). O cálculo é exato (<see cref="decimal"/>), sem arredondamento.
/// </summary>
public static class AiCostEstimator
{
    private const decimal TokensPerPriceUnit = 1_000_000m;

    public static AiCostEstimate Estimate(AiUsage usage, AiModelPrices prices)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(prices);

        var normalizado = AiUsageNormalizer.Normalize(usage);
        var entradaComum = normalizado.InputTokens - normalizado.CachedInputTokens - normalizado.CacheWriteTokens;

        var valor =
            (entradaComum * prices.InputPerMillion
             + normalizado.CachedInputTokens * prices.CachedInputPerMillion
             + normalizado.CacheWriteTokens * prices.CacheWritePerMillion
             + normalizado.OutputTokens * prices.OutputPerMillion)
            / TokensPerPriceUnit;

        return new AiCostEstimate(valor, prices.Currency);
    }
}
