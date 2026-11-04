namespace IaBimeta.AiGateway.Contracts;

/// <summary>
/// O que o Agents (ou outro consumidor interno) pede ao AiGateway. O pedido NÃO nomeia provedor, modelo nem credencial: o AiGateway escolhe pelo tenant
/// do contexto confiável (configuração do tenant) e resolve a credencial na hora. Não há <c>temperature</c>, <c>top_p</c> nem controle de raciocínio: a
/// qualidade se sustenta por perfil de modelo, na configuração do adaptador (ADR 0017, seção C.2).
/// </summary>
/// <param name="CorrelationId">Correlação opaca (por exemplo, o <c>TurnId</c>), devolvida ao registro de consumo. O AiGateway não lhe dá significado. De 1 a 100 caracteres.</param>
/// <param name="SystemInstruction">A instrução de sistema (camada base mais instruções do tenant). Não pode ser vazia.</param>
/// <param name="Messages">As mensagens, em ordem, só texto. Não pode ser vazia, e a última é do usuário (<see cref="AiMessageRole.User"/>).</param>
/// <param name="MaxOutputTokens">O teto de tokens de saída que o consumidor pede. O AiGateway o limita ao teto do tenant e ao do modelo.</param>
/// <param name="Timeout">O tempo limite da chamada. O AiGateway o limita ao máximo configurado.</param>
/// <param name="OutputMode">Texto livre ou JSON conforme <paramref name="StructuredOutput"/>.</param>
/// <param name="StructuredOutput">O esquema, obrigatório com <see cref="AiOutputMode.StructuredJson"/> e proibido com <see cref="AiOutputMode.Text"/>.</param>
public sealed record AiGenerationRequest(
    string CorrelationId,
    string SystemInstruction,
    IReadOnlyList<AiMessage> Messages,
    int MaxOutputTokens,
    TimeSpan Timeout,
    AiOutputMode OutputMode = AiOutputMode.Text,
    AiStructuredOutput? StructuredOutput = null)
{
    /// <summary>Não imprime a instrução nem as mensagens: o pedido carrega conteúdo de atendimento e nunca deve ir a log por acidente.</summary>
    public override string ToString() => $"AiGenerationRequest {{ CorrelationId = {CorrelationId}, OutputMode = {OutputMode}, Messages = {Messages?.Count ?? 0} }}";
}

/// <summary>
/// O que o AiGateway pede a UM adaptador de provedor (BOUNDARIES §7). É o pedido já resolvido: modelo do catálogo, perfil, tetos já limitados e a
/// credencial do tenant em memória. O adaptador devolve sempre um <see cref="AiCompletionResult"/>: falha do provedor é resultado, não exceção.
/// </summary>
/// <param name="AiRequestId">O identificador desta chamada, gerado pelo AiGateway (uma por chamada ao modelo).</param>
/// <param name="CorrelationId">A correlação opaca do consumidor (por exemplo, o <c>TurnId</c>).</param>
/// <param name="ModelId">O modelo, como o provedor o conhece (do catálogo).</param>
/// <param name="Profile">O perfil do modelo, do catálogo: o adaptador o usa para escolher os parâmetros que sustentam a qualidade.</param>
/// <param name="SystemInstruction">A instrução de sistema.</param>
/// <param name="Messages">As mensagens, só texto.</param>
/// <param name="MaxOutputTokens">O teto de tokens de saída, já limitado.</param>
/// <param name="Timeout">O tempo limite, já limitado.</param>
/// <param name="OutputMode">Texto livre ou JSON conforme <paramref name="StructuredOutput"/>.</param>
/// <param name="StructuredOutput">O esquema, quando o modo é <see cref="AiOutputMode.StructuredJson"/>.</param>
/// <param name="Credential">A credencial do tenant, em memória, resolvida na hora. Nunca vai a log, a erro nem a resultado.</param>
public sealed record AiCompletionRequest(
    Guid AiRequestId,
    string CorrelationId,
    string ModelId,
    string Profile,
    string SystemInstruction,
    IReadOnlyList<AiMessage> Messages,
    int MaxOutputTokens,
    TimeSpan Timeout,
    AiOutputMode OutputMode,
    AiStructuredOutput? StructuredOutput,
    AiCredential Credential)
{
    /// <summary>Não imprime a instrução, as mensagens nem a credencial.</summary>
    public override string ToString() => $"AiCompletionRequest {{ AiRequestId = {AiRequestId}, ModelId = {ModelId}, OutputMode = {OutputMode} }}";
}

