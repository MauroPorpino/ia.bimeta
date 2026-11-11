namespace IaBimeta.Agents.Domain;

/// <summary>
/// Os códigos de fim de turno (<c>ai_turns.end_reason</c>) que outros componentes leem: o relatório de operação conta por eles. São códigos curtos e estáveis, nunca conteúdo de mensagem; mudar um valor
/// quebra a contagem histórica.
/// </summary>
public static class AiTurnEndReasons
{
    /// <summary>A resposta do modelo foi aceita pelo Conversations.</summary>
    public const string Replied = "replied";

    /// <summary>A resposta direta do FAQ foi aceita pelo Conversations: o turno terminou SEM chamar o modelo (ADR 0029).</summary>
    public const string FaqHit = "faq-hit";

    /// <summary>Respondido direto pela busca semântica no conhecimento aprovado (ADR 0034), sem chamar o modelo.</summary>
    public const string SemanticHit = "semantic-hit";
}
