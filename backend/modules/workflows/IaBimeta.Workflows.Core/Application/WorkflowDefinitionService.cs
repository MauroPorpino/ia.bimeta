using System.Text.RegularExpressions;
using IaBimeta.Audit.Contracts;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <param name="GraphJson">O documento do grafo; só vem no detalhe (a lista não o traz).</param>
public sealed record WorkflowView(
    Guid Id,
    Guid OrganizationId,
    string Key,
    int Version,
    VersionStatus Status,
    string Name,
    string? Description,
    string GraphHash,
    int Revision,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? RetiredAt,
    string? GraphJson = null)
{
    internal static WorkflowView From(WorkflowVersion v, bool withGraph = false) =>
        new(v.Id, v.OrganizationId, v.Key, v.Version, v.Status, v.Name, v.Description, v.GraphHash, v.Revision, v.UpdatedAt, v.PublishedAt, v.RetiredAt, withGraph ? v.GraphJson : null);
}

/// <param name="GraphJson">O documento do editor, como texto JSON: lido de forma estrita pelo servidor.</param>
/// <param name="ExpectedRevision">Obrigatório para atualizar um rascunho existente (concorrência otimista).</param>
public sealed record SaveWorkflowRequest(Guid OrganizationId, string Key, string Name, string? Description, string GraphJson, int? ExpectedRevision);

/// <summary>O rascunho salvo e os problemas que ainda impedem a publicação (o editor os mostra por nó). Salvar não exige um grafo completo; publicar exige.</summary>
public sealed record SaveWorkflowResult(WorkflowView Workflow, IReadOnlyList<FlowProblem> Problems);

public sealed record SimulateRequest(Guid OrganizationId, string GraphJson, SimulationScenario Scenario);

/// <param name="Problems">Se o grafo não é válido, a simulação nem começa e os problemas voltam aqui.</param>
public sealed record SimulateResult(IReadOnlyList<FlowProblem> Problems, SimulationResult? Simulation);

public sealed record OperationOption(string Key, string Name, string? Description, bool Eligible, IReadOnlyList<CapabilityParameterInfo> Parameters, IReadOnlyList<string> OutputFields);

/// <summary>
/// Os fluxos do tenant, versionados (ADR 0037 §6): salvar o rascunho, validar, simular, publicar e aposentar. Publicar valida DE NOVO, congela a versão (imutável) e aposenta a anterior na MESMA transação.
/// Cada passo confere a permissão sobre a organização DO FLUXO e é auditado, sem conteúdo do grafo.
/// </summary>
public sealed partial class WorkflowDefinitionService
{
    private readonly WorkflowsAuthorizer _auth;
    private readonly IWorkflowStore _store;
    private readonly ICapabilityCatalog? _catalog;
    private readonly WorkflowsOptions _options;
    private readonly SimulationThrottle _throttle;
    private readonly TimeProvider _clock;

    internal WorkflowDefinitionService(WorkflowsAuthorizer auth, IWorkflowStore store, ICapabilityCatalog? catalog, WorkflowsOptions options, SimulationThrottle throttle, TimeProvider clock)
    {
        _auth = auth;
        _store = store;
        _catalog = catalog;
        _options = options;
        _throttle = throttle;
        _clock = clock;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_.-]{1,79}$")]
    internal static partial Regex KeyRegex();

