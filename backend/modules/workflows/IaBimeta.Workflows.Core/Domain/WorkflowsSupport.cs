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

    public int MaxActivationsPerTenant { get; set; } = 100;

    public int MaxSimulationInputs { get; set; } = 100;

    public int MaxSimulationStubs { get; set; } = 100;

    public int SimulationsPerMinutePerTenant { get; set; } = 60;

    public FlowLimits Limits { get; set; } = new();

    // ---- Runtime (ADR 0038)

    /// <summary>Quantas passadas (transições e efeitos) o executor faz por instância em cada reivindicação, antes de soltar o lease e voltar depois.</summary>
    public int MaxRunIterations { get; set; } = 30;

    /// <summary>Quantas vezes, no máximo, uma mensagem de fluxo recusada por entrada defasada é refeita (reabre o turno, mesma chave de idempotência). Esgotado, a instância segue a política <c>OnFailure</c>.</summary>
    public int StaleRetries { get; set; } = 3;

    /// <summary>O orçamento de uma consulta de integração (o motor tem os próprios limites; este é o do fluxo). De 1 a 30 s.</summary>
    public TimeSpan CapabilityBudget { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>O teto de instâncias ativas por tenant: acima dele a sessão nova fica com o agente (nenhuma instância é criada).</summary>
    public int MaxActiveInstancesPerTenant { get; set; } = 1000;

    /// <summary>A duração do lease de uma instância reivindicada: se o executor morrer, outra reivindica depois dele.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public int ExecutorBatchSize { get; set; } = 20;

    public int ExecutorParallelism { get; set; } = 4;

    public TimeSpan ExecutorInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>O teto de instâncias reivindicadas por tenant em cada ciclo (um tenant barulhento não monopoliza o executor).</summary>
    public int ExecutorPerTenantBatch { get; set; } = 5;

    public void Validate()
    {
        if (MaxKeysPerTenant is < 1 or > 10_000 || MaxVersionsPerKey is < 1 or > 10_000 || MaxActivationsPerTenant is < 1 or > 10_000 || MaxSimulationInputs is < 1 or > 1000 || MaxSimulationStubs is < 1 or > 1000 || SimulationsPerMinutePerTenant is < 1 or > 10_000)
        {
            throw new InvalidOperationException("Workflows:Options com limite fora da faixa.");
        }

        if (MaxRunIterations is < 1 or > 500 || StaleRetries is < 0 or > 10 || CapabilityBudget < TimeSpan.FromSeconds(1) || CapabilityBudget > TimeSpan.FromSeconds(30) || MaxActiveInstancesPerTenant is < 1 or > 1_000_000
            || LeaseDuration < TimeSpan.FromSeconds(10) || ExecutorBatchSize is < 1 or > 1000 || ExecutorParallelism is < 1 or > 64 || ExecutorInterval < TimeSpan.FromMilliseconds(100) || ExecutorPerTenantBatch is < 1 or > 1000)
        {
            throw new InvalidOperationException("Workflows:Options (runtime) com limite fora da faixa.");
        }

        if (Limits.MaxNodes is < 1 or > 2000 || Limits.MaxEdges is < 1 or > 6000 || Limits.MaxStepsPerRun is < 1 or > 500 || Limits.MaxDocumentBytes is < 1024 or > 4_194_304 || Limits.MaxTextLength is < 1 or > 8192)
        {
            throw new InvalidOperationException("Workflows:Options:Limits fora da faixa.");
        }
    }
}
