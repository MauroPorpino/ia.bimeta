namespace IaBimeta.Workflows.Core.Domain;

// Estado, entradas e efeitos do interpretador (ADR 0037 §3). Tudo é dado: o interpretador não faz I/O.

public enum FlowStatus
{
    /// <summary>Instância criada, ainda sem nenhum passo (só o roteador a cria; o executor a inicia com a primeira mensagem do cliente).</summary>
    Created = 1,

    /// <summary>Espera a próxima mensagem do cliente (ou o prazo).</summary>
    WaitingInput = 2,

    /// <summary>Espera o resultado de um efeito assíncrono (operação ou conhecimento).</summary>
    AwaitingEffect = 3,

    /// <summary>Reservado: IA com retorno ao fluxo (ADR 0037 §2). Não é produzido na v1.</summary>
    AwaitingAgent = 4,

    Completed = 5,

    /// <summary>Transferida a um atendente.</summary>
    HandedOff = 6,

    /// <summary>Entregue ao Agents (nó <c>ai</c>, v1).</summary>
    Delegated = 7,

    Failed = 8,

    Cancelled = 9,
}

public static class FlowStatusExtensions
{
    public static bool IsTerminal(this FlowStatus status) => status is FlowStatus.Completed or FlowStatus.HandedOff or FlowStatus.Delegated or FlowStatus.Failed or FlowStatus.Cancelled;
}

/// <param name="NodeId">O nó em que a instância está (espera ou terminou).</param>
/// <param name="Variables">Valores das variáveis (texto). A classe de cada uma vem do grafo.</param>
/// <param name="Attempts">Tentativas da pergunta atual.</param>
/// <param name="Step">Contador monotônico de transições; entra na chave de cada efeito.</param>
/// <param name="WaitUntil">Prazo da pergunta atual (o executor dispara <see cref="TimeoutInput"/>).</param>
/// <param name="PendingEffectKey">A chave do efeito assíncrono esperado.</param>
public sealed record FlowState(
    FlowStatus Status,
    string? NodeId,
    IReadOnlyDictionary<string, string> Variables,
    int Attempts,
    long Step,
    DateTimeOffset? WaitUntil = null,
    string? PendingEffectKey = null,
    string? FailureCode = null);

public abstract record FlowInput;

/// <param name="ChoiceId">Id de uma escolha vinda de botão ou lista do canal.</param>
/// <param name="MediaKind">Tipo de mídia recebida (<c>image</c>, <c>audio</c>...). Pergunta de texto não aceita mídia.</param>
public sealed record InboundInput(string? Text = null, string? ChoiceId = null, string? MediaKind = null) : FlowInput;

public sealed record TimeoutInput : FlowInput;

public sealed record CapabilityResultInput(string EffectKey, bool Success, IReadOnlyDictionary<string, string>? Fields = null, string? ErrorCode = null) : FlowInput;

public sealed record KnowledgeResultInput(string EffectKey, bool Found, string? Answer = null) : FlowInput;

/// <summary>Um efeito pedido pelo interpretador. <see cref="Key"/> é <c>passo:índice</c>; o runtime a prefixa com a instância para a chave de idempotência (ADR 0037 §4).</summary>
public abstract record FlowEffect(string Key, string NodeId);

/// <param name="ExpectsFreeText">O passo termina numa pergunta cuja resposta é TEXTO LIVRE (qualquer tipo de resposta que não seja escolha): marcador explícito do nó, lido pelo Conversations na decisão de saída (ADR 0041, regra 3).</param>
public sealed record SendMessageEffect(string Key, string NodeId, string Text, IReadOnlyList<FlowChoice> Choices, bool ExpectsFreeText = false) : FlowEffect(Key, NodeId);

public sealed record CallCapabilityEffect(string Key, string NodeId, string OperationKey, IReadOnlyDictionary<string, string> Parameters) : FlowEffect(Key, NodeId);

public sealed record LookupKnowledgeEffect(string Key, string NodeId, string Query, string? SourceKey) : FlowEffect(Key, NodeId);

public sealed record RequestHandoffEffect(string Key, string NodeId, string ReasonCode, IReadOnlyDictionary<string, string> Summary) : FlowEffect(Key, NodeId);

public sealed record DelegateToAgentEffect(string Key, string NodeId) : FlowEffect(Key, NodeId);

public sealed record FinishEffect(string Key, string NodeId) : FlowEffect(Key, NodeId);

/// <param name="Ignored">A entrada não se aplicava ao estado (duplicada, atrasada ou de outro efeito): nada mudou.</param>
/// <param name="Visited">Os nós percorridos nesta transição, em ordem (para destacar o caminho no editor e no histórico).</param>
public sealed record StepResult(FlowState State, IReadOnlyList<FlowEffect> Effects, IReadOnlyList<string> Visited, bool Ignored = false);
