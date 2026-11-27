namespace IaBimeta.Workflows.Contracts;

/// <summary>Quem responde a uma sessão (ADR 0038, §1): o agente de IA (o padrão) ou um fluxo do chatbot visual. Nunca os dois ao mesmo tempo.</summary>
public enum AutomationOwner
{
    Agent = 1,
    Flow = 2,
}

/// <summary>
/// O roteador: a decisão ÚNICA e durável de quem responde a cada sessão (ADR 0038). O Agents consulta antes de abrir um turno e ignora a sessão que é de um fluxo. Com a função desligada
/// (<c>Workflows:Options:Enabled = false</c>, o padrão) as duas operações respondem <see cref="AutomationOwner.Agent"/> SEM consultar nem gravar nada: o caminho é idêntico ao anterior ao chatbot visual.
/// </summary>
public interface IAutomationRouter
{
    /// <summary>De quem é a sessão agora. Somente leitura: sessão sem decisão é do agente.</summary>
    Task<AutomationOwner> GetOwnerAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chamado a cada mensagem do cliente. Só a PRIMEIRA mensagem de uma sessão (<paramref name="inboundSeq"/> = 1) pode escolher um fluxo, por uma ativação que case com a organização, a unidade e o canal da
    /// sessão: uma sessão em andamento nunca muda de dono por uma ativação criada depois. A decisão é gravada uma vez (índice único): disputas devolvem a mesma resposta.
    /// Empresa suspensa, ou evento ANTERIOR à última reativação (<paramref name="eventOccurredAt"/>): nenhuma instância é criada nem decisão gravada (a sessão fica com o agente), para que um estímulo da suspensão, entregue
    /// com atraso, nunca dispare um fluxo depois da reativação (P1-03).
    /// </summary>
    Task<AutomationOwner> ResolveOnInboundAsync(Guid sessionId, long inboundSeq, CancellationToken cancellationToken = default, DateTimeOffset? eventOccurredAt = null);
}

/// <param name="OrganizationId">O escopo de organização da sessão: o conhecimento consultado é o desta organização.</param>
/// <param name="SourceKey">Uma fonte específica de conhecimento, se o nó a informou.</param>
public sealed record FlowKnowledgeQuery(Guid OrganizationId, string Text, string? SourceKey);

public sealed record FlowKnowledgeResult(bool Found, string? Answer);

/// <summary>
/// A porta do nó de conhecimento: reutiliza o conhecimento aprovado que o Agents já mantém (o FAQ aprovado do agente e o índice semântico); o fluxo NÃO tem outro mecanismo de conhecimento. Implementada pelo
/// Agents. Somente leitura, sem chamada de modelo de geração.
/// </summary>
public interface IFlowKnowledgeLookup
{
    Task<FlowKnowledgeResult> LookupAsync(FlowKnowledgeQuery query, CancellationToken cancellationToken = default);
}

public enum FlowHandoffOutcome
{
    /// <summary>(Só na verificação) O pedido seria aceito agora.</summary>
    Routable = 1,

    Completed = 2,

    /// <summary>O mesmo pedido já tinha sido aplicado (repetição idempotente).</summary>
    AlreadyApplied = 3,

    /// <summary>As regras de roteamento recusaram (nenhuma fila atende, pausa, horário) ou o atendimento já é de uma pessoa.</summary>
    Refused = 4,

    SessionEnded = 5,
}

/// <param name="RequestId">A chave de idempotência do pedido (derivada da instância e do nó, determinística).</param>
/// <param name="CheckOnly">Só pergunta se o pedido seria aceito, sem gravar nada.</param>
public sealed record FlowHandoffRequest(Guid SessionId, Guid RequestId, string ReasonCode, bool CheckOnly);

public sealed record FlowHandoffResult(FlowHandoffOutcome Outcome, string? Code);

/// <summary>
/// A porta da transferência a um atendente. O Workflows não referencia o ContactCenter: o adaptador do host a implementa sobre o <c>RequestHandoff</c> do ContactCenter (idempotente pelo
/// <see cref="FlowHandoffRequest.RequestId"/>).
/// </summary>
public interface IFlowHandoffPort
{
    Task<FlowHandoffResult> RequestAsync(FlowHandoffRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A porta de validação do ESCOPO de uma ativação (ADR 0038 §2): confere, no tenant do contexto, que a organização existe, que a unidade é dela e que o número (canal) é dela e, com unidade, a atende.
/// O Workflows não referencia o Channels; o host a implementa sobre o Tenancy e o Channels. Devolve nulo se o escopo é válido, ou um motivo curto e fixo (nunca o valor informado).
/// </summary>
public interface IActivationScopeValidator
{
    Task<string?> ValidateAsync(Guid organizationId, Guid? businessUnitId, Guid? channelInstanceId, CancellationToken cancellationToken = default);
}
