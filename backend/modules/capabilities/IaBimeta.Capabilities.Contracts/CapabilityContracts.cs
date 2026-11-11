using System.Text.Json;

namespace IaBimeta.Capabilities.Contracts;

/// <summary>O efeito de uma operação no sistema externo. Só <see cref="Write"/> altera dados; leitura nunca é confirmada nem exige chave de idempotência.</summary>
public enum CapabilityEffect
{
    Read = 1,
    Write = 2,
}

/// <summary>
/// O que se sabe sobre repetir uma operação de escrita (ARQ-21, EFX-4). <see cref="Proven"/>: o sistema externo trata a repetição com segurança (chave de idempotência nativa ou operação naturalmente idempotente);
/// só esta permite retentativa automática. <see cref="Reconcilable"/>: não há chave, mas uma consulta confirma o efeito (nunca reenviada automaticamente). <see cref="None"/>: nada se sabe; nunca reenviada.
/// </summary>
public enum IdempotencySupport
{
    /// <summary>Não declarada. Vale para leitura; numa ESCRITA é recusada: declarar <see cref="None"/> é uma decisão, esquecer não é (ARQ-21).</summary>
    Unspecified = 0,
    None = 1,
    Reconcilable = 2,
    Proven = 3,
}

/// <summary>Quem está chamando o motor. Quem chama é o CÓDIGO confiável do módulo (nunca saída de modelo de IA); a operação declara quais origens aceita.</summary>
[Flags]
public enum CapabilityCaller
{
    None = 0,

    /// <summary>Uma pessoa, pela administração ou por uma tela do produto (exige usuário autenticado).</summary>
    Operator = 1,

    /// <summary>O agente de IA do atendimento.</summary>
    Agent = 2,

    /// <summary>Um fluxo (workflow ou chatbot visual).</summary>
    Workflow = 4,

    /// <summary>O assistente interno do colaborador.</summary>
    EmployeeAssistant = 8,

    /// <summary>O chatbot determinístico.</summary>
    Chatbot = 16,
}

public enum CapabilityParameterType
{
    String = 1,
    Integer = 2,
    Number = 3,
    Boolean = 4,

    /// <summary>Data civil, <c>yyyy-MM-dd</c>.</summary>
    Date = 5,

    /// <summary>Texto com um conjunto fechado de valores (<see cref="CapabilityParameterSpec.EnumValues"/>).</summary>
    Enum = 6,
}

/// <summary>Onde o valor do parâmetro vai na chamada. O molde da operação fixa o lugar: o chamador só informa o VALOR.</summary>
public enum CapabilityParameterLocation
{
    Path = 1,
    Query = 2,
    Header = 3,
    Body = 4,
}

public enum CapabilityFieldType
{
    String = 1,
    Integer = 2,
    Number = 3,
    Boolean = 4,
}

/// <summary>
/// Um parâmetro declarado de uma operação. <see cref="Name"/> é o nome que o chamador usa; <see cref="WireName"/> é o nome na chamada externa (padrão: o mesmo; no corpo aceita caminho com ponto, por exemplo
/// <c>paciente.documento</c>). <see cref="Personal"/> marca dado pessoal: nunca vai a log nem auditoria (nenhum valor vai, de qualquer forma) e serve a minimização futura.
/// </summary>
public sealed record CapabilityParameterSpec(
    string Name,
    CapabilityParameterType Type,
    CapabilityParameterLocation Location,
    bool Required,
    string? Description = null,
    string? WireName = null,
    int? MaxLength = null,
    string? Pattern = null,
    decimal? Min = null,
    decimal? Max = null,
    IReadOnlyList<string>? EnumValues = null,
    string? Default = null,
    bool Personal = false);

/// <summary>Um campo da resposta que o motor devolve. <see cref="Pointer"/> é um JSON Pointer (RFC 6901) relativo ao objeto da resposta (ou a cada item, se a operação mapeia uma lista).</summary>
public sealed record CapabilityResponseField(
    string Name,
    string Pointer,
    CapabilityFieldType Type,
    bool Required = true,
    int? MaxLength = null,
    bool Personal = false);

/// <summary>O molde de uma operação REST/JSON. Tudo que sai na chamada vem DAQUI mais os valores validados dos parâmetros: nunca de texto livre.</summary>
public sealed record CapabilityOperationSpec(
    string Method,
    string PathTemplate,
    IReadOnlyList<CapabilityParameterSpec> Parameters,
    IReadOnlyList<CapabilityResponseField> Response,
    IdempotencySupport Idempotency,
    string? IdempotencyHeader = null,
    string? ResponseRoot = null,
    int MaxItems = 20,
    int MaxAttempts = 1);

