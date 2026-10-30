using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// As regras de roteamento (canal, unidade e setor para fila e prioridade) e a simulação da decisão. Quem gere as filas da
/// organização cria, liga, desliga e apaga; quem as lê vê (TEN-2). A regra só aponta para fila da própria organização, e a unidade e
/// o setor dela precisam existir nela. O canal é um identificador opaco: o Channels é o dono do cadastro, e o que o número autoriza é
/// perguntado pelo Conversations a cada vez que uma conversa muda de unidade (ADR 0008), nunca copiado para cá.
/// </summary>
public sealed class RoutingRuleService
{
    private const string RuleResource = "routing-rule";

    private readonly IRoutingRepository _routing;
    private readonly IQueueRepository _queues;
    private readonly IOrganizationDirectory _organizations;
    private readonly IBusinessUnitDirectory _units;
    private readonly IDepartmentDirectory _departments;
    private readonly IConversationGateway _conversations;
    private readonly QueueRouter _router;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IDeferredAudit _audit;

    public RoutingRuleService(
        IRoutingRepository routing,
        IQueueRepository queues,
        IOrganizationDirectory organizations,
        IBusinessUnitDirectory units,
        IDepartmentDirectory departments,
        IConversationGateway conversations,
        QueueRouter router,
        IRequestContextAccessor context,
        TimeProvider clock,
        IDeferredAudit audit)
    {
        _routing = routing;
        _queues = queues;
        _organizations = organizations;
        _units = units;
        _departments = departments;
        _conversations = conversations;
        _router = router;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="ContactCenterResourceNotFoundException">A organização não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir filas da organização.</exception>
    /// <exception cref="ContactCenterValidationException">Dados inválidos, fila de destino, unidade ou setor que não existem na organização.</exception>
    public async Task<RoutingRuleView> CreateAsync(
        Guid organizationId, string name, Guid? channelInstanceId, Guid? businessUnitId, Guid? departmentId, Guid queueId, int priority, int order,
        CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        if (!contexto.IsAllowed(Permission.OrganizationRead, organizationId) || !await _organizations.ExistsAsync(organizationId, cancellationToken))
        {
            throw new ContactCenterResourceNotFoundException("Organização");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.QueueManage, organizationId, "contact-center.routing-rule.create", "organization", organizationId.ToString(), cancellationToken);

        // A mesma mensagem para o que não existe e para o que é de outra organização: não se descobre, por tentativa, o que existe nas outras.
        var fila = await _queues.FindQueueReadOnlyAsync(queueId, cancellationToken);
        if (fila is null || fila.OrganizationId != organizationId)
        {
            throw new ContactCenterValidationException("A fila de destino não existe nesta organização.");
        }

        if (businessUnitId is { } unidade && unidade != Guid.Empty && await _units.FindOrganizationIdAsync(unidade, cancellationToken) != organizationId)
        {
            throw new ContactCenterValidationException("A unidade não existe nesta organização.");
        }

        if (departmentId is { } setor && setor != Guid.Empty && await _departments.FindBusinessUnitIdAsync(setor, cancellationToken) != businessUnitId)
        {
            throw new ContactCenterValidationException("O setor não existe nesta unidade.");
        }

        var regra = RoutingRule.Create(
            contexto.TenantId, organizationId, name, channelInstanceId, businessUnitId, departmentId, queueId, priority, order, _clock.GetUtcNow());
        _audit.Defer(new AuditEntry("contact-center.routing-rule.create", RuleResource, regra.Id.ToString(), AuditOutcome.Success));
        await _routing.AddRuleAsync(regra, cancellationToken);
        await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        return RoutingRuleView.From(regra);
    }

    /// <summary>Uma regra de outro tenant, ou de organização sem permissão de leitura, devolve <c>null</c> (TEN-2).</summary>
    public async Task<RoutingRuleView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var regra = await _routing.FindRuleReadOnlyAsync(id, cancellationToken);
        return regra is null || !contexto.IsAllowed(Permission.QueueRead, regra.OrganizationId) ? null : RoutingRuleView.From(regra);
    }

    /// <summary>As regras que o usuário lê, na ordem de avaliação.</summary>
    /// <exception cref="AccessDeniedException">O usuário não tem a permissão de ler filas em organização alguma.</exception>
    public async Task<IReadOnlyList<RoutingRuleView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var visiveis = contexto.OrganizationsWithPermission(Permission.QueueRead);
        if (visiveis is { Count: 0 })
        {
            throw new AccessDeniedException(Permission.QueueRead);
        }

        var regras = await _routing.ListRulesAsync(visiveis, cancellationToken);
        return regras.Select(RoutingRuleView.From).ToList();
    }

    public Task<RoutingRuleView> EnableAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(id, true, "contact-center.routing-rule.enable", cancellationToken);

    public Task<RoutingRuleView> DisableAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(id, false, "contact-center.routing-rule.disable", cancellationToken);

    /// <summary>Apaga a regra. Devolve <c>false</c>, sem auditoria, se ela já não existia (idempotente).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var regra = await _routing.FindRuleAsync(id, cancellationToken);
        if (regra is null || !contexto.IsAllowed(Permission.QueueRead, regra.OrganizationId))
        {
            return false;
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.QueueManage, regra.OrganizationId, "contact-center.routing-rule.delete", RuleResource, regra.Id.ToString(), cancellationToken);
        _routing.RemoveRule(regra);
        _audit.Defer(new AuditEntry("contact-center.routing-rule.delete", RuleResource, regra.Id.ToString(), AuditOutcome.Success));
        await _routing.SaveChangesAsync(cancellationToken);
        await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        return true;
    }

    /// <summary>
    /// Simula o roteamento de uma conversa (não muda nada): os destinos que seriam tentados, na ordem, e o que cada fila recusada
    /// alegou. Para quem gere as filas da organização da conversa.
    /// </summary>
    public async Task<RoutingDecisionView> PreviewAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var sessao = await _conversations.GetSessionAsync(sessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Conversa");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.QueueManage, sessao.OrganizationId, "contact-center.routing.preview", "session", sessao.Id.ToString(), cancellationToken);

        var decisao = await _router.ResolveByRulesAsync(sessao, _clock.GetUtcNow(), cancellationToken);
        return new RoutingDecisionView(
            decisao.Candidates.Select(c => new RouteCandidateView(c.Queue.Id, c.Priority, c.RoutingRuleId, c.TargetBusinessUnitId, c.TargetDepartmentId, c.Notes)).ToList(),
            decisao.Refusals.Select(r => new RouteRefusalView(r.QueueId, r.Rule)).ToList());
    }

    private async Task<RoutingRuleView> ChangeEnabledAsync(Guid id, bool enabled, string operation, CancellationToken cancellationToken)
    {
        var contexto = _context.GetRequired();
        var regra = await _routing.FindRuleAsync(id, cancellationToken);
        if (regra is null || !contexto.IsAllowed(Permission.QueueRead, regra.OrganizationId))
        {
            throw new ContactCenterResourceNotFoundException("Regra de roteamento");
        }

        await AuditedAccess.RequireAsync(_audit, contexto, Permission.QueueManage, regra.OrganizationId, operation, RuleResource, regra.Id.ToString(), cancellationToken);
        if (regra.SetEnabled(enabled, _clock.GetUtcNow()))
        {
            _audit.Defer(new AuditEntry(operation, RuleResource, regra.Id.ToString(), AuditOutcome.Success));
            await _routing.SaveChangesAsync(cancellationToken);
            await AuditedAccess.DeliverAsync(_audit, cancellationToken);
        }

        return RoutingRuleView.From(regra);
    }
}
