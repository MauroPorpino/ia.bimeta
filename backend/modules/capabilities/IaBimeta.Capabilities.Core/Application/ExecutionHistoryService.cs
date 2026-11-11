using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Application;

public sealed record ConnectionStatsView(Guid ConnectionId, int Total, int Failed, int Unknown, DateTimeOffset? LastAt);

/// <summary>
/// O histórico de execuções e o resumo por conexão, para a administração (ADR 0035, §5). Filtra pelas organizações que o contexto pode ler (uma execução de conexão de outra organização não aparece) e nunca traz
/// parâmetros nem resposta (o registro não os guarda).
/// </summary>
public sealed class ExecutionHistoryService
{
    private const int MaxTake = 100;

    private readonly CapabilitiesAuthorizer _auth;
    private readonly IExecutionStore _executions;
    private readonly TimeProvider _clock;

    internal ExecutionHistoryService(CapabilitiesAuthorizer auth, IExecutionStore executions, TimeProvider clock)
    {
        _auth = auth;
        _executions = executions;
        _clock = clock;
    }

    public async Task<ExecutionPage> ListAsync(Guid? connectionId, string? key, CapabilityExecutionStatus? status, bool? isTest, int take, int skip, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationRead);
        if (take is < 1 or > MaxTake || skip < 0 || (status is { } s && !Enum.IsDefined(s)))
        {
            throw new CapabilitiesValidationException($"take: de 1 a {MaxTake}; skip não negativo; situação válida.");
        }

        if (key is not null && !DefinitionService.KeyRegex().IsMatch(key))
        {
            throw new CapabilitiesValidationException("key: inválida.");
        }

        var visiveis = _auth.Context.OrganizationsWithPermission(Permission.IntegrationRead);
        var filtro = new ExecutionFilter(connectionId, key, status, isTest);
        var itens = await _executions.ListAsync(filtro, visiveis, take, skip, cancellationToken);
        var total = await _executions.CountAsync(filtro, visiveis, cancellationToken);
        return new ExecutionPage(itens.Select(ExecutionView.From).ToList(), total);
    }

    public async Task<ExecutionView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationRead);
        var execucao = await _executions.FindAsync(id, cancellationToken);
        var visiveis = _auth.Context.OrganizationsWithPermission(Permission.IntegrationRead);
        var pode = execucao is not null && (visiveis is null || (execucao.OrganizationId is { } o && visiveis.Contains(o)));
        return pode ? ExecutionView.From(execucao!) : null;
    }

    public async Task<IReadOnlyList<ConnectionStatsView>> StatsAsync(int days, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationRead);
        if (days is < 1 or > 90)
        {
            throw new CapabilitiesValidationException("days: de 1 a 90.");
        }

        var visiveis = _auth.Context.OrganizationsWithPermission(Permission.IntegrationRead);
        var linhas = await _executions.StatsAsync(_clock.GetUtcNow().AddDays(-days), visiveis, cancellationToken);
        return linhas.Select(l => new ConnectionStatsView(l.ConnectionId, l.Total, l.Failed, l.Unknown, l.LastAt)).ToList();
    }
}
