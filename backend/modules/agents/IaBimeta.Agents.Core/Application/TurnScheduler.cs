using IaBimeta.Agents.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Application;

/// <summary>
/// Aplica os eventos do Conversations à máquina do turno (T1, T2, T6, T8, T12): só GRAVA o <see cref="AiTurn"/> no banco do módulo. Nunca chama o modelo, o Conversations nem o ContactCenter, e
/// nada aqui espera o provedor: o handler do relay não pode segurar a entrega dos outros tenants (ADR 0017, seção G.1). Quem executa o turno é o executor do Worker. O tenant vem do contexto
/// confiável do escopo da entrega, nunca do payload. Toda operação é idempotente por estado (a idempotência por <c>EventId</c> é do inbox do módulo, na mesma transação).
/// </summary>
public sealed class TurnScheduler
{
    private readonly IAiTurnStore _store;
    private readonly IRequestContextAccessor _context;
    private readonly AgentsOptions _options;
    private readonly TimeProvider _clock;

    public TurnScheduler(IAiTurnStore store, IRequestContextAccessor context, AgentsOptions options, TimeProvider clock)
    {
        _store = store;
        _context = context;
        _options = options;
        _clock = clock;
    }

    /// <summary>
    /// <c>InboundMessageRegisteredV1</c>. Sem turno aberto: T1 (cria o agendado). Agendado: T2 (recalcula o disparo). Pronto: nada (o snapshot do T4 já pega a mensagem, TRN-2). Em andamento: T6 (troca
    /// pelo sucessor, na mesma transação) se ainda cabe interromper, senão T12 (marca a entrada pendente: a resposta vai ser recusada e o sucessor cobre as pendentes).
    /// </summary>
    /// <param name="receivedAt">Quando o Conversations registrou a mensagem (relógio do servidor, GRP-2).</param>
    public async Task OnInboundMessageAsync(Guid sessionId, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;
        var agora = _clock.GetUtcNow();
        var recebida = receivedAt > agora ? agora : receivedAt;

        var aberto = await _store.FindOpenBySessionAsync(sessionId, cancellationToken);
        if (aberto is null)
        {
            _store.Add(AiTurn.Schedule(tenant, sessionId, recebida, recebida, _options, agora));
        }
        else
        {
            switch (aberto.State)
            {
                case AiTurnState.Scheduled:
                    aberto.Extend(recebida, _options, agora);
                    break;

                case AiTurnState.Running:
                    if (aberto.HandoffReason is null && aberto.SupersedeCount < _options.MaxSupersedesPerBurst)
                    {
                        _store.Add(aberto.Supersede(recebida, _options, agora));
                    }
                    else
                    {
                        aberto.MarkInputPending(agora);
                    }

                    break;
            }
        }

        await _store.SaveAsync(cancellationToken);
    }

    /// <summary><c>HandlingModeChangedV1</c> com a IA de volta ao comando (CON-7): se há pendentes, o turno dispara já. Sem pendentes o executor descobre no snapshot e encerra sem resposta.</summary>
    public async Task OnReturnedToAiAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;
        var agora = _clock.GetUtcNow();
        var aberto = await _store.FindOpenBySessionAsync(sessionId, cancellationToken);
        if (aberto is null)
        {
            _store.Add(AiTurn.Schedule(tenant, sessionId, agora, agora, _options, agora, immediate: true));
        }
        else if (!aberto.Wake(agora))
        {
            return;
        }

        await _store.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// T8: o responsável deixou de ser a IA, ou a sessão acabou. O turno aberto é cancelado, qualquer que seja o estado. Se ele estava em andamento, o executor perde a corrida ao gravar o fim, e a
    /// resposta em voo é recusada pelo Conversations (<c>TurnNotActive</c>, <c>StaleState</c>, <c>NotAiResponder</c>): nenhuma resposta sai depois de o humano assumir.
    /// </summary>
    public async Task OnLeftAiControlAsync(Guid sessionId, string reason, CancellationToken cancellationToken)
    {
        var aberto = await _store.FindOpenBySessionAsync(sessionId, cancellationToken);
        if (aberto is null)
        {
            return;
        }

        aberto.Cancel(_clock.GetUtcNow(), reason);
        await _store.SaveAsync(cancellationToken);
    }

    /// <summary><c>MessageContentReadyV1</c> e <c>MessageContentFailedV1</c>: o turno que espera conteúdo pendente fica devido já, para reavaliar o grupo sem esperar o recuo.</summary>
    public async Task OnContentChangedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var aberto = await _store.FindOpenBySessionAsync(sessionId, cancellationToken);
        if (aberto is null || !aberto.Wake(_clock.GetUtcNow()))
        {
            return;
        }

        await _store.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// <c>SessionTransferredV1</c> e <c>ConversationLocaleChangedV1</c>: o estado em que o turno foi montado mudou (unidade, idioma). O turno em andamento é trocado por um sucessor que dispara já (a
    /// resposta em voo seria recusada por <c>StaleState</c>); o agendado ou pronto lê o estado novo no snapshot. Não é rajada do cliente: não conta como interrupção.
    /// </summary>
    public async Task OnSessionStateChangedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var aberto = await _store.FindOpenBySessionAsync(sessionId, cancellationToken);
        if (aberto is not { State: AiTurnState.Running, HandoffReason: null })
        {
            return;
        }

        var agora = _clock.GetUtcNow();
        _store.Add(aberto.Supersede(agora, _options, agora, countsAsInterruption: false, immediate: true));
        await _store.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// O reconciliador (CON-15): a sessão tem a IA no comando e mensagens pendentes, mas nenhum turno aberto (evento perdido ou morto). Cria o turno devido já. Devolve se criou. Idempotente: com turno
    /// aberto, não faz nada.
    /// </summary>
    public async Task<bool> EnsureTurnForPendingAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var tenant = _context.GetRequired().TenantId;
        var aberto = await _store.FindOpenBySessionAsync(sessionId, cancellationToken);
        if (aberto is not null)
        {
            return false;
        }

        var agora = _clock.GetUtcNow();
        _store.Add(AiTurn.Schedule(tenant, sessionId, agora, agora, _options, agora, immediate: true));
        await _store.SaveAsync(cancellationToken);
        return true;
    }
}