    public async Task<IReadOnlyList<WorkflowView>> ListAsync(CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        var todas = await _store.ListLatestAsync(cancellationToken);
        return todas.Where(CanRead).OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => WorkflowView.From(v)).ToList();
    }

    public async Task<WorkflowView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        var versao = await _store.FindAsync(id, cancellationToken);
        return versao is not null && CanRead(versao) ? WorkflowView.From(versao, withGraph: true) : null;
    }

    public async Task<IReadOnlyList<WorkflowView>> VersionsAsync(string key, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        return (await _store.ListVersionsAsync(key, cancellationToken)).Where(CanRead).Select(v => WorkflowView.From(v)).ToList();
    }

    /// <summary>As operações de integração que o editor pode oferecer: só as elegíveis para fluxos (leitura, públicas, autorizadas) da organização do fluxo. Sem permissão de integração, a lista é vazia.</summary>
    public async Task<IReadOnlyList<OperationOption>> OperationsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        await _auth.RequireAsync(Permission.WorkflowRead, organizationId, "workflows.operations.list", null, cancellationToken);
        var lista = await LoadCatalogAsync(organizationId, cancellationToken);
        return lista.Select(d => new OperationOption(d.Key, d.Name, d.Description, SnapshotCatalogView.IsEligible(d), d.Parameters, d.OutputFields)).ToList();
    }

    public async Task<IReadOnlyList<FlowProblem>> ValidateAsync(Guid organizationId, string graphJson, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        await _auth.RequireAsync(Permission.WorkflowRead, organizationId, "workflows.validate", null, cancellationToken);
        var graph = Parse(graphJson);
        return await ValidateGraphAsync(graph, organizationId, cancellationToken);
    }

    /// <summary>Cria o rascunho (uma versão nova) ou atualiza o existente da chave. O fluxo pertence a uma organização, que não muda depois da primeira versão.</summary>
    public async Task<SaveWorkflowResult> SaveDraftAsync(SaveWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _auth.RequireAny(Permission.WorkflowManage);
        await _auth.RequireAsync(Permission.WorkflowManage, request.OrganizationId, "workflows.definition.save", null, cancellationToken);

        var problemas = new List<string>();
        if (!KeyRegex().IsMatch(request.Key ?? string.Empty))
        {
            problemas.Add("key: minúsculas, números e . _ -, começando por letra (2 a 80).");
        }

        var nome = (request.Name ?? string.Empty).Trim();
        if (nome.Length is < 1 or > 100 || nome.Any(char.IsControl))
        {
            problemas.Add("name: de 1 a 100 caracteres.");
        }

        var descricao = request.Description?.Trim();
        if (descricao is { Length: > 500 } || (descricao?.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) ?? false))
        {
            problemas.Add("description: até 500 caracteres.");
        }

        FlowGraph? graph = null;
        if (!FlowGraphSerializer.TryParse(request.GraphJson ?? string.Empty, _options.Limits, out graph, out var parseError))
        {
            problemas.Add($"graph: {parseError}.");
        }

        if (problemas.Count > 0)
        {
            throw new WorkflowsValidationException(problemas);
        }

        var agora = _clock.GetUtcNow();
        var hash = FlowGraphSerializer.Hash(graph!);
        var normalizado = FlowGraphSerializer.Serialize(graph!);
        var existente = await _store.FindDraftAsync(request.Key!, cancellationToken);
        WorkflowVersion rascunho;
        if (existente is not null)
        {
            if (existente.OrganizationId != request.OrganizationId)
            {
                throw new WorkflowsValidationException("organizationId: a organização de um fluxo não muda depois da primeira versão.");
            }

            if (request.ExpectedRevision is not { } esperada || esperada != existente.Revision)
            {
                throw new WorkflowsConflictException("changed", "O rascunho foi alterado por outra operação. Releia e tente de novo.");
            }

            existente.UpdateDraft(nome, descricao, normalizado, hash, agora);
            rascunho = existente;
        }
        else
        {
            var versoes = await _store.ListVersionsAsync(request.Key!, cancellationToken);
            if (versoes.Count > 0 && versoes[0].OrganizationId != request.OrganizationId)
            {
                throw new WorkflowsValidationException("organizationId: a organização de um fluxo não muda depois da primeira versão.");
            }

            if (versoes.Count == 0 && await _store.CountKeysAsync(cancellationToken) >= _options.MaxKeysPerTenant)
            {
                throw new WorkflowsConflictException("limit-reached", "O limite de fluxos desta empresa foi atingido.");
            }

            if (versoes.Count >= _options.MaxVersionsPerKey)
            {
                throw new WorkflowsConflictException("limit-reached", "O limite de versões deste fluxo foi atingido.");
            }

            var proxima = (versoes.Count == 0 ? 0 : versoes.Max(v => v.Version)) + 1;
            rascunho = WorkflowVersion.CreateDraft(_auth.Context.TenantId, request.OrganizationId, request.Key!, proxima, nome, descricao, normalizado, hash, _auth.Context.Principal?.UserId, agora);
            _store.Add(rascunho);
        }

        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("workflows.definition.save", rascunho.Id, AuditOutcome.Success, cancellationToken);
        return new SaveWorkflowResult(WorkflowView.From(rascunho, withGraph: true), await ValidateGraphAsync(graph!, request.OrganizationId, cancellationToken));
    }

    public async Task<WorkflowView?> PublishAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowPublish);
        var rascunho = await _store.FindAsync(id, cancellationToken);
        if (rascunho is null)
        {
            return null;
        }

        await _auth.RequireAsync(Permission.WorkflowPublish, rascunho.OrganizationId, "workflows.definition.publish", id, cancellationToken);

        // O grafo guardado não é confiável por ter sido aceito antes: lê, valida e confere o catálogo DE NOVO. A publicação só passa com ZERO problemas.
        var graph = Parse(rascunho.GraphJson);
        var problemas = await ValidateGraphAsync(graph, rascunho.OrganizationId, cancellationToken);
        if (problemas.Count > 0)
        {
            throw new WorkflowsValidationException(problemas.Select(p => $"{p.NodeId ?? "fluxo"}: {p.Code}").ToList());
        }

        var agora = _clock.GetUtcNow();
        var anterior = await _store.FindPublishedAsync(rascunho.Key, cancellationToken);

        // Uma transação só: a anterior sai de cena primeiro (índice único parcial) e a nova entra. O índice garante UMA publicada por chave mesmo sob disputa.
        await _store.InTransactionAsync(
            async () =>
            {
                if (anterior is not null)
                {
                    anterior.Retire(agora);
                    await _store.SaveAsync(cancellationToken);
                }

                rascunho.Publish(_auth.Context.Principal?.UserId, agora);
                await _store.SaveAsync(cancellationToken);
            },
            cancellationToken);
        await _auth.WriteAsync("workflows.definition.publish", id, AuditOutcome.Success, cancellationToken);
        return WorkflowView.From(rascunho);
    }

    public async Task<WorkflowView?> RetireAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowPublish);
        var versao = await _store.FindAsync(id, cancellationToken);
        if (versao is null)
        {
            return null;
        }

        await _auth.RequireAsync(Permission.WorkflowPublish, versao.OrganizationId, "workflows.definition.retire", id, cancellationToken);
        versao.Retire(_clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("workflows.definition.retire", id, AuditOutcome.Success, cancellationToken);
        return WorkflowView.From(versao);
    }

    /// <summary>Simula o fluxo com o MESMO interpretador e um executor simulado: nenhuma chamada a IA, sistema externo ou canal (ADR 0037 §9).</summary>
    public async Task<SimulateResult> SimulateAsync(SimulateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _auth.RequireAny(Permission.WorkflowTest);
        await _auth.RequireAsync(Permission.WorkflowTest, request.OrganizationId, "workflows.simulate", null, cancellationToken);

        if (!_throttle.TryAcquire(_auth.Context.TenantId, _options.SimulationsPerMinutePerTenant))
        {
            throw new WorkflowsConflictException("rate-limited", "Muitas simulações seguidas. Aguarde um minuto.");
        }

        var cenario = request.Scenario ?? throw new WorkflowsValidationException("scenario: obrigatório.");
        if (cenario.Inputs is null || cenario.Inputs.Count > _options.MaxSimulationInputs || (cenario.Stubs?.Count ?? 0) > _options.MaxSimulationStubs)
        {
            throw new WorkflowsValidationException("scenario: entradas ou respostas simuladas em excesso.");
        }

        if (cenario.Inputs.Any(i => !Enum.IsDefined(i.Kind) || (i.Text?.Length ?? 0) > _options.Limits.MaxVariableLength || i.Seconds is < 0 or > 604_800))
        {
            throw new WorkflowsValidationException("scenario: entrada inválida.");
        }

        var graph = Parse(request.GraphJson);
        var problemas = await ValidateGraphAsync(graph, request.OrganizationId, cancellationToken);
        if (problemas.Count > 0)
        {
            return new SimulateResult(problemas, null);
        }

        return new SimulateResult([], new FlowSimulator(_options.Limits).Run(graph, cenario));
    }

    private FlowGraph Parse(string json)
    {
        if (!FlowGraphSerializer.TryParse(json ?? string.Empty, _options.Limits, out var graph, out var erro))
        {
            throw new WorkflowsValidationException($"graph: {erro}.");
        }

        return graph!;
    }

    private async Task<IReadOnlyList<FlowProblem>> ValidateGraphAsync(FlowGraph graph, Guid organizationId, CancellationToken cancellationToken)
    {
        IFlowCatalogView? catalog = null;
        if (graph.Nodes.Any(n => n.Type == NodeType.Integration))
        {
            // O catálogo é lido com as permissões de quem está editando. Quem não pode ler operações de integração não tem como conferi-las: o validador as recusa em vez de aceitar sem olhar.
            catalog = new SnapshotCatalogView(await LoadCatalogAsync(organizationId, cancellationToken));
        }

        return FlowValidator.Validate(graph, _options.Limits, catalog).Problems;
    }

    private async Task<IReadOnlyList<CapabilityDescriptor>> LoadCatalogAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return [];
        }

        try
        {
            return await _catalog.ListAsync(CapabilityCaller.Workflow, cancellationToken, organizationId);
        }
        catch (AccessDeniedException)
        {
            return [];
        }
    }

    private bool CanRead(WorkflowVersion versao) => _auth.Context.IsAllowed(Permission.WorkflowRead, versao.OrganizationId);
}
