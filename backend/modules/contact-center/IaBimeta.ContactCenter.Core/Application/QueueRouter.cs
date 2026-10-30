using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.ContactCenter.Application;

/// <summary>Um destino possível para o atendimento: a fila, a prioridade, a regra que a escolheu e a unidade para onde a sessão iria junto.</summary>
public sealed record RouteCandidate(
    ServiceQueue Queue,
    int Priority,
    Guid? RoutingRuleId,
    Guid? TargetBusinessUnitId,
    Guid? TargetDepartmentId,
    IReadOnlyList<string> Notes);

public sealed record RouteRefusal(Guid QueueId, ContactCenterRule Rule);

public sealed record RoutingDecision(IReadOnlyList<RouteCandidate> Candidates, IReadOnlyList<RouteRefusal> Refusals);

/// <summary>
/// Decide para qual fila vai um atendimento. Duas entradas, o mesmo percurso por fila: (1) uma fila PEDIDA (explícita), que precisa
/// atender a organização, a unidade e o setor da conversa; (2) o ROTEAMENTO, em que as regras ligadas da organização, na ordem, dizem
/// a fila e a prioridade, e a fila escolhida pode ser de outra unidade (a sessão vai junto, e o Conversations só aceita se o número a
/// autoriza, ADR 0008). Em cada fila vale a política dela: pausada, fora do horário (aceitar, recusar ou ir para a contingência) e sem
/// atendente disponível (esperar ou ir para a contingência, só se lá houver atendente). Ninguém escolhe unidade, fila ou prioridade
/// fora destas regras: nem a IA, nem quem pede o handoff. A decisão não executa nada; só diz o que seria tentado, na ordem.
/// </summary>
public sealed class QueueRouter
{
    // Quantas filas de contingência encadeadas se seguem (e o ciclo A->B->A nunca se repete: há um conjunto de visitadas).
    private const int MaxFallbackDepth = 4;

    private readonly IQueueRepository _queues;
    private readonly IRoutingRepository _routing;

    public QueueRouter(IQueueRepository queues, IRoutingRepository routing)
    {
        _queues = queues;
        _routing = routing;
    }

    /// <summary>
    /// A fila foi PEDIDA: tem de atender a conversa como ela está (sem mudar de unidade), e a política da fila (e da contingência dela,
    /// também estrita) decide o resto.
    /// </summary>
    public async Task<RoutingDecision> ResolveRequestedAsync(SessionView session, ServiceQueue requested, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recusas = new List<RouteRefusal>();
        var resultado = await WalkAsync(requested, session, strict: true, now, new HashSet<Guid>(), 0, cancellationToken);
        var candidatos = new List<RouteCandidate>();
        Collect(resultado, null, null, session, candidatos, recusas);
        return new RoutingDecision(candidatos, recusas);
    }

    /// <summary>As regras ligadas da organização que servem a conversa, na ordem de avaliação, e o percurso de cada fila delas.</summary>
    public async Task<RoutingDecision> ResolveByRulesAsync(SessionView session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var regras = (await _routing.ListEnabledRulesAsync(session.OrganizationId, cancellationToken))
            .Where(r => r.Matches(session.OrganizationId, session.ChannelInstanceId.Value, session.BusinessUnitId, session.DepartmentId))
            .OrderBy(r => r.Order).ThenByDescending(r => r.Specificity).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToList();

        var candidatos = new List<RouteCandidate>();
        var recusas = new List<RouteRefusal>();
        foreach (var regra in regras)
        {
            var fila = await _queues.FindQueueReadOnlyAsync(regra.QueueId, cancellationToken);
            if (fila is null || fila.OrganizationId != session.OrganizationId)
            {
                continue;
            }

            var resultado = await WalkAsync(fila, session, strict: false, now, new HashSet<Guid>(), 0, cancellationToken);
            Collect(resultado, regra.Id, regra.Priority, session, candidatos, recusas);
        }

        return new RoutingDecision(candidatos, recusas);
    }

    private static void Collect(Walk resultado, Guid? ruleId, int? rulePriority, SessionView session, List<RouteCandidate> candidatos, List<RouteRefusal> recusas)
    {
        if (resultado.Refused is { } recusa)
        {
            recusas.Add(recusa);
            return;
        }

        var fila = resultado.Queue!;
        if (candidatos.Any(c => c.Queue.Id == fila.Id))
        {
            return;
        }

        var (unidade, setor) = fila.Covers(session.OrganizationId, session.BusinessUnitId, session.DepartmentId)
            ? ((Guid?)null, (Guid?)null)
            : Placement(fila, session);
        var notas = new List<string>();
        if (ruleId is not null)
        {
            notas.Add("rule");
        }

        notas.AddRange(resultado.Notes);
        candidatos.Add(new RouteCandidate(fila, rulePriority ?? fila.DefaultPriority, ruleId, unidade, setor, notas));
    }

