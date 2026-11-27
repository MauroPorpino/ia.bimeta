using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Contracts;
using IaBimeta.Workflows.Core.Domain;
using EffectKind = IaBimeta.Workflows.Core.Domain.EffectKind;

namespace IaBimeta.Workflows.Core.Application;

public enum FlowRunOutcome
{
    /// <summary>A instância não existe (ou é de outro tenant).</summary>
    NotFound = 1,

    /// <summary>Nada a fazer agora: espera uma mensagem do cliente ou o prazo.</summary>
    Waiting = 2,

    /// <summary>A instância terminou e todos os efeitos foram realizados.</summary>
    Finished = 3,

    /// <summary>O limite de passadas foi atingido: a instância volta a ser devida logo.</summary>
    Yielded = 4,
}

/// <summary>
/// O runtime dos fluxos (ADR 0037 §4 e ADR 0038): roda UMA instância reivindicada, passo a passo. Cada transição do interpretador grava, na MESMA transação, o estado novo, os efeitos pendentes (com identificador
/// determinístico) e o histórico; só depois os efeitos são realizados, um a um, e cada resultado vira nova entrada do interpretador. Uma queda no meio é recuperada sem perda e sem duplicidade: a mensagem e
/// a transferência são idempotentes (mesma chave), e uma consulta externa que não terminou vira "resultado ambíguo" e segue a porta de falha, sem reenvio. O tenant e o contexto de sistema vêm do host.
/// </summary>
public sealed class FlowRuntime
{
    private readonly IFlowRuntimeStore _store;
    private readonly IWorkflowStore _versions;
    private readonly IFlowCipher _cipher;
    private readonly ITurnCommands _turns;
    private readonly IFlowCommands _flowCommands;
    private readonly IConversationQueries _queries;
    private readonly ICapabilityExecutor? _capabilities;
    private readonly ICapabilityCatalog? _catalog;
    private readonly IFlowKnowledgeLookup? _knowledge;
    private readonly IFlowHandoffPort? _handoff;
    private readonly IRequestContextAccessor _context;
    private readonly WorkflowsOptions _options;
    private readonly TimeProvider _clock;
    private readonly FlowInterpreter _interpreter;
    private readonly ITenantOperationGate? _gate;

    public FlowRuntime(
        IFlowRuntimeStore store,
        IWorkflowStore versions,
        IFlowCipher cipher,
        ITurnCommands turns,
        IFlowCommands flowCommands,
        IConversationQueries queries,
        ICapabilityExecutor? capabilities,
        ICapabilityCatalog? catalog,
        IFlowKnowledgeLookup? knowledge,
        IFlowHandoffPort? handoff,
        IRequestContextAccessor context,
        WorkflowsOptions options,
        TimeProvider clock,
        ITenantOperationGate? operationGate = null)
    {
        _gate = operationGate;
        _store = store;
        _versions = versions;
        _cipher = cipher;
        _turns = turns;
        _flowCommands = flowCommands;
        _queries = queries;
        _capabilities = capabilities;
        _catalog = catalog;
        _knowledge = knowledge;
        _handoff = handoff;
        _context = context;
        _options = options;
        _clock = clock;
        _interpreter = new FlowInterpreter(options.Limits);
    }

    private TenantId Tenant => _context.GetRequired().TenantId;

    private DateTimeOffset Now => _clock.GetUtcNow();


    /// <summary>Conclui um efeito (estado e código) e conta no medidor: só tipo e resultado, nunca o conteúdo.</summary>
    private void Complete(FlowEffectRecord efeito, EffectState estado, string? codigo)
    {
        efeito.Complete(estado, codigo, Now);
        WorkflowsMetrics.EffectDone(efeito.Kind, estado);
    }

    /// <summary>Roda a instância até não haver mais o que fazer agora (ou o limite de passadas). Idempotente: pode ser chamado de novo a qualquer momento.</summary>
    public async Task<FlowRunOutcome> RunAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await using var executionLock = await _store.TryLockExecutionAsync(instanceId, cancellationToken);
        if (executionLock is null)
        {
            return FlowRunOutcome.Yielded;
        }

        for (var tentativa = 0; tentativa < 3; tentativa++)
        {
            try
            {
                return await RunOnceAsync(instanceId, cancellationToken);
            }
            catch (WorkflowsConflictException ex) when (ex.Code == "changed")
            {
                // Outro processo (um handler, uma reivindicação) alterou a instância ao mesmo tempo: relê e continua.
            }
        }

