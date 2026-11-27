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
    private readonly ITenantOperationGate? _gate;

    public FlowRouter(IFlowRuntimeStore store, IWorkflowStore versions, IConversationQueries sessions, IFlowCipher cipher, IRequestContextAccessor context, WorkflowsOptions options, TimeProvider clock, ITenantOperationGate? operationGate = null)
    {
        _gate = operationGate;
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
        if (decisao?.Owner == AutomationOwner.Flow && await IsStaleFlowAsync(sessionId, cancel: false, cancellationToken))
        {
            return AutomationOwner.Agent; // o fluxo é de ANTES da última reativação: já não é o dono da sessão (somente leitura)
        }

        return decisao?.Owner ?? AutomationOwner.Agent;
    }

    /// <summary>
    /// O fluxo ativo da sessão foi CRIADO antes da última reativação da empresa (P1-03 da 2.ª revisão)? A referência é a data de criação, estável. Se sim, ele não responde mais: a sessão volta ao agente para a mensagem
    /// NOVA (sem tocar o Conversations: o modo de atendimento atual, inclusive humano, continua valendo) e, com <paramref name="cancel"/>, o cancelamento seguro é pedido (o executor o conclui sem enviar nada).
    /// </summary>
    private async Task<bool> IsStaleFlowAsync(Guid sessionId, bool cancel, CancellationToken cancellationToken)
    {
        if (_gate is null)
        {
            return false;
        }

        var estado = await _gate.GetAsync(_context.GetRequired().TenantId, cancellationToken);
        if (!estado.IsOperational || estado.OperationalSince is not { } corte)
        {
            return false;
        }

        var instancia = await _store.FindActiveBySessionAsync(sessionId, cancellationToken);
        if (instancia is null || instancia.CreatedAt >= corte)
        {
            return false;
        }

        if (cancel)
        {
            instancia.RequestCancel("tenant-suspended", _clock.GetUtcNow());
            await _store.SaveAsync(cancellationToken);
        }

        return true;
    }

    public async Task<AutomationOwner> ResolveOnInboundAsync(Guid sessionId, long inboundSeq, CancellationToken cancellationToken = default, DateTimeOffset? eventOccurredAt = null)
    {
        if (!_options.Enabled)
        {
            return AutomationOwner.Agent;
        }

        var existente = await _store.GetDecisionAsync(sessionId, cancellationToken);
        if (existente is not null)
        {
            if (existente.Owner == AutomationOwner.Flow && await IsStaleFlowAsync(sessionId, cancel: true, cancellationToken))
            {
                return AutomationOwner.Agent; // fluxo antigo: a mensagem NOVA é do agente (que respeita o modo de atendimento atual)
            }

            return existente.Owner;
        }

        if (inboundSeq != 1)
        {
            // Uma sessão em andamento não muda de dono: só a primeira mensagem pode escolher um fluxo.
            return AutomationOwner.Agent;
        }

        // P1-03: empresa suspensa não cria instância nem grava decisão; e um estímulo ANTERIOR à última reativação (entrega atrasada de um evento da suspensão) também não: a sessão fica com o agente e nada é criado.
        if (_gate is not null)
        {
            var estado = await _gate.GetAsync(_context.GetRequired().TenantId, cancellationToken);
            if (!estado.IsOperational || (eventOccurredAt is { } ocorridoEm && estado.OperationalSince is { } reativadaEm && ocorridoEm < reativadaEm))
            {
                return AutomationOwner.Agent;
            }
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
