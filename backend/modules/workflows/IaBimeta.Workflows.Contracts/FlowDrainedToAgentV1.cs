namespace IaBimeta.Workflows.Contracts;

/// <summary>A drenagem cancelou o fluxo. O Agents deve reavaliar a entrada ainda sem resposta desta sessão.</summary>
public sealed record FlowDrainedToAgentV1(Guid SessionId)
{
    public const string EventType = "workflows.flow-drained-to-agent";
}
