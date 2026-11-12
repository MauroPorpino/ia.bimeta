using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;

namespace IaBimeta.Workflows.Core.Domain;

/// <summary>O que fazer quando o fluxo falha (ADR 0038 §2): transferir a um atendente ou devolver a conversa ao agente de IA.</summary>
public enum OnFailurePolicy
{
    Handoff = 1,
    Agent = 2,
}

/// <summary>
/// Quando um fluxo publicado entra em uso (ADR 0038 §2): por organização (obrigatória), unidade e canal (opcionais; o mais específico vence; empate por prioridade), com porcentagem DETERMINÍSTICA opcional
/// (o mesmo cliente cai sempre do mesmo lado). Nasce DESLIGADA.
/// </summary>
public sealed class FlowActivation : ITenantOwned
{
    private FlowActivation()
    {
        WorkflowKey = string.Empty;
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid? BusinessUnitId { get; private set; }

    /// <summary>O número (instância de canal) em que a ativação vale; nulo = qualquer canal.</summary>
    public Guid? ChannelInstanceId { get; private set; }

    public string WorkflowKey { get; private set; }

    public bool Enabled { get; private set; }

    public int Priority { get; private set; }

    public int RolloutPercent { get; private set; } = 100;

    public OnFailurePolicy OnFailure { get; private set; } = OnFailurePolicy.Handoff;

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static FlowActivation Create(
        TenantId tenantId, Guid organizationId, Guid? businessUnitId, Guid? channelInstanceId, string workflowKey, int priority, int rolloutPercent, OnFailurePolicy onFailure, Guid? createdBy, DateTimeOffset now)
    {
        var a = new FlowActivation { TenantId = tenantId, Id = Guid.NewGuid(), OrganizationId = organizationId, CreatedBy = createdBy, CreatedAt = now, Enabled = false };
        a.Apply(businessUnitId, channelInstanceId, workflowKey, priority, rolloutPercent, onFailure, now);
        a.Revision = 1;
        return a;
    }

    public void Update(Guid? businessUnitId, Guid? channelInstanceId, string workflowKey, int priority, int rolloutPercent, OnFailurePolicy onFailure, DateTimeOffset now)
    {
        Apply(businessUnitId, channelInstanceId, workflowKey, priority, rolloutPercent, onFailure, now);
        Revision++;
    }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAt = now;
        Revision++;
    }

    private void Apply(Guid? businessUnitId, Guid? channelInstanceId, string workflowKey, int priority, int rolloutPercent, OnFailurePolicy onFailure, DateTimeOffset now)
    {
        BusinessUnitId = businessUnitId;
        ChannelInstanceId = channelInstanceId;
        WorkflowKey = workflowKey;
        Priority = priority;
        RolloutPercent = rolloutPercent;
        OnFailure = onFailure;
        UpdatedAt = now;
    }

    /// <summary>Quantos critérios opcionais a ativação especifica: o mais específico vence.</summary>
    public int Specificity => (BusinessUnitId is null ? 0 : 1) + (ChannelInstanceId is null ? 0 : 1);

    public bool Matches(Guid organizationId, Guid? businessUnitId, Guid channelInstanceId) =>
        OrganizationId == organizationId
        && (BusinessUnitId is null || BusinessUnitId == businessUnitId)
        && (ChannelInstanceId is null || ChannelInstanceId == channelInstanceId);