        return FlowRunOutcome.Yielded;
    }

    private async Task<FlowRunOutcome> RunOnceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var inst = await _store.FindInstanceAsync(instanceId, cancellationToken);
        if (inst is null)
        {
            return FlowRunOutcome.NotFound;
        }

        if (inst.Status == FlowStatus.Cancelled && inst.FailureCode == "operator-drain")
        {
            return FlowRunOutcome.Finished;
        }

        // P1-03: o INÍCIO efetivo da execução de cada instância confere a empresa de novo (a suspensão pode ter vindo entre a reivindicação e aqui). Suspensa: devolve a instância adiada, sem rodar nada e sem gastar tentativa.
        // Instância CRIADA antes da última reativação (a referência é a data de criação, estável: NextRunAt pode ser um prazo futuro ou renovado por Wake e NUNCA decide anterioridade): é um fluxo ANTIGO e não roda sozinho, nem por Wake, timeout ou novo agendamento: cancela com segurança (nenhuma mensagem sai; nada é apagado) e a decisão volta ao agente SEM acionar a IA agora (sem o comando de devolução ao Conversations, que dispararia um turno sobre a mensagem antiga): o agente só responde a uma mensagem NOVA.
        if (_gate is not null)
        {
            var estado = await _gate.GetAsync(Tenant, cancellationToken);
            if (!estado.IsOperational)
            {
                return await DeferForSuspensionAsync(inst, cancellationToken);
            }

            if (estado.OperationalSince is { } reativadaEm && !inst.IsTerminal && inst.CreatedAt < reativadaEm)
            {
                await CancelAsync(inst, "tenant-suspended", release: false, cancellationToken);
                return FlowRunOutcome.Finished;
            }
        }

        var graph = await LoadGraphAsync(inst, cancellationToken);
        if (graph is null)
        {
            await FailAsync(inst, "version-missing", cancellationToken);
            return FlowRunOutcome.Finished;
        }

        for (var i = 0; i < _options.MaxRunIterations; i++)
        {
            if (inst.CancelReason is { } motivo && !inst.IsTerminal)
            {
                await CancelAsync(inst, motivo, release: false, cancellationToken);
                return FlowRunOutcome.Finished;
            }

            var abertos = await _store.ListOpenEffectsAsync(inst.Id, cancellationToken);
            if (abertos.Count > 0)
            {
                // Antes de realizar QUALQUER efeito (é aqui que sai mensagem, transferência e consulta externa): a empresa continua operacional? E o efeito é de DEPOIS da última reativação? Um efeito preparado antes dela
                // nunca é realizado (viraria um pedido novo de envio só por receber uma data posterior): fica "substituído", sem nada enviado.
                if (_gate is not null)
                {
                    var agoraDaEmpresa = await _gate.GetAsync(Tenant, cancellationToken);
                    if (!agoraDaEmpresa.IsOperational)
                    {
                        return await DeferForSuspensionAsync(inst, cancellationToken);
                    }

                    if (agoraDaEmpresa.OperationalSince is { } corte)
                    {
                        var antigos = abertos.Where(e => e.CreatedAt < corte).ToList();
                        if (antigos.Count > 0)
                        {
                            foreach (var antigo in antigos)
                            {
                                Complete(antigo, EffectState.Superseded, "tenant-suspended");
                            }

                            await _store.SaveAsync(cancellationToken);
                            continue;
                        }
                    }
                }

                if (!await ExecuteEffectsAsync(inst, graph, abertos, cancellationToken))
                {
                    if (inst.IsTerminal)
                    {
                        return await FinalizeAsync(inst, cancellationToken);
                    }

                    inst.ParkUntil(inst.WaitUntil, Now);
                    await _store.SaveAsync(cancellationToken);
                    return FlowRunOutcome.Waiting;
                }

                continue;
            }

            if (inst.IsTerminal)
            {
                return await FinalizeAsync(inst, cancellationToken);
            }

            if (!await AdvanceFromInputAsync(inst, graph, cancellationToken))
            {
                // Nada a fazer agora: o próximo olhar é o prazo da pergunta (ou nunca, até chegar mensagem).
                inst.ParkUntil(inst.WaitUntil, Now);

                await _store.SaveAsync(cancellationToken);
                return FlowRunOutcome.Waiting;
            }
        }

        inst.Defer(Now, Now);
        await _store.SaveAsync(cancellationToken);
        return FlowRunOutcome.Yielded;
    }

    /// <summary>
    /// A empresa deixou de estar operacional: só o lease é solto. O instante do próximo olhar NÃO muda (não é empurrado para depois da reativação): a instância continua "devida desde antes" e, reativada a empresa, é reconhecida
    /// como gatilho antigo e cancelada com segurança, nunca executada sozinha. Sem rodar, sem gastar tentativa e sem apagar nada (o executor do Worker nem reivindica empresa suspensa, então não há laço de reivindicação).
    /// </summary>
    private async Task<FlowRunOutcome> DeferForSuspensionAsync(FlowInstance inst, CancellationToken ct)
    {
        inst.ReleaseLease(Now);
        await _store.SaveAsync(ct);
        return FlowRunOutcome.Waiting;
    }

    // ------------------------------------------------------------------ entrada

    private async Task<bool> AdvanceFromInputAsync(FlowInstance inst, FlowGraph graph, CancellationToken ct)
    {
        var vars = ReadVariables(inst);
        var agora = Now;

        // Uma instância esperando o resultado de um efeito que já não existe (o processo caiu entre a gravação e a realização): o resultado é ambíguo e segue a porta de falha.
        if (inst.Status == FlowStatus.AwaitingEffect && inst.PendingEffectKey is { } chave)
        {
            var nodeId = inst.NodeId;
            var no = graph.Node(nodeId ?? string.Empty);
            FlowInput entrada = no?.Type == NodeType.Knowledge ? new KnowledgeResultInput(chave, false) : new CapabilityResultInput(chave, false, ErrorCode: "outcome-unknown");
            var r = _interpreter.Advance(graph, inst.ToState(vars), entrada, agora);
            if (r.Ignored)
            {
                return false;
            }

            await PersistAsync(inst, r, inst.SeenMessageSeq, ct);
            return true;
        }

        var mensagens = await NextCustomerMessagesAsync(inst, ct);
        if (mensagens is null)
        {
            await CancelAsync(inst, "session-missing", release: false, ct);
            return false;
        }

        StepResult resultado;
        long visto;
        if (inst.Status == FlowStatus.Created)
        {
            if (mensagens.Count == 0)
            {
                return false;
            }

            var primeira = mensagens[0];
            resultado = _interpreter.Start(graph, agora, ToInbound(primeira));
            visto = primeira.MessageSeq;
        }
        else if (mensagens.Count > 0)
        {
            var proxima = mensagens[0];
            resultado = _interpreter.Advance(graph, inst.ToState(vars), ToInbound(proxima), agora);
            visto = proxima.MessageSeq;
        }
        else if (inst.WaitUntil is { } prazo && prazo <= agora)
        {
            resultado = _interpreter.Advance(graph, inst.ToState(vars), new TimeoutInput(), agora);
            visto = inst.SeenMessageSeq;
        }
        else
        {
            return false;
        }

        if (resultado.Ignored)
        {
            return false;
        }

        await PersistAsync(inst, resultado, visto, ct);
        return true;
    }

    /// <summary>As mensagens do cliente depois da última vista, em ordem. Nulo se a sessão não existe mais. Percorre as páginas só até achar a próxima mensagem do cliente.</summary>
    private async Task<List<MessageView>?> NextCustomerMessagesAsync(FlowInstance inst, CancellationToken ct)
    {
        string? cursor = null;
        for (var pagina = 0; pagina < 5; pagina++)
        {
            var historico = await _queries.GetHistoryAsync(new GetHistoryQuery(inst.SessionId, 50, cursor, HistoryOrder.OldestFirst, AfterMessageSeq: inst.SeenMessageSeq), ct);
            if (historico is null)
            {
                return null;
            }

            var doCliente = historico.Items.Where(m => m.Direction == MessageDirection.Inbound && m.Author == MessageAuthor.Customer && !m.IsOptOutCommand).OrderBy(m => m.MessageSeq).ToList();
            if (doCliente.Count > 0 || historico.NextCursor is null)
            {
                return doCliente;
            }

            cursor = historico.NextCursor;
        }

        return [];
    }

    private static InboundInput ToInbound(MessageView m)
    {
        // Texto de uma mensagem de texto, ou a transcrição já pronta de um áudio, vale como resposta escrita; mídia sem texto pronto entra só como "tipo de mídia" (nunca o conteúdo).
        if (!string.IsNullOrWhiteSpace(m.Text) && m.ContentState == ContentState.Ready)
        {
            return new InboundInput(Text: m.Text);
        }

        return m.Kind == MessageKind.Text ? new InboundInput(Text: m.Text ?? string.Empty) : new InboundInput(MediaKind: m.Kind.ToString().ToLowerInvariant());
    }

    // ------------------------------------------------------------------ transição

    private async Task PersistAsync(FlowInstance inst, StepResult result, long seenSeq, CancellationToken ct)
    {
        var tenant = Tenant;
        var agora = Now;
        var cifra = _cipher.Protect(JsonSerializer.Serialize(result.State.Variables), tenant, inst.Id, FlowPurposes.Variables);
        inst.Apply(result.State, cifra.Ciphertext, cifra.KeyId, seenSeq, result.Effects.Count > 0, agora);

        var indice = 0;
        foreach (var efeito in result.Effects)
        {
            var (tipo, conteudo) = Describe(efeito);
            var id = Guid.NewGuid();
            var texto = _cipher.Protect(JsonSerializer.Serialize(conteudo), tenant, id, FlowPurposes.Effect);
            _store.Add(FlowEffectRecord.Create(tenant, id, inst.Id, efeito.Key, result.State.Step, indice++, tipo, efeito.NodeId, texto.Ciphertext, texto.KeyId, agora));
        }

        var desfecho = result.State.Status switch
        {
            FlowStatus.WaitingInput => "waiting",
            FlowStatus.AwaitingEffect => "awaiting-effect",
            FlowStatus.Completed => "completed",
            FlowStatus.HandedOff => "handed_off",
            FlowStatus.Delegated => "delegated",
            FlowStatus.Failed => $"failed:{result.State.FailureCode}",
            _ => result.State.Status.ToString().ToLowerInvariant(),
        };
        for (var i = 0; i < result.Visited.Count; i++)
        {
            _store.Add(FlowStepRecord.Create(tenant, inst.Id, result.State.Step, result.Visited[i], i == result.Visited.Count - 1 ? desfecho : "pass", agora));
        }

        await _store.SaveAsync(ct);
    }

    private sealed record EffectPayload(string? Text, IReadOnlyList<FlowChoice>? Choices, string? OperationKey, Dictionary<string, string>? Parameters, string? Query, string? SourceKey, string? ReasonCode, bool ExpectsFreeText = false);

    private static (EffectKind Kind, EffectPayload Payload) Describe(FlowEffect e) => e switch
    {
        SendMessageEffect m => (EffectKind.SendMessage, new EffectPayload(m.Text, m.Choices, null, null, null, null, null, m.ExpectsFreeText)),
        CallCapabilityEffect c => (EffectKind.CallCapability, new EffectPayload(null, null, c.OperationKey, new Dictionary<string, string>(c.Parameters), null, null, null)),
        LookupKnowledgeEffect k => (EffectKind.LookupKnowledge, new EffectPayload(null, null, null, null, k.Query, k.SourceKey, null)),
        RequestHandoffEffect h => (EffectKind.RequestHandoff, new EffectPayload(null, null, null, null, null, null, h.ReasonCode)),
        DelegateToAgentEffect => (EffectKind.DelegateToAgent, new EffectPayload(null, null, null, null, null, null, null)),
        _ => (EffectKind.Finish, new EffectPayload(null, null, null, null, null, null, null)),
    };

    private EffectPayload ReadPayload(FlowEffectRecord e) =>
        JsonSerializer.Deserialize<EffectPayload>(_cipher.Unprotect(new FlowCipherText(e.PayloadKeyId, e.PayloadCipher), Tenant, e.Id, FlowPurposes.Effect))
        ?? new EffectPayload(null, null, null, null, null, null, null);

    // ------------------------------------------------------------------ efeitos

    /// <summary>Realiza os efeitos abertos, na ordem. Devolve verdadeiro para o laço reler o estado e seguir (acabaram os efeitos, ou uma nova transição foi gravada) e falso quando a execução deve parar (a instância foi cancelada, falhou ou o resultado não se aplicou).</summary>
    private async Task<bool> ExecuteEffectsAsync(FlowInstance inst, FlowGraph graph, IReadOnlyList<FlowEffectRecord> abertos, CancellationToken ct)
    {
        var lista = abertos.OrderBy(e => e.Step).ThenBy(e => e.Index).ToList();

        // Uma transferência pendente: confere ANTES de avisar o cliente (depois dela a conversa já não é do fluxo; o aviso não pode prometer o que as regras recusam).
        var transferencia = lista.FirstOrDefault(e => e.Kind == EffectKind.RequestHandoff && e.State == EffectState.Pending);
        if (transferencia is not null && lista.Any(e => e.Kind == EffectKind.SendMessage && e.State == EffectState.Pending && e.Step == transferencia.Step))
        {
            var verificacao = await CheckHandoffAsync(inst, transferencia, ct);
            if (verificacao is not null)
            {
                foreach (var aviso in lista.Where(e => e.Step == transferencia.Step && e.Kind == EffectKind.SendMessage && e.IsOpen))
                {
                    Complete(aviso, EffectState.Superseded, "handoff-not-routable");
                }

                Complete(transferencia, EffectState.Failed, verificacao);
                await _store.SaveAsync(ct);
                await FailAsync(inst, "handoff-" + verificacao, ct);
                return false;
            }
        }

        var i = 0;
        while (i < lista.Count)
        {
            var e = lista[i];
            switch (e.Kind)
            {
                case EffectKind.SendMessage:
                    {
                        var grupo = new List<FlowEffectRecord>();
                        while (i < lista.Count && lista[i].Kind == EffectKind.SendMessage && lista[i].Step == e.Step)
                        {
                            grupo.Add(lista[i]);
                            i++;
                        }

                        var cobre = !(i < lista.Count && lista[i].Kind == EffectKind.DelegateToAgent && lista[i].Step == e.Step);
                        if (!await SendGroupAsync(inst, grupo, cobre, ct))
                        {
                            return false;
                        }

                        break;
                    }

                case EffectKind.CallCapability:
                    return await ExecuteCapabilityAsync(inst, graph, e, ct);

                case EffectKind.LookupKnowledge:
                    return await ExecuteKnowledgeAsync(inst, graph, e, ct);

                case EffectKind.RequestHandoff:
                    await ExecuteHandoffAsync(inst, e, ct);
                    i++;
                    break;

                case EffectKind.DelegateToAgent:
                    e.Start();
                    await _store.SaveAsync(ct);
                    await ReleaseToAgentAsync(inst, "delegated", ct);
                    Complete(e, EffectState.Done, null);
                    await _store.SaveAsync(ct);
                    i++;
                    break;

                default:
                    Complete(e, EffectState.Done, null);
                    await _store.SaveAsync(ct);
                    i++;
                    break;
            }
        }

        return true;
    }

    /// <summary>Junta as mensagens de um passo numa só (as opções viram uma lista numerada, o mesmo para qualquer canal) e a registra no Conversations com as cercas. Devolve falso se a instância não pode continuar.</summary>
    private async Task<bool> SendGroupAsync(FlowInstance inst, List<FlowEffectRecord> grupo, bool cobre, CancellationToken ct)
    {
        var partes = new List<string>();
        IReadOnlyList<FlowChoice>? opcoes = null;
        var esperaTextoLivre = false;
        foreach (var e in grupo)
        {
            var conteudo = ReadPayload(e);
            if (!string.IsNullOrWhiteSpace(conteudo.Text))
            {
                partes.Add(conteudo.Text!);
            }

            opcoes = conteudo.Choices is { Count: > 0 } ? conteudo.Choices : null;
            esperaTextoLivre |= conteudo.ExpectsFreeText;
        }

        var texto = string.Join("\n\n", partes);
        if (opcoes is not null)
        {
            texto += (texto.Length > 0 ? "\n" : string.Empty) + string.Join("\n", opcoes.Select((o, n) => $"{n + 1}) {o.Label}"));
        }

        foreach (var e in grupo)
        {
            e.Start();
        }

        await _store.SaveAsync(ct);

        if (string.IsNullOrWhiteSpace(texto))
        {
            foreach (var e in grupo)
            {
                Complete(e, EffectState.Done, "empty");
            }

            await _store.SaveAsync(ct);
            return true;
        }

        var turno = DeterministicGuid(inst.Id, grupo[0].Key);
        var resultado = RegisterReplyOutcome.StaleInput;
        for (var tentativa = 0; tentativa <= _options.StaleRetries; tentativa++)
        {
            var abertura = await _turns.BeginTurnAsync(new BeginTurnCommand(inst.SessionId, turno, 0), ct);
            if (abertura.Outcome != BeginTurnOutcome.Granted)
            {
                await SupersedeAsync(grupo, "mode-" + abertura.Outcome.ToString().ToLowerInvariant(), ct);
                await CancelAsync(inst, abertura.Outcome == BeginTurnOutcome.SessionClosed ? "session-closed" : "not-ai-responder", release: false, ct);
                return false;
            }

            var registro = await _flowCommands.RegisterFlowMessageAsync(
                new RegisterFlowMessageCommand(inst.SessionId, turno, abertura.Grant!.Epoch, abertura.Grant.StateVersion, abertura.Snapshot!.LastInboundSeq, abertura.Snapshot.ConversationId, texto, cobre, esperaTextoLivre), ct);
            resultado = registro.Outcome;
            if (resultado is RegisterReplyOutcome.Accepted or RegisterReplyOutcome.AlreadyRegistered)
            {
                break;
            }

            if (resultado is not (RegisterReplyOutcome.StaleInput or RegisterReplyOutcome.TurnNotActive))
            {
                break;
            }
        }

        switch (resultado)
        {
            case RegisterReplyOutcome.Accepted:
            case RegisterReplyOutcome.AlreadyRegistered:
                foreach (var e in grupo)
                {
                    Complete(e, EffectState.Done, resultado == RegisterReplyOutcome.Accepted ? "sent" : "already");
                }

                await _store.SaveAsync(ct);
                return true;

            case RegisterReplyOutcome.OptOutCommand:
                // O cliente pediu a saída (comando confirmado): a mensagem preparada NÃO é enviada e o fluxo é cancelado sem falha; a sessão volta ao agente e uma mensagem comum depois é atendida normalmente.
                await SupersedeAsync(grupo, "opt-out-command", ct);
                await CancelAsync(inst, "opt-out-command", release: true, ct);
                return false;

            case RegisterReplyOutcome.NotAiResponder:
            case RegisterReplyOutcome.StaleState:
            case RegisterReplyOutcome.SessionNotFound:
                // Um atendente assumiu (ou a sessão acabou): nada mais é enviado.
                await SupersedeAsync(grupo, "human-takeover", ct);
                await CancelAsync(inst, resultado == RegisterReplyOutcome.SessionNotFound ? "session-missing" : "human-takeover", release: false, ct);
                return false;

            default:
                // Defasagem esgotada ou forma recusada: nada foi enviado nem duplicado; a política de falha da ativação decide.
                await SupersedeAsync(grupo, "stale-exhausted", ct);
                await FailAsync(inst, resultado == RegisterReplyOutcome.ShapeNotAllowed ? "message-rejected" : "stale-input", ct);
                return false;
        }
    }

    private async Task SupersedeAsync(IEnumerable<FlowEffectRecord> efeitos, string codigo, CancellationToken ct)
    {
        foreach (var e in efeitos.Where(e => e.IsOpen))
        {
            Complete(e, EffectState.Superseded, codigo);
        }

        await _store.SaveAsync(ct);
    }

    private async Task<bool> ExecuteCapabilityAsync(FlowInstance inst, FlowGraph graph, FlowEffectRecord e, CancellationToken ct)
    {
        FlowInput entrada;
        if (e.State == EffectState.InFlight)
        {
            // O processo caiu depois de a chamada sair: o resultado é ambíguo e NUNCA é reenviado (segue a porta de falha).
            Complete(e, EffectState.Unknown, "outcome-unknown");
            entrada = new CapabilityResultInput(e.Key, false, ErrorCode: "outcome-unknown");
        }
        else
        {
            e.Start();
            await _store.SaveAsync(ct);
            var (sucesso, campos, codigo) = await CallCapabilityAsync(inst, e, ct);
            var estado = (sucesso, codigo) switch
            {
                (true, _) => EffectState.Done,
                (false, "outcome-unknown") => EffectState.Unknown,
                _ => EffectState.Failed,
            };
            Complete(e, estado, codigo);
            entrada = new CapabilityResultInput(e.Key, sucesso, campos, codigo);
        }

        return await ApplyResultAsync(inst, graph, entrada, ct);
    }

    private async Task<(bool Success, Dictionary<string, string>? Fields, string? Code)> CallCapabilityAsync(FlowInstance inst, FlowEffectRecord e, CancellationToken ct)
    {
        if (_capabilities is null)
        {
            return (false, null, "module-disabled");
        }

        var conteudo = ReadPayload(e);
        var chave = conteudo.OperationKey ?? string.Empty;
        var iniciouConsulta = false;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(_options.CapabilityBudget);

            // Os valores vêm das variáveis do fluxo (texto): o tipo declarado pela operação (inteiro, número, booleano) é aplicado aqui; o motor valida DE NOVO contra o esquema.
            var declarados = _catalog is null ? null : (await _catalog.GetAsync(chave, CapabilityCaller.Workflow, limite.Token, inst.OrganizationId))?.Parameters.ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
            if (_catalog is not null && declarados is null)
            {
                return (false, null, "operation-unavailable");
            }

            var parametros = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (nome, valor) in conteudo.Parameters ?? [])
            {
                parametros[nome] = ToJson(valor, declarados is not null && declarados.TryGetValue(nome, out var tipo) ? tipo : CapabilityParameterType.String);
            }

            iniciouConsulta = true;
            var execucao = await _capabilities.ExecuteAsync(
                new CapabilityInvocation(chave, parametros, CapabilityCaller.Workflow, null, inst.SessionId, $"wf-{inst.Id:N}-{e.Step}", inst.OrganizationId), limite.Token);
            if (execucao.Status == CapabilityExecutionStatus.Succeeded)
            {
                return (true, Flatten(execucao.Output), null);
            }

            var codigo = execucao.Error?.Code;
            var incerto = execucao.Status == CapabilityExecutionStatus.Unknown || codigo == "timeout"
                || (execucao.Status == CapabilityExecutionStatus.Cancelled && limite.IsCancellationRequested);
            return (false, null, incerto ? "outcome-unknown" : codigo ?? "failed");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null, iniciouConsulta ? "outcome-unknown" : "timeout");
        }
        catch (AccessDeniedException)
        {
            return (false, null, "caller-not-allowed");
        }
        catch (WorkflowsUnavailableException)
        {
            return (false, null, "unavailable");
        }
    }

    private static JsonElement ToJson(string valor, CapabilityParameterType tipo)
    {
        object? objeto = tipo switch
        {
            CapabilityParameterType.Integer when long.TryParse(valor, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var i) => i,
            CapabilityParameterType.Number when decimal.TryParse(valor, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            CapabilityParameterType.Boolean when bool.TryParse(valor, out var b) => b,
            _ => valor,
        };
        return JsonSerializer.SerializeToElement(objeto);
    }

    /// <summary>Os campos escalares do resultado do motor viram variáveis (texto). Listas e objetos aninhados não entram, só a contagem de uma lista (<c>count</c>).</summary>
    private Dictionary<string, string> Flatten(JsonElement? saida)
    {
        var campos = new Dictionary<string, string>(StringComparer.Ordinal);
        if (saida is not { ValueKind: JsonValueKind.Object } objeto)
        {
            return campos;
        }

        foreach (var p in objeto.EnumerateObject())
        {
            if (campos.Count >= 50)
            {
                break;
            }

            switch (p.Value.ValueKind)
            {
                case JsonValueKind.String:
                    campos[p.Name] = Clip(p.Value.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    campos[p.Name] = p.Value.GetRawText();
                    break;
                case JsonValueKind.Array when p.Name == "items":
                    campos["count"] = p.Value.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        return campos;
    }

    private string Clip(string valor) => valor.Length <= _options.Limits.MaxVariableLength ? valor : valor[.._options.Limits.MaxVariableLength];

    private async Task<bool> ExecuteKnowledgeAsync(FlowInstance inst, FlowGraph graph, FlowEffectRecord e, CancellationToken ct)
    {
        e.Start();
        await _store.SaveAsync(ct);
        var conteudo = ReadPayload(e);
        FlowKnowledgeResult resultado;
        try
        {
            resultado = _knowledge is null
                ? new FlowKnowledgeResult(false, null)
                : await _knowledge.LookupAsync(new FlowKnowledgeQuery(inst.OrganizationId, conteudo.Query ?? string.Empty, conteudo.SourceKey), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha na busca: o conhecimento "não achou" (o fluxo segue pela porta de erro do próprio desenho).
            resultado = new FlowKnowledgeResult(false, null);
        }

        Complete(e, EffectState.Done, resultado.Found ? "found" : "not-found");
        return await ApplyResultAsync(inst, graph, new KnowledgeResultInput(e.Key, resultado.Found, resultado.Answer), ct);
    }

    /// <summary>Aplica o resultado de um efeito assíncrono ao interpretador e grava a transição (com o efeito já concluído, na mesma gravação).</summary>
    private async Task<bool> ApplyResultAsync(FlowInstance inst, FlowGraph graph, FlowInput entrada, CancellationToken ct)
    {
        var r = _interpreter.Advance(graph, inst.ToState(ReadVariables(inst)), entrada, Now);
        if (r.Ignored)
        {
            await _store.SaveAsync(ct);
            return false;
        }

        await PersistAsync(inst, r, inst.SeenMessageSeq, ct);
        return true;
    }

    private async Task<string?> CheckHandoffAsync(FlowInstance inst, FlowEffectRecord e, CancellationToken ct)
    {
        if (_handoff is null)
        {
            return "unavailable";
        }

        try
        {
            var conteudo = ReadPayload(e);
            var r = await _handoff.RequestAsync(new FlowHandoffRequest(inst.SessionId, DeterministicGuid(inst.Id, e.Key), conteudo.ReasonCode ?? "flow_handoff", CheckOnly: true), ct);
            return r.Outcome is FlowHandoffOutcome.Routable or FlowHandoffOutcome.AlreadyApplied or FlowHandoffOutcome.Completed ? null : r.Code ?? r.Outcome.ToString().ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "unavailable";
        }
    }

    private async Task ExecuteHandoffAsync(FlowInstance inst, FlowEffectRecord e, CancellationToken ct)
    {
        e.Start();
        await _store.SaveAsync(ct);
        var conteudo = ReadPayload(e);
        FlowHandoffResult? r = null;
        try
        {
            if (_handoff is not null)
            {
                r = await _handoff.RequestAsync(new FlowHandoffRequest(inst.SessionId, DeterministicGuid(inst.Id, e.Key), conteudo.ReasonCode ?? "flow_handoff", CheckOnly: false), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            r = null;
        }

        if (r is { Outcome: FlowHandoffOutcome.Completed or FlowHandoffOutcome.AlreadyApplied })
        {
            Complete(e, EffectState.Done, "handed-off");
            await _store.SaveAsync(ct);
            return;
        }

        Complete(e, EffectState.Failed, r?.Code ?? "handoff-failed");
        await _store.SaveAsync(ct);
        // A instância já está em "transferida" (o interpretador a deu por terminada); como a transferência falhou, a política de falha devolve a conversa ao agente.
        inst.MarkFailed("handoff-failed", Now);
        await _store.SaveAsync(ct);
    }

    // ------------------------------------------------------------------ fim de vida

    private async Task<FlowRunOutcome> FinalizeAsync(FlowInstance inst, CancellationToken ct)
    {
        // MINIMIZAÇÃO: encerrada a instância, as variáveis e o conteúdo dos efeitos (que podem ter dado pessoal do cliente) deixam de existir; ficam só o caminho, os estados e os códigos.
        var vazio = _cipher.Protect("{}", Tenant, inst.Id, FlowPurposes.Variables);
        inst.WipeVariables(vazio.Ciphertext, vazio.KeyId, Now);
        await _store.SaveAsync(ct);
        await _store.WipeEffectPayloadsAsync(inst.Id, ct);
        WorkflowsMetrics.InstanceEnded(inst.Status, Now - inst.CreatedAt);

        var decisao = await _store.GetDecisionAsync(inst.SessionId, ct);
        if (decisao is { Owner: AutomationOwner.Flow })
        {
            if (inst.Status == FlowStatus.Failed)
            {
                await ApplyFailurePolicyAsync(inst, ct);
            }

            decisao.ReleaseToAgent(Now);
        }

        inst.ParkUntil(null, Now);
        await _store.SaveAsync(ct);
        return FlowRunOutcome.Finished;
    }

    private async Task FailAsync(FlowInstance inst, string codigo, CancellationToken ct)
    {
        foreach (var e in await _store.ListOpenEffectsAsync(inst.Id, ct))
        {
            Complete(e, EffectState.Superseded, "failed");
        }

        inst.MarkFailed(codigo, Now);
        await _store.SaveAsync(ct);
        await FinalizeAsync(inst, ct);
    }

    /// <summary>A política da ativação: transferir a um atendente (depois de checar que as regras a aceitam) ou devolver ao agente. Idempotente (mesma chave).</summary>
    private async Task ApplyFailurePolicyAsync(FlowInstance inst, CancellationToken ct)
    {
        if (inst.OnFailure == OnFailurePolicy.Handoff && _handoff is not null)
        {
            try
            {
                var r = await _handoff.RequestAsync(new FlowHandoffRequest(inst.SessionId, DeterministicGuid(inst.Id, "failure"), "flow_failed", CheckOnly: false), ct);
                if (r.Outcome is FlowHandoffOutcome.Completed or FlowHandoffOutcome.AlreadyApplied)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cai no retorno ao agente abaixo.
            }
        }

        await ReleaseToAgentAsync(inst, "failed", ct);
    }

    private async Task ReleaseToAgentAsync(FlowInstance inst, string motivo, CancellationToken ct)
    {
        var decisao = await _store.GetDecisionAsync(inst.SessionId, ct);
        decisao?.ReleaseToAgent(Now);
        await _store.SaveAsync(ct);
        await _flowCommands.ReleaseSessionToAgentAsync(new ReleaseFlowCommand(inst.SessionId, motivo), ct);
    }

    /// <summary>Cancela a instância (atendente assumiu, sessão encerrada): os efeitos abertos ficam "substituídos" (nada é enviado) e a conversa passa ao agente em definitivo.</summary>
    private async Task CancelAsync(FlowInstance inst, string motivo, bool release, CancellationToken ct)
    {
        foreach (var e in await _store.ListOpenEffectsAsync(inst.Id, ct))
        {
            Complete(e, EffectState.Superseded, motivo);
        }

        inst.MarkCancelled(motivo, Now);
        var decisao = await _store.GetDecisionAsync(inst.SessionId, ct);
        decisao?.ReleaseToAgent(Now);
        await _store.SaveAsync(ct);
        if (release)
        {
            await _flowCommands.ReleaseSessionToAgentAsync(new ReleaseFlowCommand(inst.SessionId, "cancelled"), ct);
        }
    }

    // ------------------------------------------------------------------ apoio

    private async Task<FlowGraph?> LoadGraphAsync(FlowInstance inst, CancellationToken ct)
    {
        var versao = await _versions.FindAsync(inst.WorkflowVersionId, ct);
        return versao is not null && FlowGraphSerializer.TryParse(versao.GraphJson, _options.Limits, out var grafo, out _) ? grafo : null;
    }

    private Dictionary<string, string> ReadVariables(FlowInstance inst) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(_cipher.Unprotect(new FlowCipherText(inst.VariablesKeyId, inst.VariablesCipher), Tenant, inst.Id, FlowPurposes.Variables)) ?? new Dictionary<string, string>();

    /// <summary>O identificador determinístico de uma intenção (instância e chave): a mesma intenção, o mesmo identificador, em qualquer processo, antes e depois de uma queda.</summary>
    internal static Guid DeterministicGuid(Guid instanceId, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"ia.bimeta/workflows/{instanceId:N}/{key}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
