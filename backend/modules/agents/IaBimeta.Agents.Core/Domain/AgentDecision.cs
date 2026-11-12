namespace IaBimeta.Agents.Domain;

/// <summary>As ações que o modelo pode SUGERIR (ADR 0017, seção C.5). A lista é fechada: qualquer outra é saída inválida. O modelo não executa nada: o Agents valida e executa pelos contratos.</summary>
public enum AgentAction
{
    /// <summary>Responder ao cliente com o texto sugerido.</summary>
    Reply = 1,

    /// <summary>Transferir para um atendente humano, por um motivo da lista fechada.</summary>
    Handoff = 2,

    /// <summary>Pedir UMA consulta de leitura ao Motor de Integração (ADR 0036). Só existe no esquema com `lookup` (função ligada e operações elegíveis); a chave precisa estar no conjunto oferecido no turno.</summary>
    Lookup = 3,
}

/// <summary>A decisão já validada (nunca a saída bruta do modelo): <see cref="ReplyText"/> para <see cref="AgentAction.Reply"/>, <see cref="HandoffReason"/> para <see cref="AgentAction.Handoff"/>.</summary>
public sealed record AgentDecision(
    AgentAction Action,
    string? ReplyText,
    string? HandoffReason,
    string? LookupKey = null,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? LookupParameters = null);

/// <summary>
/// Os motivos de handoff que o modelo pode escolher (lista fechada) e os códigos que o Agents usa no pedido ao ContactCenter. O ContactCenter aceita só <c>^[a-z0-9][a-z0-9_.-]*$</c> de até
/// 100 caracteres.
/// </summary>
public static class HandoffReasons
{
    public const string None = "none";
    public const string Price = "price";
    public const string Schedule = "schedule";
    public const string Coverage = "coverage";
    public const string ClinicalOrResult = "clinical_or_result";
    public const string Complaint = "complaint";
    public const string CustomerRequest = "customer_request";
    public const string InsufficientInformation = "insufficient_information";
    public const string Other = "other";

    /// <summary>Os motivos que acompanham uma ação de handoff (todos menos <see cref="None"/>).</summary>
    public static IReadOnlyList<string> Selectable { get; } = new[]
    {
        Price, Schedule, Coverage, ClinicalOrResult, Complaint, CustomerRequest, InsufficientInformation, Other,
    };

    /// <summary>O código de motivo da decisão da IA no pedido de handoff.</summary>
    public static string ForAiDecision(string reason) => $"ai-decision.{reason}";

    /// <summary>MED-4: o conteúdo (mídia) não pôde ser interpretado, e a política do tipo manda para o atendente.</summary>
    public const string ContentUnavailable = "content-unavailable";

    /// <summary>PRO-5: o turno morreu por falha permanente do modelo ou do tenant, e a política é transferir.</summary>
    public const string AiProcessingFailed = "ai-processing-failed";

    /// <summary>O tenant não tem definição de agente publicada: a IA não responde sem instruções aprovadas.</summary>
    public const string AiNotConfigured = "ai-not-configured";

    /// <summary>ADR 0036: a consulta ao sistema da empresa falhou (indisponível, tempo, limite, recusa ou resposta inválida): a IA não improvisa sem o dado e a política é transferir.</summary>
    public const string IntegrationUnavailable = "integration_unavailable";
}
