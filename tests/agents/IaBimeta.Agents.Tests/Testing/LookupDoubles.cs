using System.Text.Json;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.Capabilities.Contracts;

namespace IaBimeta.Agents.Testing;

/// <summary>O catálogo do Motor de Integração dos testes do Agents: devolve o que o teste semeou (o motor real é que filtra por escopo; aqui se prova que o Agents confere de novo) e registra os pedidos.</summary>
internal sealed class FakeCatalog : ICapabilityCatalog
{
    public List<CapabilityDescriptor> Descriptors { get; } = [];

    public List<(CapabilityCaller Caller, Guid? Scope)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(CapabilityCaller caller, CancellationToken cancellationToken = default, Guid? scopeOrganizationId = null)
    {
        lock (Calls)
        {
            Calls.Add((caller, scopeOrganizationId));
        }

        return Failure is null ? Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(Descriptors.ToList()) : throw Failure;
    }

    public async Task<CapabilityDescriptor?> GetAsync(string key, CapabilityCaller caller, CancellationToken cancellationToken = default, Guid? scopeOrganizationId = null) =>
        (await ListAsync(caller, cancellationToken, scopeOrganizationId)).FirstOrDefault(d => d.Key == key);

    public static CapabilityDescriptor Operation(string key, CapabilityEffect effect = CapabilityEffect.Read, CapabilityDataClass dataClass = CapabilityDataClass.Public) => new(
        key,
        1,
        "Consulta " + key,
        "Descrição de " + key,
        effect,
        effect == CapabilityEffect.Write,
        [new CapabilityParameterInfo("exame", CapabilityParameterType.String, true, "Nome do exame", null)],
        ["preco", "prazo"],
        dataClass);
}

/// <summary>O executor do Motor de Integração dos testes: responde o que o teste roteirizar, registra cada invocação e pode demorar (para provar o orçamento).</summary>
internal sealed class FakeExecutor : ICapabilityExecutor
{
    public List<CapabilityInvocation> Invocations { get; } = [];

    public Func<CapabilityInvocation, CapabilityResult> Behavior { get; set; } = _ => Ok("""{"preco":42.5,"prazo":"2 dias"}""");

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public Action? DuringCall { get; set; }

    public static CapabilityResult Ok(string json) => new(Guid.NewGuid(), CapabilityExecutionStatus.Succeeded, JsonDocument.Parse(json).RootElement.Clone());

    public static CapabilityResult Rejected(string code) => new(Guid.NewGuid(), CapabilityExecutionStatus.Rejected, Error: new CapabilityError(code, "mensagem fixa"));

    public static CapabilityResult Failed(string code) => new(Guid.NewGuid(), CapabilityExecutionStatus.Failed, Error: new CapabilityError(code, "mensagem fixa", true));

    public async Task<CapabilityResult> ExecuteAsync(CapabilityInvocation invocation, CancellationToken cancellationToken = default)
    {
        lock (Invocations)
        {
            Invocations.Add(invocation);
        }

        DuringCall?.Invoke();
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Behavior(invocation);
    }

    public Task<CapabilityResult> ConfirmAsync(Guid executionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("O agente nunca confirma.");

    public Task<CapabilityResult> CancelAsync(Guid executionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("O agente nunca cancela.");
}

/// <summary>As saídas do modelo no esquema COM `lookup` (cinco campos).</summary>
internal static class LookupOutputs
{
    public static AiGenerationResult Lookup(string key, string parametersJson) =>
        ScriptedGateway.Output(JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "lookup", ["reply_text"] = "", ["handoff_reason"] = "none", ["operation_key"] = key, ["parameters_json"] = parametersJson }));

    public static AiGenerationResult Reply(string text) =>
        ScriptedGateway.Output(JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "reply", ["reply_text"] = text, ["handoff_reason"] = "none", ["operation_key"] = "none", ["parameters_json"] = "{}" }));
}
