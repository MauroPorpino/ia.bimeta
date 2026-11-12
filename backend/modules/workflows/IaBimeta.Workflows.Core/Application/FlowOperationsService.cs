using IaBimeta.Audit.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

public sealed record FlowActivationView(
    Guid Id,
    Guid OrganizationId,
    Guid? BusinessUnitId,
    Guid? ChannelInstanceId,
    string WorkflowKey,
    bool Enabled,
    int Priority,
    int RolloutPercent,
    OnFailurePolicy OnFailure,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision)
{
    internal static FlowActivationView From(FlowActivation a) =>
        new(a.Id, a.OrganizationId, a.BusinessUnitId, a.ChannelInstanceId, a.WorkflowKey, a.Enabled, a.Priority, a.RolloutPercent, a.OnFailure, a.CreatedAt, a.UpdatedAt, a.Revision);
}

public sealed record FlowActivationRequest(
    Guid OrganizationId, Guid? BusinessUnitId, Guid? ChannelInstanceId, string WorkflowKey, int Priority, int RolloutPercent, OnFailurePolicy OnFailure, int? ExpectedRevision);

/// <summary>Uma instância no histórico: SÓ identificadores, estados e códigos. Nunca variáveis nem conteúdo de mensagem.</summary>
public sealed record FlowInstanceView(
    Guid Id, Guid SessionId, string WorkflowKey, int VersionNumber, FlowStatus Status, string? NodeId, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt)
{
    internal static FlowInstanceView From(FlowInstance i) => new(i.Id, i.SessionId, i.WorkflowKey, i.VersionNumber, i.Status, i.NodeId, i.FailureCode, i.CreatedAt, i.EndedAt);
}

public sealed record FlowStepView(long Step, string NodeId, string Outcome, DateTimeOffset At);

public sealed record FlowEffectView(string Key, string Kind, string State, string? ResultCode, int Attempts);

public sealed record FlowInstanceDetailView(FlowInstanceView Instance, IReadOnlyList<FlowStepView> Steps, IReadOnlyList<FlowEffectView> Effects);

public sealed record FlowInstancePage(IReadOnlyList<FlowInstanceView> Items, int Total);

public sealed record FlowMetricRow(string WorkflowKey, int VersionNumber, int Started, int Active, int Completed, int HandedOff, int Delegated, int Failed, int Cancelled);

public sealed record FlowFunnelRow(string NodeId, int Visits);

/// <param name="Organizations">Nulo = todas as organizações do tenant (concessão de tenant); senão só estas.</param>
public sealed record FlowInstanceFilter(IReadOnlyCollection<Guid>? Organizations, FlowStatus? Status, string? Key, int Take, int Skip);

/// <summary>O armazém das telas de operação: ativações, histórico e métricas. Todas as consultas valem para o tenant do contexto.</summary>
public interface IFlowOperationsStore
{
    Task<IReadOnlyList<FlowActivation>> ListActivationsAsync(CancellationToken cancellationToken);

    Task<FlowActivation?> FindActivationAsync(Guid id, CancellationToken cancellationToken);

    Task<int> CountActivationsAsync(CancellationToken cancellationToken);

    void Add(FlowActivation activation);

    /// <exception cref="WorkflowsConflictException"><c>changed</c>.</exception>
    Task SaveAsync(CancellationToken cancellationToken);

    Task<FlowInstancePage> ListInstancesAsync(FlowInstanceFilter filter, CancellationToken cancellationToken);