/// <summary>O que um chamador (agente, chatbot, assistente) enxerga de uma operação: o necessário para escolher e preencher, sem detalhe de transporte nem de conexão.</summary>
public sealed record CapabilityDescriptor(
    string Key,
    int Version,
    string Name,
    string? Description,
    CapabilityEffect Effect,
    bool RequiresConfirmation,
    IReadOnlyList<CapabilityParameterInfo> Parameters,
    IReadOnlyList<string> OutputFields);

public sealed record CapabilityParameterInfo(string Name, CapabilityParameterType Type, bool Required, string? Description, IReadOnlyList<string>? EnumValues);

/// <summary>
/// O pedido de execução. <see cref="Key"/> e <see cref="Parameters"/> são dados NÃO confiáveis (podem vir de um modelo de IA): o motor os valida contra o catálogo do tenant. O tenant e o usuário vêm do
/// <c>RequestContext</c>, nunca daqui. <see cref="IdempotencyKey"/> é obrigatória para escrita e escolhida por quem chama (a mesma intenção, a mesma chave).
/// </summary>
/// <param name="ServiceSessionId">A correlação com o atendimento, opaca para o motor (trilha e histórico).</param>
public sealed record CapabilityInvocation(
    string Key,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    CapabilityCaller Caller,
    string? IdempotencyKey = null,
    Guid? ServiceSessionId = null);

public enum CapabilityExecutionStatus
{
    /// <summary>Executou e o sistema externo confirmou.</summary>
    Succeeded = 1,

    /// <summary>Não executou, ou o sistema externo recusou de forma definitiva.</summary>
    Failed = 2,

    /// <summary>O motor recusou antes de chamar (validação, permissão de negócio, limite, desabilitada).</summary>
    Rejected = 3,

    /// <summary>Escrita que espera a confirmação de uma pessoa autorizada. Nada foi enviado.</summary>
    AwaitingConfirmation = 4,

    /// <summary>A chamada saiu e o resultado é AMBÍGUO. Nunca é reenviada automaticamente; conferir no sistema de origem.</summary>
    Unknown = 5,

    /// <summary>Outra chamada com a mesma chave de idempotência ainda está em curso.</summary>
    InProgress = 6,

    Cancelled = 7,
}

/// <summary>
/// Um erro estruturado. <see cref="Code"/> é curto e fixo (por exemplo <c>validation-failed</c>, <c>not-found</c>, <c>disabled</c>, <c>caller-not-allowed</c>, <c>rate-limited</c>, <c>timeout</c>, <c>unavailable</c>,
/// <c>upstream-rejected</c>, <c>upstream-auth</c>, <c>redirect-blocked</c>, <c>blocked-destination</c>, <c>response-too-large</c>, <c>response-invalid</c>, <c>secret-unavailable</c>, <c>idempotency-required</c>,
/// <c>idempotency-conflict</c>, <c>outcome-unknown</c>, <c>confirmation-expired</c>). <see cref="Message"/> é uma frase fixa em português: NUNCA traz texto do sistema externo nem valor de parâmetro.
/// </summary>
public sealed record CapabilityError(string Code, string Message, bool Retryable = false, int? UpstreamStatus = null, IReadOnlyList<CapabilityParameterProblem>? Problems = null);

/// <summary>Um problema de validação: o NOME do parâmetro e a regra violada (<c>required</c>, <c>unknown</c>, <c>type</c>, <c>too-long</c>, <c>pattern</c>, <c>range</c>, <c>enum</c>, <c>format</c>, <c>unsafe-chars</c>). Nunca o valor.</summary>
public sealed record CapabilityParameterProblem(string Parameter, string Rule);

/// <param name="Output">Só os campos mapeados, tipados e sanitizados. Conteúdo de sistema externo: <see cref="UntrustedContent"/> é sempre verdadeiro e quem usar o texto num prompt o trata como DADO.</param>
/// <param name="Replayed">Resultado devolvido do registro da mesma chave de idempotência, sem nova chamada.</param>
public sealed record CapabilityResult(
    Guid ExecutionId,
    CapabilityExecutionStatus Status,
    JsonElement? Output = null,
    CapabilityError? Error = null,
    bool Replayed = false,
    DateTimeOffset? ConfirmationExpiresAt = null)
{
    public bool UntrustedContent => true;

    public bool Succeeded => Status == CapabilityExecutionStatus.Succeeded;
}
