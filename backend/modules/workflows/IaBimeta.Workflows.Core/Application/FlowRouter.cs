using System.Text.Json;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Domain;

namespace IaBimeta.Workflows.Core.Application;

/// <summary>
/// O roteador (ADR 0038 §1): a decisão ÚNICA e durável de quem responde à sessão. Com a função desligada responde "agente" sem consultar nem gravar nada (o caminho idêntico ao anterior ao chatbot visual).
/// Só a PRIMEIRA mensagem de uma sessão pode escolher um fluxo, por uma ativação que case com a organização, a unidade e o canal dela: uma sessão em andamento nunca muda de dono por uma ativação criada depois.
/// A decisão e a instância são gravadas juntas e uma vez (chave primária por tenant e sessão): duas entregas simultâneas (o Agents e o Workflows consultam ao mesmo tempo) recebem a MESMA resposta.
/// </summary>
public sealed class FlowRouter : IAutomationRouter
{
    private readonly IFlowRuntimeStore _store;
    private readonly IWorkflowStore _versions;
    private readonly IConversationQueries _sessions;
    private readonly IFlowCipher _cipher;
    private readonly IRequestContextAccessor _context;
    private readonly WorkflowsOptions _options;
    private readonly TimeProvider _clock;

    public FlowRouter(IFlowRuntimeStore store, IWorkflowStore versions, IConversationQueries sessions, IFlowCipher cipher, IRequestContextAccessor context, WorkflowsOptions options, TimeProvider clock)
    {
        _store = store;
        _versions = versions;
        _sessions = sessions;
        _cipher = cipher;
        _context = context;
        _options = options;
        _clock = clock;
    }

    public async Task<AutomationOwner> GetOwnerAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return AutomationOwner.Agent;
        }

        var decisao = await _store.GetDecisionAsync(sessionId, cancellationToken);
        return decisao?.Owner ?? AutomationOwner.Agent;
    }

    public async Task<AutomationOwner> ResolveOnInboundAsync(Guid sessionId, long inboundSeq, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return AutomationOwner.Agent;
        }

        var existente = await _store.GetDecisionAsync(sessionId, cancellationToken);
        if (existente is not null)
        {
            return existente.Owner;
        }

        if (inboundSeq != 1)
        {
            // Uma sessão em andamento não muda de dono: só a primeira mensagem pode escolher um fluxo.
            return AutomationOwner.Agent;
        }

        if (!_cipher.IsConfigured)
        {
            // Sem a chave das variáveis nenhuma instância pode ser criada (nunca se grava variável em claro): a sessão fica com o agente.
            return AutomationOwner.Agent;
        }

        var sessao = await _sessions.GetSessionAsync(sessionId, cancellationToken);
        if (sessao is null || sessao.Status != IaBimeta.Conversations.Contracts.SessionStatus.Open || sessao.HandlingMode != IaBimeta.Conversations.Contracts.HandlingMode.AiAutonomous)
        {
            return AutomationOwner.Agent;
        }

        var ativacao = (await _store.ListEnabledActivationsAsync(cancellationToken))
            .Where(a => a.Matches(sessao.OrganizationId, sessao.BusinessUnitId, sessao.ChannelInstanceId.Value) && a.InRollout(sessao.ContactId))
            .OrderByDescending(a => a.Specificity)
            .ThenByDescending(a => a.Priority)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
        if (ativacao is null)
        {
            return AutomationOwner.Agent;
        }

        var versao = await _versions.FindPublishedAsync(ativacao.WorkflowKey, cancellationToken);
        if (versao is null || versao.OrganizationId != sessao.OrganizationId)
        {
            return AutomationOwner.Agent;
        }

        // O teto de instâncias ativas por tenant: acima dele a sessão nova fica com o agente (nada é criado).
        if (await _store.CountActiveAsync(cancellationToken) >= _options.MaxActiveInstancesPerTenant)
        {
            return AutomationOwner.Agent;
        }

        var agora = _clock.GetUtcNow();
        var tenant = _context.GetRequired().TenantId;
        var instanciaId = Guid.NewGuid();
        var variaveis = _cipher.Protect(JsonSerializer.Serialize(new Dictionary<string, string>()), tenant, instanciaId, FlowPurposes.Variables);
        var instancia = FlowInstance.Create(tenant, sessionId, sessao.OrganizationId, versao.Id, versao.Key, versao.Version, ativacao.Id, ativacao.OnFailure, variaveis.Ciphertext, variaveis.KeyId, agora);
        instancia.SetId(instanciaId);
        var decisao = RoutingDecision.Create(tenant, sessionId, AutomationOwner.Flow, ativacao.Id, agora);

        if (await _store.TryRecordAsync(decisao, instancia, cancellationToken))
        {
            WorkflowsMetrics.InstanceStarted();
            return AutomationOwner.Flow;
        }

        // Perdeu a disputa: quem ganhou decidiu, e a resposta é a dele.
        return (await _store.GetDecisionAsync(sessionId, cancellationToken))?.Owner ?? AutomationOwner.Agent;
    }
}

/// <summary>As finalidades da cifra (entram como dado autenticado): o texto cifrado de uma finalidade não abre em outra.</summary>
public static class FlowPurposes
{
    public const string Variables = "variables";
    public const string Effect = "effect";
}
