namespace IaBimeta.AiGateway.Contracts;

/// <summary>
/// O consumo normalizado de uma chamada (ADR 0017, seção C.3). Cada adaptador converte o que o provedor informa para estes campos; os valores nunca são
/// negativos, e as parcelas de cache nunca passam do total de entrada (o AiGateway corrige o que não obedecer). O consumo vale também quando a chamada
/// falha depois de gastar tokens (por exemplo, uma recusa do modelo).
/// </summary>
/// <param name="InputTokens">Os tokens de entrada, no TOTAL: incluem os lidos do cache e os escritos no cache.</param>
/// <param name="CachedInputTokens">Os tokens de entrada lidos do cache do prompt (já contados em <paramref name="InputTokens"/>).</param>
/// <param name="CacheWriteTokens">Os tokens de entrada escritos no cache do prompt (já contados em <paramref name="InputTokens"/>).</param>
/// <param name="OutputTokens">Os tokens de saída, incluindo os de raciocínio (que são cobrados como saída).</param>
/// <param name="ReasoningTokens">Os tokens de raciocínio, quando o provedor os informa (já contados em <paramref name="OutputTokens"/>).</param>
public sealed record AiUsage(int InputTokens, int CachedInputTokens, int CacheWriteTokens, int OutputTokens, int? ReasoningTokens = null)
{
    /// <summary>Nenhum consumo (a chamada falhou antes de gastar tokens).</summary>
    public static AiUsage None { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Os erros normalizados (ADR 0017, seção C.4). Cada adaptador mapeia os códigos do provedor para esta lista, e o Agents só conhece esta lista.
/// Só <see cref="Transient"/> é retentável.
/// </summary>
public enum AiErrorKind
{
    /// <summary>Tempo esgotado, 5xx, sobrecarga ou limite de taxa (com ou sem espera sugerida). A única falha que se tenta de novo.</summary>
    Transient = 1,

    /// <summary>A chave do tenant foi recusada (401 e 403) ou não existe.</summary>
    InvalidCredentials = 2,

    /// <summary>Cobrança, gasto ou crédito esgotados na conta do tenant. NÃO é retentável, mesmo que o provedor sugira uma espera.</summary>
    QuotaOrBilling = 3,

    /// <summary>O pedido é inválido para o provedor ou para o modelo.</summary>
    InvalidRequest = 4,

    /// <summary>O contexto enviado não cabe na janela do modelo.</summary>
    ContextTooLong = 5,

    /// <summary>O provedor recusou o pedido por política (diferente de o modelo recusar a resposta: <see cref="AiStopReason.Refused"/>).</summary>
    Refused = 6,

    /// <summary>Um limite do tenant no AiGateway foi atingido (chamadas simultâneas, chamadas ou tokens por período, mensagens por turno).</summary>
    TenantLimitExceeded = 7,

    /// <summary>
    /// O tenant não tem uma configuração de IA utilizável: nenhuma, modelo fora do catálogo ou desativado, provedor sem adaptador ou origem de
    /// credencial ainda não suportada. Só o AiGateway produz este valor: nenhum adaptador o devolve.
    /// </summary>
    NotConfigured = 8,
}

/// <summary>
/// A falha de uma chamada ao provedor, como o adaptador a informa. Sem mensagem livre: o texto que um provedor devolve pode repetir trechos do pedido, e
/// o contrato não carrega prompt, resposta nem credencial fora dos campos próprios.
/// </summary>
/// <param name="Kind">A categoria normalizada.</param>
/// <param name="RetryAfter">A espera que o provedor sugeriu, quando houver (só tem efeito em <see cref="AiErrorKind.Transient"/>).</param>
/// <param name="Code">O código do próprio provedor (por exemplo, o <c>error.code</c>), só para diagnóstico. Nunca contém segredo.</param>
public sealed record AiError(AiErrorKind Kind, TimeSpan? RetryAfter = null, string? Code = null);
