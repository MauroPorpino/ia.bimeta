namespace IaBimeta.Workflows.Core.Domain;

/// <summary>Dados inválidos (400). As mensagens são fixas e nunca ecoam o conteúdo enviado.</summary>
public sealed class WorkflowsValidationException : Exception
{
    public WorkflowsValidationException(IReadOnlyList<string> problems)
        : base("Dados inválidos.")
    {
        Problems = problems;
    }

    public WorkflowsValidationException(string problem)
        : this([problem])
    {
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Conflito (409): <c>changed</c>, <c>key-in-use</c>, <c>limit-reached</c>, <c>not-draft</c>, <c>not-published</c>.</summary>
public sealed class WorkflowsConflictException : Exception
{
    public WorkflowsConflictException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Limites e chaves do módulo (ADR 0037). Os padrões são conservadores; a função de runtime nasce DESLIGADA.</summary>
public sealed class WorkflowsOptions
{
    /// <summary>Liga o runtime (instâncias, roteador e Worker). A administração (rascunhos, validação, simulação) funciona mesmo desligada. Padrão: desligado.</summary>
    public bool Enabled { get; set; }

    public int MaxKeysPerTenant { get; set; } = 100;

    public int MaxVersionsPerKey { get; set; } = 200;

    public int MaxSimulationInputs { get; set; } = 100;

    public int MaxSimulationStubs { get; set; } = 100;

    public int SimulationsPerMinutePerTenant { get; set; } = 60;

    public FlowLimits Limits { get; set; } = new();

    public void Validate()
    {
        if (MaxKeysPerTenant is < 1 or > 10_000 || MaxVersionsPerKey is < 1 or > 10_000 || MaxSimulationInputs is < 1 or > 1000 || MaxSimulationStubs is < 1 or > 1000 || SimulationsPerMinutePerTenant is < 1 or > 10_000)
        {
            throw new InvalidOperationException("Workflows:Options com limite fora da faixa.");
        }

        if (Limits.MaxNodes is < 1 or > 2000 || Limits.MaxEdges is < 1 or > 6000 || Limits.MaxStepsPerRun is < 1 or > 500 || Limits.MaxDocumentBytes is < 1024 or > 4_194_304 || Limits.MaxTextLength is < 1 or > 8192)
        {
            throw new InvalidOperationException("Workflows:Options:Limits fora da faixa.");
        }
    }
}