    Task<FlowInstance?> FindInstanceAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowStepView>> ListStepsAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowEffectView>> ListEffectsAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowMetricRow>> MetricsAsync(IReadOnlyCollection<Guid>? organizations, DateTimeOffset since, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowFunnelRow>> FunnelAsync(IReadOnlyCollection<Guid>? organizations, string key, int version, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>
/// A operação dos fluxos (ADR 0038): ativações por organização, unidade e canal (nascem DESLIGADAS; ligar exige uma versão publicada), o histórico de execução e as métricas. Ler é <c>WorkflowRead</c>; criar,
/// alterar, ligar e desligar uma ativação é <c>WorkflowPublish</c> (quem edita não coloca no ar), sempre sobre a organização DA ATIVAÇÃO. Cada mudança é auditada, sem conteúdo. O histórico e as métricas só têm
/// identificadores, estados, códigos e contagens: nunca variáveis nem texto de cliente.
/// </summary>
public sealed class FlowOperationsService
{
    private readonly WorkflowsAuthorizer _auth;
    private readonly IFlowOperationsStore _store;
    private readonly IWorkflowStore _versions;
    private readonly WorkflowsOptions _options;
    private readonly TimeProvider _clock;

    internal FlowOperationsService(WorkflowsAuthorizer auth, IFlowOperationsStore store, IWorkflowStore versions, WorkflowsOptions options, TimeProvider clock)
    {
        _auth = auth;
        _store = store;
        _versions = versions;
        _options = options;
        _clock = clock;
    }

    // ------------------------------------------------------------ ativações

    public async Task<IReadOnlyList<FlowActivationView>> ListActivationsAsync(CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        return (await _store.ListActivationsAsync(cancellationToken))
            .Where(a => _auth.Context.IsAllowed(Permission.WorkflowRead, a.OrganizationId))
            .OrderBy(a => a.WorkflowKey, StringComparer.Ordinal)
            .ThenByDescending(a => a.Priority)
            .Select(FlowActivationView.From)
            .ToList();
    }

    public async Task<FlowActivationView> CreateActivationAsync(FlowActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _auth.RequireAny(Permission.WorkflowPublish);
        await _auth.RequireAsync(Permission.WorkflowPublish, request.OrganizationId, "workflows.activation.create", null, cancellationToken, WorkflowsAuthorizer.ActivationResource);
        await ValidateAsync(request, cancellationToken);

        if (await _store.CountActivationsAsync(cancellationToken) >= _options.MaxActivationsPerTenant)
        {
            throw new WorkflowsConflictException("limit-reached", "O limite de ativações desta empresa foi atingido.");
        }

        var agora = _clock.GetUtcNow();
        var ativacao = FlowActivation.Create(
            _auth.Context.TenantId, request.OrganizationId, request.BusinessUnitId, request.ChannelInstanceId, request.WorkflowKey, request.Priority, request.RolloutPercent, request.OnFailure, _auth.Context.Principal?.UserId, agora);
        _store.Add(ativacao);
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("workflows.activation.create", ativacao.Id, AuditOutcome.Success, cancellationToken, WorkflowsAuthorizer.ActivationResource);
        return FlowActivationView.From(ativacao);
    }

    public async Task<FlowActivationView?> UpdateActivationAsync(Guid id, FlowActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _auth.RequireAny(Permission.WorkflowPublish);
        var ativacao = await _store.FindActivationAsync(id, cancellationToken);
        if (ativacao is null)
        {
            return null;
        }

        await _auth.RequireAsync(Permission.WorkflowPublish, ativacao.OrganizationId, "workflows.activation.update", id, cancellationToken, WorkflowsAuthorizer.ActivationResource);
        if (request.OrganizationId != ativacao.OrganizationId)
        {
            throw new WorkflowsValidationException("organizationId: a organização de uma ativação não muda.");
        }

        await ValidateAsync(request, cancellationToken);
        if (request.ExpectedRevision is not { } esperada || esperada != ativacao.Revision)
        {
            throw new WorkflowsConflictException("changed", "A ativação foi alterada por outra pessoa. Releia e tente de novo.");
        }

        ativacao.Update(request.BusinessUnitId, request.ChannelInstanceId, request.WorkflowKey, request.Priority, request.RolloutPercent, request.OnFailure, _clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("workflows.activation.update", id, AuditOutcome.Success, cancellationToken, WorkflowsAuthorizer.ActivationResource);
        return FlowActivationView.From(ativacao);
    }

    /// <summary>Liga ou desliga. Ligar exige uma versão PUBLICADA do fluxo na organização da ativação; desligar vale sempre (as instâncias em andamento seguem na versão em que começaram).</summary>
    public async Task<FlowActivationView?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowPublish);
        var ativacao = await _store.FindActivationAsync(id, cancellationToken);
        if (ativacao is null)
        {
            return null;
        }

        var operacao = enabled ? "workflows.activation.enable" : "workflows.activation.disable";
        await _auth.RequireAsync(Permission.WorkflowPublish, ativacao.OrganizationId, operacao, id, cancellationToken, WorkflowsAuthorizer.ActivationResource);
        if (enabled)
        {
            await RequirePublishedAsync(ativacao.WorkflowKey, ativacao.OrganizationId, cancellationToken);
        }

        ativacao.SetEnabled(enabled, _clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync(operacao, id, AuditOutcome.Success, cancellationToken, WorkflowsAuthorizer.ActivationResource);
        return FlowActivationView.From(ativacao);
    }

    private async Task ValidateAsync(FlowActivationRequest request, CancellationToken cancellationToken)
    {
        var problemas = new List<string>();
        if (!WorkflowDefinitionService.KeyRegex().IsMatch(request.WorkflowKey ?? string.Empty))
        {
            problemas.Add("workflowKey: chave inválida.");
        }

        if (request.BusinessUnitId == Guid.Empty || request.ChannelInstanceId == Guid.Empty)
        {
            problemas.Add("unidade e canal: identificador inválido.");
        }

        if (request.RolloutPercent is < 0 or > 100)
        {
            problemas.Add("rolloutPercent: de 0 a 100.");
        }

        if (request.Priority is < -1000 or > 1000)
        {
            problemas.Add("priority: de -1000 a 1000.");
        }

        if (!Enum.IsDefined(request.OnFailure))
        {
            problemas.Add("onFailure: Handoff ou Agent.");
        }

        if (problemas.Count > 0)
        {
            throw new WorkflowsValidationException(problemas);
        }

        // O fluxo precisa existir (publicado ou, ao menos, em uso) na MESMA organização: uma ativação não aponta para o fluxo de outra organização.
        var versoes = await _versions.ListVersionsAsync(request.WorkflowKey!, cancellationToken);
        if (versoes.Count == 0 || versoes.Any(v => v.OrganizationId != request.OrganizationId))
        {
            throw new WorkflowsValidationException("workflowKey: o fluxo não existe nesta organização.");
        }
    }

    private async Task RequirePublishedAsync(string key, Guid organizationId, CancellationToken cancellationToken)
    {
        var publicada = await _versions.FindPublishedAsync(key, cancellationToken);
        if (publicada is null || publicada.OrganizationId != organizationId)
        {
            throw new WorkflowsConflictException("no-published-version", "O fluxo não tem uma versão publicada nesta organização.");
        }
    }

    // ------------------------------------------------------------ histórico e métricas

    private IReadOnlyCollection<Guid>? VisibleOrganizations()
    {
        var orgs = _auth.Context.OrganizationsWithPermission(Permission.WorkflowRead);
        return orgs is null ? null : orgs.ToList();
    }

    public async Task<FlowInstancePage> ListInstancesAsync(FlowStatus? status, string? key, int take, int skip, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        if (status is not null && !Enum.IsDefined(status.Value))
        {
            throw new WorkflowsValidationException("status: inválido.");
        }

        var chave = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        return await _store.ListInstancesAsync(new FlowInstanceFilter(VisibleOrganizations(), status, chave, Math.Clamp(take, 1, 100), Math.Max(0, skip)), cancellationToken);
    }

    public async Task<FlowInstanceDetailView?> GetInstanceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        var instancia = await _store.FindInstanceAsync(id, cancellationToken);
        if (instancia is null || !_auth.Context.IsAllowed(Permission.WorkflowRead, instancia.OrganizationId))
        {
            return null;
        }

        return new FlowInstanceDetailView(FlowInstanceView.From(instancia), await _store.ListStepsAsync(id, cancellationToken), await _store.ListEffectsAsync(id, cancellationToken));
    }

    public async Task<IReadOnlyList<FlowMetricRow>> MetricsAsync(int days, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        return await _store.MetricsAsync(VisibleOrganizations(), _clock.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90)), cancellationToken);
    }

    public async Task<IReadOnlyList<FlowFunnelRow>> FunnelAsync(string key, int version, int days, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.WorkflowRead);
        return await _store.FunnelAsync(VisibleOrganizations(), key, version, _clock.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90)), cancellationToken);
    }
}