/// <summary>
/// O resultado de UMA chamada a um adaptador: uma resposta (<see cref="Completion"/>) ou uma falha normalizada (<see cref="Error"/>), nunca as duas
/// nem nenhuma. O consumo e a latência valem nos dois casos.
/// </summary>
/// <param name="Completion">A resposta, quando a chamada deu certo.</param>
/// <param name="Error">A falha normalizada, quando não deu.</param>
/// <param name="Usage">O consumo normalizado (<see cref="AiUsage.None"/> se nada foi gasto).</param>
/// <param name="Latency">A duração da chamada ao provedor.</param>
/// <param name="ProviderRequestId">O identificador da requisição no provedor (por exemplo, o cabeçalho de identificação), quando houver.</param>
/// <param name="UsageCertainty">
/// O quanto o consumo informado é confiável (ADR 0017, ajuste B do ledger). O adaptador NUNCA presume ausência de consumo: <see cref="AiUsageCertainty.NoneIncurred"/> exige evidência
/// de que nenhuma chamada faturável ocorreu. O padrão da falha é <see cref="AiUsageCertainty.Unknown"/>, que o AiGateway contabiliza pela reserva (limite superior).
/// </param>
public sealed record AiCompletionResult(
    AiCompletion? Completion,
    AiError? Error,
    AiUsage Usage,
    TimeSpan Latency,
    string? ProviderRequestId = null,
    AiUsageCertainty UsageCertainty = AiUsageCertainty.Unknown)
{
    /// <summary>Verdadeiro quando há resposta e não há falha.</summary>
    public bool IsSuccess => Completion is not null && Error is null;

    /// <summary>A chamada deu certo: o provedor devolveu a resposta, e o consumo é o que ele informou (<see cref="AiUsageCertainty.Reported"/>).</summary>
    public static AiCompletionResult Success(AiCompletion completion, AiUsage usage, TimeSpan latency, string? providerRequestId = null) =>
        new(completion, null, usage, latency, providerRequestId, AiUsageCertainty.Reported);

    /// <summary>
    /// A chamada falhou. Sem informar a certeza, vale <see cref="AiUsageCertainty.Unknown"/>: só use <see cref="AiUsageCertainty.NoneIncurred"/> com evidência de que nada foi
    /// faturado (uma rejeição de admissão do provedor, com resposta completa e sem <c>usage</c>), e <see cref="AiUsageCertainty.Reported"/> quando o provedor informou o consumo.
    /// </summary>
    public static AiCompletionResult Failure(
        AiError error, TimeSpan latency, AiUsage? usage = null, string? providerRequestId = null, AiUsageCertainty usageCertainty = AiUsageCertainty.Unknown) =>
        new(null, error, usage ?? AiUsage.None, latency, providerRequestId, usageCertainty);
}

/// <summary>O quanto o consumo de uma chamada ao provedor é conhecido.</summary>
public enum AiUsageCertainty
{
    /// <summary>O provedor informou o consumo (<c>usage</c>): é o valor que vale, também numa falha que o cobrou (por exemplo, uma recusa do modelo).</summary>
    Reported = 1,

    /// <summary>Há evidência suficiente de que NENHUMA chamada faturável ocorreu (rejeição de admissão com resposta completa e sem consumo): o consumo é zero.</summary>
    NoneIncurred = 2,

    /// <summary>
    /// Não se sabe se houve consumo nem quanto (tempo esgotado, queda no meio da resposta, 5xx, resposta sem <c>usage</c>, adaptador que lançou exceção, desistência do chamador).
    /// O AiGateway contabiliza como INDETERMINADO: cobra a reserva (limite superior) e mantém a possibilidade de reconciliar depois.
    /// </summary>
    Unknown = 3,
}

/// <summary>
/// A falha que o AiGateway devolve ao consumidor, já com a política de erro aplicada: <paramref name="Retryable"/> é verdadeiro só para a falha
/// transitória, e quem tenta de novo, quantas vezes e com que espera é o consumidor (o Agents).
/// </summary>
/// <param name="Kind">A categoria normalizada.</param>
/// <param name="Retryable">Se vale tentar de novo. Só <see cref="AiErrorKind.Transient"/>.</param>
/// <param name="RetryAfter">A espera sugerida (limitada pelo AiGateway), só quando <paramref name="Retryable"/>.</param>
/// <param name="Code">Um código estável para diagnóstico (do AiGateway ou do provedor). Nunca contém segredo, prompt nem resposta.</param>
public sealed record AiFailure(AiErrorKind Kind, bool Retryable, TimeSpan? RetryAfter = null, string? Code = null);

/// <summary>O custo ESTIMADO da chamada, calculado com a tabela de preços do catálogo. Não é fatura.</summary>
public sealed record AiCostEstimate(decimal Amount, string Currency);

/// <summary>
/// O resultado que o consumidor recebe: a resposta ou a falha (nunca as duas), o consumo, o custo estimado e a latência. O provedor e o modelo
/// são os que o AiGateway escolheu para o tenant (nulos quando a falha ocorreu antes da escolha).
/// </summary>
/// <param name="AiRequestId">O identificador da chamada, gerado pelo AiGateway, para a correlação do registro de resposta da IA.</param>
/// <param name="ProviderId">O provedor escolhido, ou nulo se a escolha falhou.</param>
/// <param name="ModelId">O modelo escolhido, ou nulo se a escolha falhou.</param>
/// <param name="Completion">A resposta, quando a chamada deu certo.</param>
/// <param name="Failure">A falha, quando não deu.</param>
/// <param name="Usage">O consumo normalizado.</param>
/// <param name="EstimatedCost">O custo estimado, ou nulo quando não há modelo escolhido.</param>
/// <param name="Latency">A duração da chamada ao provedor (zero se ela não chegou a ser feita).</param>
/// <param name="ProviderRequestId">O identificador da requisição no provedor, quando houver.</param>
public sealed record AiGenerationResult(
    Guid AiRequestId,
    string? ProviderId,
    string? ModelId,
    AiCompletion? Completion,
    AiFailure? Failure,
    AiUsage Usage,
    AiCostEstimate? EstimatedCost,
    TimeSpan Latency,
    string? ProviderRequestId = null)
{
    /// <summary>Verdadeiro quando há resposta e não há falha.</summary>
    public bool IsSuccess => Completion is not null && Failure is null;
}
