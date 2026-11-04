namespace IaBimeta.AiGateway.Contracts;

/// <summary>De quem é a chave de IA que o tenant usa (ADR 0017, seção D.2).</summary>
public enum AiCredentialSource
{
    /// <summary>A chave é do próprio tenant, que contrata o provedor (a única origem implementada).</summary>
    Byok = 1,

    /// <summary>
    /// RESERVADA: a chave é da plataforma, com faturamento centralizado. O valor existe, mas a configuração é recusada como não implementada, e nada a
    /// aceita nem a executa.
    /// </summary>
    PlatformManaged = 2,
}

/// <summary>A situação de um modelo no catálogo.</summary>
public enum AiModelStatus
{
    /// <summary>O modelo pode ser escolhido e usado.</summary>
    Active = 1,

    /// <summary>O modelo foi desativado no catálogo: não pode ser escolhido, e a configuração que ainda o usa deixa de funcionar.</summary>
    Disabled = 2,
}

/// <summary>
/// O preço por MILHÃO de tokens, por tipo de token, na moeda indicada. É a tabela do catálogo (configuração) que serve ao custo ESTIMADO: não é fatura.
/// </summary>
/// <param name="Currency">O código da moeda (três letras maiúsculas, por exemplo <c>USD</c>).</param>
/// <param name="InputPerMillion">A entrada que não veio do cache nem foi escrita nele.</param>
/// <param name="CachedInputPerMillion">A entrada lida do cache do prompt.</param>
/// <param name="CacheWritePerMillion">A entrada escrita no cache do prompt.</param>
/// <param name="OutputPerMillion">A saída, incluindo o raciocínio.</param>
public sealed record AiModelPrices(string Currency, decimal InputPerMillion, decimal CachedInputPerMillion, decimal CacheWritePerMillion, decimal OutputPerMillion);

/// <summary>
/// Um modelo suportado do catálogo (ADR 0017, seção D.1). O provedor é identificado por texto (<paramref name="ProviderId"/>): nenhuma lista de
/// provedores é fechada no contrato, e um provedor novo exige só um adaptador e entradas no catálogo.
/// </summary>
/// <param name="ProviderId">O identificador do provedor (minúsculas, números e <c>._-</c>), o mesmo que o adaptador declara.</param>
/// <param name="ModelId">O identificador do modelo no provedor.</param>
/// <param name="Profile">O perfil do modelo: o nome sob o qual o adaptador guarda os parâmetros que sustentam a qualidade (esforço, teto de saída, cache).</param>
/// <param name="ContextWindowTokens">A janela de contexto, em tokens.</param>
/// <param name="MaxOutputTokens">O máximo de tokens de saída do modelo.</param>
/// <param name="Prices">A tabela de preços.</param>
/// <param name="Status">A situação no catálogo.</param>
public sealed record AiModelDescriptor(
    string ProviderId,
    string ModelId,
    string Profile,
    int ContextWindowTokens,
    int MaxOutputTokens,
    AiModelPrices Prices,
    AiModelStatus Status);