    /// <summary>
    /// Para onde a sessão vai, além da fila, quando a fila é de unidade: a unidade e o setor dela, ou nada se a sessão já está lá. Uma
    /// fila da organização inteira não mexe na unidade da sessão.
    /// </summary>
    public static (Guid? BusinessUnit, Guid? Department) Placement(ServiceQueue destino, SessionView sessao)
    {
        if (destino.BusinessUnitId is not { } unidade)
        {
            return (null, null);
        }

        var setor = destino.DepartmentId ?? (sessao.BusinessUnitId == unidade ? sessao.DepartmentId : null);
        return sessao.BusinessUnitId == unidade && sessao.DepartmentId == setor ? (null, null) : (unidade, setor);
    }

    private sealed record Walk(ServiceQueue? Queue, IReadOnlyList<string> Notes, int AvailableAgents, RouteRefusal? Refused);

    private static Walk Refuse(ServiceQueue queue, ContactCenterRule rule) => new(null, Array.Empty<string>(), 0, new RouteRefusal(queue.Id, rule));

    private async Task<Walk> WalkAsync(
        ServiceQueue queue, SessionView session, bool strict, DateTimeOffset now, HashSet<Guid> visited, int depth, CancellationToken cancellationToken)
    {
        visited.Add(queue.Id);

        // A política da fila (fuso, faixas, dias fechados, pausa, contingência) é lida de UM instante: a decisão inteira sobre esta fila
        // vale para o mesmo horário, mesmo que o gestor o esteja trocando agora. A leitura anterior da fila só serviu para chegar até aqui.
        var instantaneo = await _routing.GetPolicySnapshotAsync(queue.Id, cancellationToken);
        if (instantaneo is null)
        {
            return Refuse(queue, ContactCenterRule.QueueDoesNotCoverSession);
        }

        queue = instantaneo.Queue;

        // Fila pedida: tem de atender a conversa como ela está. Fila de regra: pode pedir a mudança de unidade (decidida depois).
        if (strict && !queue.Covers(session.OrganizationId, session.BusinessUnitId, session.DepartmentId))
        {
            return Refuse(queue, ContactCenterRule.QueueDoesNotCoverSession);
        }

        if (queue.OrganizationId != session.OrganizationId)
        {
            return Refuse(queue, ContactCenterRule.QueueDoesNotCoverSession);
        }

        if (queue.Status == QueueStatus.Paused)
        {
            return Refuse(queue, ContactCenterRule.QueuePaused);
        }

        var notas = new List<string>();
        var aberta = instantaneo.IsOpenAt(now);
        if (!aberta)
        {
            switch (queue.OutOfHours)
            {
                case OutOfHoursBehavior.Accept:
                    notas.Add("closed-wait");
                    break;

                case OutOfHoursBehavior.Fallback when await TryFallbackAsync(queue, session, strict, now, visited, depth, cancellationToken) is { } alternativa:
                    return alternativa with { Notes = new[] { "out-of-hours:fallback" }.Concat(alternativa.Notes).ToList() };

                default:
                    return Refuse(queue, ContactCenterRule.QueueClosed);
            }
        }

        var disponiveis = await _routing.CountAvailableAgentsAsync(queue.Id, queue.MaxItemsPerAgent, cancellationToken);
        if (disponiveis == 0)
        {
            // Sem atendente disponível, a contingência só vale se lá houver alguém: do contrário a conversa espera aqui mesmo.
            if (queue.NoAgent == NoAgentBehavior.Fallback
                && await TryFallbackAsync(queue, session, strict, now, visited, depth, cancellationToken) is { AvailableAgents: > 0 } alternativa)
            {
                return alternativa with { Notes = new[] { "no-agent:fallback" }.Concat(alternativa.Notes).ToList() };
            }

            notas.Add("no-agent-wait");
        }

        return new Walk(queue, notas, disponiveis, null);
    }

    /// <summary>A contingência da fila, ou <c>null</c> se não há, já foi visitada, é muito funda, ou ela mesma recusa.</summary>
    private async Task<Walk?> TryFallbackAsync(
        ServiceQueue queue, SessionView session, bool strict, DateTimeOffset now, HashSet<Guid> visited, int depth, CancellationToken cancellationToken)
    {
        if (queue.FallbackQueueId is not { } id || visited.Contains(id) || depth >= MaxFallbackDepth)
        {
            return null;
        }

        var contingencia = await _queues.FindQueueReadOnlyAsync(id, cancellationToken);
        if (contingencia is null)
        {
            return null;
        }

        var alternativa = await WalkAsync(contingencia, session, strict, now, visited, depth + 1, cancellationToken);
        return alternativa.Refused is null ? alternativa : null;
    }
}