    /// <summary>A porcentagem determinística: o mesmo contato e a mesma ativação dão sempre o mesmo lado, em qualquer processo.</summary>
    public bool InRollout(Guid contactKey)
    {
        if (RolloutPercent >= 100)
        {
            return true;
        }

        if (RolloutPercent <= 0)
        {
            return false;
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{Id:N}|{contactKey:N}"));
        return (BitConverter.ToUInt32(bytes, 0) % 100) < (uint)RolloutPercent;
    }
}

/// <summary>A decisão ÚNICA e durável de quem responde à sessão (chave primária por tenant e sessão: a disputa é decidida pelo banco).</summary>
public sealed class RoutingDecision : ITenantOwned
{
    private RoutingDecision()
    {
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid SessionId { get; private set; }

    public AutomationOwner Owner { get; private set; }

    public Guid? ActivationId { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static RoutingDecision Create(TenantId tenantId, Guid sessionId, AutomationOwner owner, Guid? activationId, DateTimeOffset now) =>
        new() { TenantId = tenantId, SessionId = sessionId, Owner = owner, ActivationId = activationId, DecidedAt = now, UpdatedAt = now };

    /// <summary>O fluxo terminou (fim, transferência, entrega, falha, cancelamento): a sessão passa ao agente, de forma definitiva. Nunca o inverso.</summary>
    public void ReleaseToAgent(DateTimeOffset now)
    {
        if (Owner == AutomationOwner.Agent)
        {
            return;
        }

        Owner = AutomationOwner.Agent;
        UpdatedAt = now;
        Revision++;
    }
}

public enum EffectKind
{
    SendMessage = 1,
    CallCapability = 2,
    LookupKnowledge = 3,
    RequestHandoff = 4,
    DelegateToAgent = 5,
    Finish = 6,
}

public enum EffectState
{
    Pending = 1,

    /// <summary>Começou a ser realizado. Se o processo cair aqui, uma operação externa vira <c>Unknown</c> (nunca é reenviada); mensagem e transferência são idempotentes e repetem.</summary>
    InFlight = 2,

    Done = 3,
    Failed = 4,

    /// <summary>Resultado ambíguo de uma operação externa: segue a porta de falha e NUNCA é reenviado (ARQ-21).</summary>
    Unknown = 5,

    /// <summary>Não será realizado (a conversa passou a um atendente, o fluxo foi cancelado): nada foi enviado.</summary>
    Superseded = 6,
}

/// <summary>Um efeito de uma transição, gravado NA MESMA transação do estado (ADR 0037 §4). O conteúdo (pode ter dado pessoal) é cifrado.</summary>
public sealed class FlowEffectRecord : ITenantOwned
{
    private FlowEffectRecord()
    {
        Key = string.Empty;
        PayloadCipher = string.Empty;
        PayloadKeyId = string.Empty;
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid InstanceId { get; private set; }

    /// <summary>O identificador determinístico dentro da instância: <c>passo:índice</c>. Com a instância forma a chave de idempotência de cada realização.</summary>
    public string Key { get; private set; }

    public long Step { get; private set; }

    public int Index { get; private set; }

    public EffectKind Kind { get; private set; }

    public string NodeId { get; private set; } = string.Empty;

    public string PayloadCipher { get; private set; }

    public string PayloadKeyId { get; private set; }

    public EffectState State { get; private set; }

    public string? ResultCode { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static FlowEffectRecord Create(TenantId tenantId, Guid effectId, Guid instanceId, string key, long step, int index, EffectKind kind, string nodeId, string payloadCipher, string payloadKeyId, DateTimeOffset now) =>
        new() { TenantId = tenantId, Id = effectId, InstanceId = instanceId, Key = key, Step = step, Index = index, Kind = kind, NodeId = nodeId, PayloadCipher = payloadCipher, PayloadKeyId = payloadKeyId, State = EffectState.Pending, CreatedAt = now };

    public void Start()
    {
        State = EffectState.InFlight;
        Attempts++;
    }

    public void Complete(EffectState state, string? resultCode, DateTimeOffset now)
    {
        State = state;
        ResultCode = resultCode;
        CompletedAt = now;
    }

    public bool IsOpen => State is EffectState.Pending or EffectState.InFlight;
}

/// <summary>Uma passagem por um nó (o histórico de execução): só identificadores e o desfecho, nunca valores nem texto.</summary>
public sealed class FlowStepRecord : ITenantOwned
{
    private FlowStepRecord()
    {
        NodeId = string.Empty;
        Outcome = string.Empty;
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid InstanceId { get; private set; }

    public long Step { get; private set; }

    public string NodeId { get; private set; }

    /// <summary>O código curto do desfecho da transição (<c>waiting</c>, <c>completed</c>, <c>handed_off</c>, <c>delegated</c>, <c>failed:step-limit</c>...).</summary>
    public string Outcome { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static FlowStepRecord Create(TenantId tenantId, Guid instanceId, long step, string nodeId, string outcome, DateTimeOffset at) =>
        new() { TenantId = tenantId, Id = Guid.NewGuid(), InstanceId = instanceId, Step = step, NodeId = nodeId, Outcome = outcome, At = at };
}

/// <summary>
/// Uma conversa em andamento num fluxo (ADR 0037 §4). Fixada na VERSÃO em que começou até o encerramento. Uma ativa por sessão (índice único parcial). As variáveis ficam cifradas (AES-GCM, vinculadas ao
/// tenant e à instância). O lease com token impede que dois executores rodem a mesma instância; a revisão é a concorrência otimista.
/// </summary>
public sealed class FlowInstance : ITenantOwned
{
    private FlowInstance()
    {
        WorkflowKey = string.Empty;
        VariablesCipher = string.Empty;
        VariablesKeyId = string.Empty;
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid WorkflowVersionId { get; private set; }

    public string WorkflowKey { get; private set; }

    public int VersionNumber { get; private set; }

    public Guid? ActivationId { get; private set; }

    public OnFailurePolicy OnFailure { get; private set; }

    public FlowStatus Status { get; private set; }

    public string? NodeId { get; private set; }

    public int Attempts { get; private set; }

    public long Step { get; private set; }

    public DateTimeOffset? WaitUntil { get; private set; }

    public string? PendingEffectKey { get; private set; }

    public string? FailureCode { get; private set; }

    public string VariablesCipher { get; private set; }

    public string VariablesKeyId { get; private set; }

    /// <summary>A maior <c>MessageSeq</c> da sessão já vista pela instância: as mensagens do cliente depois dela são a próxima entrada.</summary>
    public long SeenMessageSeq { get; private set; }

    /// <summary>Quando o executor deve olhar a instância de novo (agora, ou no prazo da pergunta); nulo = só quando algo acontecer.</summary>
    public DateTimeOffset? NextRunAt { get; private set; }

    /// <summary>Pedido de cancelamento (atendente assumiu, sessão encerrada): o executor o aplica com segurança.</summary>
    public string? CancelReason { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public long LeaseToken { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public bool IsTerminal => Status.IsTerminal();

    /// <summary>Fixa o identificador (a cifra das variáveis iniciais precisa dele antes de a instância existir). Só a criação o usa.</summary>
    internal void SetId(Guid id) => Id = id;

    public static FlowInstance Create(
        TenantId tenantId, Guid sessionId, Guid organizationId, Guid workflowVersionId, string workflowKey, int versionNumber, Guid? activationId, OnFailurePolicy onFailure, string variablesCipher, string variablesKeyId, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            OrganizationId = organizationId,
            WorkflowVersionId = workflowVersionId,
            WorkflowKey = workflowKey,
            VersionNumber = versionNumber,
            ActivationId = activationId,
            OnFailure = onFailure,
            Status = FlowStatus.Created,
            VariablesCipher = variablesCipher,
            VariablesKeyId = variablesKeyId,
            NextRunAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public FlowState ToState(IReadOnlyDictionary<string, string> variables) =>
        new(Status, NodeId, variables, Attempts, Step, WaitUntil, PendingEffectKey, FailureCode);

    /// <summary>Aplica o estado do interpretador. O próximo olhar do executor: agora se há efeitos a realizar; senão o prazo da pergunta (ou nunca).</summary>
    public void Apply(FlowState state, string variablesCipher, string variablesKeyId, long seenMessageSeq, bool hasPendingEffects, DateTimeOffset now)
    {
        Status = state.Status;
        NodeId = state.NodeId;
        Attempts = state.Attempts;
        Step = state.Step;
        WaitUntil = state.WaitUntil;
        PendingEffectKey = state.PendingEffectKey;
        FailureCode = state.FailureCode;
        VariablesCipher = variablesCipher;
        VariablesKeyId = variablesKeyId;
        SeenMessageSeq = Math.Max(SeenMessageSeq, seenMessageSeq);
        // Efeitos a realizar, ou um fim de vida a finalizar (devolver a conversa ao agente): olhar já. Senão o prazo da pergunta (ou nunca, até chegar mensagem).
        NextRunAt = hasPendingEffects || IsTerminal ? now : state.WaitUntil;
        if (IsTerminal && EndedAt is null)
        {
            EndedAt = now;
        }

        UpdatedAt = now;
        Revision++;
    }

    public void Wake(DateTimeOffset now)
    {
        if (!IsTerminal)
        {
            NextRunAt = now;
            UpdatedAt = now;
            Revision++;
        }
    }

    public void RequestCancel(string reason, DateTimeOffset now)
    {
        if (!IsTerminal && CancelReason is null)
        {
            CancelReason = reason;
            NextRunAt = now;
            UpdatedAt = now;
            Revision++;
        }
    }

    public void MarkCancelled(string reason, DateTimeOffset now)
    {
        Status = FlowStatus.Cancelled;
        FailureCode = reason;
        WaitUntil = null;
        PendingEffectKey = null;
        NextRunAt = null;
        EndedAt ??= now;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Falha definitiva da instância (por exemplo, a versão sumiu ou a mensagem foi recusada em definitivo): terminal, com o código curto do motivo.</summary>
    public void MarkFailed(string code, DateTimeOffset now)
    {
        Status = FlowStatus.Failed;
        FailureCode = code;
        WaitUntil = null;
        PendingEffectKey = null;
        NextRunAt = now;
        EndedAt ??= now;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Solta o lease e marca o próximo olhar (o prazo da pergunta, ou nenhum: só quando chegar mensagem ou outro motivo acordar a instância).</summary>
    public void ParkUntil(DateTimeOffset? until, DateTimeOffset now)
    {
        NextRunAt = until;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Esvazia as variáveis (já cifradas com o mapa vazio): depois do encerramento nada do cliente fica na instância (minimização de dado pessoal).</summary>
    public void WipeVariables(string variablesCipher, string variablesKeyId, DateTimeOffset now)
    {
        VariablesCipher = variablesCipher;
        VariablesKeyId = variablesKeyId;
        UpdatedAt = now;
        Revision++;
    }

    public void Claim(string owner, DateTimeOffset until, DateTimeOffset now)
    {
        LeaseOwner = owner;
        LeaseExpiresAt = until;
        LeaseToken++;
        UpdatedAt = now;
        Revision++;
    }

    public void ReleaseLease(DateTimeOffset now)
    {
        LeaseOwner = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
        Revision++;
    }

    /// <summary>Adia o próximo olhar (uma falha transitória): o lease é solto e a instância volta a ser devida depois do atraso.</summary>
    public void Defer(DateTimeOffset until, DateTimeOffset now)
    {
        NextRunAt = until;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        UpdatedAt = now;
        Revision++;
    }
}
