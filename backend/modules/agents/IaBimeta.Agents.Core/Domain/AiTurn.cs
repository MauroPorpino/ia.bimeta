using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Domain;

/// <summary>Os estados do turno de IA (CONVERSATION-LIFECYCLE.md §5). Os três primeiros são não terminais; o resto é terminal.</summary>
public enum AiTurnState : short
{
    /// <summary>Esperando o agrupamento (<c>fire_at</c>) ou o conteúdo pendente.</summary>
    Scheduled = 1,

    /// <summary>Pronto para o executor: depois de uma falha transitória, esperando o recuo (<c>next_attempt_at</c>).</summary>
    Ready = 2,

    /// <summary>Em poder de um executor (lease vigente).</summary>
    Running = 3,

    /// <summary>Terminou bem: a resposta foi aceita (T5) ou o atendimento humano foi pedido.</summary>
    Completed = 4,

    /// <summary>A resposta foi recusada pelas cercas, ou não havia o que responder (T7, T7b).</summary>
    Discarded = 5,

    /// <summary>Uma mensagem nova (ou mudança do estado) o trocou por um sucessor (T6).</summary>
    Superseded = 6,

    /// <summary>O responsável deixou de ser a IA, a sessão acabou, ou o turno foi cancelado (T8, T4b).</summary>
    Cancelled = 7,

    /// <summary>Falha permanente ou tentativas esgotadas (T10): aplica-se a política de falha.</summary>
    Dead = 8,
}

/// <summary>A transição pedida não existe na máquina do turno.</summary>
public sealed class InvalidAiTurnTransitionException : InvalidOperationException
{
    public InvalidAiTurnTransitionException(AiTurnState from, string operation)
        : base($"O turno em {from} não admite a operação {operation}.")
    {
    }
}

/// <summary>
/// O turno de IA (<c>AiTurn</c>): a unidade que o Agents agenda, agrupa, executa e encerra para UMA sessão de atendimento. É o dono do agrupamento, das tentativas e do cancelamento
/// cooperativo; o estado da conversa e a aceitação da resposta continuam sendo do Conversations. No máximo um turno não terminal por sessão (índice único parcial no banco). Toda mudança de
/// estado passa pelos métodos abaixo, que aplicam a tabela T1 a T12 e avançam <see cref="Version"/> (a concorrência otimista entre o handler dos eventos e o executor).
/// </summary>
public sealed class AiTurn : ITenantOwned
{
    private const int ReasonMaxLength = 100;

    private AiTurn()
    {
    }

    public TenantId TenantId { get; private set; } = null!;

    /// <summary>O <c>TurnId</c>: também a chave de idempotência do registro da resposta no Conversations e do pedido de handoff.</summary>
    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public AiTurnState State { get; private set; }

    /// <summary>A hora (relógio do servidor) da primeira mensagem do grupo.</summary>
    public DateTimeOffset FirstReceivedAt { get; private set; }

    /// <summary>A hora da última mensagem do grupo.</summary>
    public DateTimeOffset LastReceivedAt { get; private set; }

    /// <summary>Quando o turno fica devido, no estado <see cref="AiTurnState.Scheduled"/> (GRP-2).</summary>
    public DateTimeOffset FireAt { get; private set; }

    /// <summary>Quando o turno fica devido, no estado <see cref="AiTurnState.Ready"/> (o recuo depois de uma falha transitória).</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>As tentativas gastas por falha transitória, saída inválida ou lease perdido (T9). Esperar por capacidade ou por conteúdo não conta.</summary>
    public int Attempts { get; private set; }

    /// <summary>As interrupções da rajada sem resposta aceita (INT-1): o sucessor herda o contador, e uma resposta aceita o zera (novo turno começa em zero).</summary>
    public int SupersedeCount { get; private set; }

    /// <summary>Chegou mensagem com o turno em andamento e ele não é interrompível (T12): a resposta vai ser recusada pela cerca de entrada, e o sucessor cobre as pendentes.</summary>
    public bool InputPending { get; private set; }

    /// <summary>O token de lane deste executor, de uma sequência do banco sempre crescente: o <c>BeginTurn</c> recusa o menor que o já visto.</summary>
    public long? LaneToken { get; private set; }

    public string? ClaimedBy { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    /// <summary>Desde quando o turno espera conteúdo pendente (MED-2); a espera adicional por tipo conta daqui.</summary>
    public DateTimeOffset? ContentWaitStartedAt { get; private set; }

    /// <summary>O motivo do handoff já decidido (pela IA, pela política de conteúdo ou pela falha permanente): quem retoma o turno continua daqui, sem chamar o modelo de novo.</summary>
    public string? HandoffReason { get; private set; }

    /// <summary>O aviso de transferência já foi registrado (ele só pode sair uma vez: uma resposta por turno, MSG-1).</summary>
    public bool NoticeDone { get; private set; }

    /// <summary>O código curto de como o turno terminou. Nunca conteúdo de mensagem.</summary>
    public string? EndReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>O token de concorrência otimista: toda mudança o avança.</summary>
    public int Version { get; private set; }

    /// <summary>Uma cópia independente (o "banco" em memória dos testes entrega cópias, como uma leitura de verdade).</summary>
    internal AiTurn Clone() => (AiTurn)MemberwiseClone();

    public bool IsOpen => State is AiTurnState.Scheduled or AiTurnState.Ready or AiTurnState.Running;

    public bool IsTerminal => !IsOpen;

    /// <summary><c>fire_at = min(última + QuietWindow, primeira + MaxWait)</c> (GRP-2): relógio do servidor, nunca o do provedor.</summary>
    public static DateTimeOffset ComputeFireAt(DateTimeOffset firstReceivedAt, DateTimeOffset lastReceivedAt, AgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var quieta = lastReceivedAt + options.QuietWindow;
        var teto = firstReceivedAt + options.MaxWait;
        return quieta < teto ? quieta : teto;
    }

    /// <summary>T1: cria o turno <see cref="AiTurnState.Scheduled"/> para a sessão. Com <paramref name="immediate"/> ele já nasce devido (retorno à IA, CON-7, sucessor depois de recusa).</summary>
    public static AiTurn Schedule(
        TenantId tenant,
        Guid sessionId,
        DateTimeOffset firstReceivedAt,
        DateTimeOffset lastReceivedAt,
        AgentsOptions options,
        DateTimeOffset now,
        int supersedeCount = 0,
        bool immediate = false,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(options);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A sessão do turno é obrigatória.", nameof(sessionId));
        }

        return new AiTurn
        {
            TenantId = tenant,
            Id = id ?? Guid.NewGuid(),
            SessionId = sessionId,
            State = AiTurnState.Scheduled,
            FirstReceivedAt = firstReceivedAt,
            LastReceivedAt = lastReceivedAt,
            FireAt = immediate ? now : ComputeFireAt(firstReceivedAt, lastReceivedAt, options),
            SupersedeCount = supersedeCount,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>T2: mensagem nova com o turno ainda agendado: recalcula <c>fire_at</c>, sem passar do teto desde a primeira (GRP-1).</summary>
    public void Extend(DateTimeOffset receivedAt, AgentsOptions options, DateTimeOffset now)
    {
        Require(State == AiTurnState.Scheduled, nameof(Extend));
        if (receivedAt > LastReceivedAt)
        {
            LastReceivedAt = receivedAt;
        }

        if (receivedAt < FirstReceivedAt)
        {
            FirstReceivedAt = receivedAt;
        }

        FireAt = ComputeFireAt(FirstReceivedAt, LastReceivedAt, options);
        Touch(now);
    }

    /// <summary>O conteúdo pendente ficou pronto (ou falhou), ou o responsável voltou à IA: o turno que espera fica devido já. Em outro estado não faz nada.</summary>
    public bool Wake(DateTimeOffset now)
    {
        if (State != AiTurnState.Scheduled || FireAt <= now)
        {
            return false;
        }

        FireAt = now;
        Touch(now);
        return true;
    }

    /// <summary>
    /// T6: mensagem nova com o turno em andamento e dentro do limite de interrupções. Este turno passa a <see cref="AiTurnState.Superseded"/> e devolve o sucessor <see cref="AiTurnState.Scheduled"/> (a
    /// janela quieta recomeça na mensagem nova). O chamador grava os dois na MESMA transação. <paramref name="countsAsInterruption"/> é falso para a mudança de estado (unidade, idioma), que não é
    /// rajada do cliente.
    /// </summary>
    public AiTurn Supersede(DateTimeOffset receivedAt, AgentsOptions options, DateTimeOffset now, bool countsAsInterruption = true, bool immediate = false)
    {
        Require(State == AiTurnState.Running, nameof(Supersede));
        End(AiTurnState.Superseded, "superseded", now);
        return Schedule(TenantId, SessionId, receivedAt, receivedAt, options, now, SupersedeCount + (countsAsInterruption ? 1 : 0), immediate);
    }

    /// <summary>T12: mensagem nova com o turno em andamento que não pode ser interrompido. Ao terminar, a resposta é recusada pela cerca de entrada.</summary>
    public void MarkInputPending(DateTimeOffset now)
    {
        Require(State == AiTurnState.Running, nameof(MarkInputPending));
        if (InputPending)
        {
            return;
        }

        InputPending = true;
        Touch(now);
    }

    /// <summary>O turno está devido: <see cref="AiTurnState.Scheduled"/> com <c>fire_at</c> vencido, <see cref="AiTurnState.Ready"/> com o recuo vencido, ou <see cref="AiTurnState.Running"/> com o lease vencido.</summary>
    public bool IsDue(DateTimeOffset now) => State switch
    {
        AiTurnState.Scheduled => FireAt <= now,
        AiTurnState.Ready => (NextAttemptAt ?? FireAt) <= now,
        AiTurnState.Running => LeaseExpiresAt is { } fim && fim <= now,
        _ => false,
    };

    /// <summary>
    /// T4 (lado do Agents): um executor assume o turno devido, com o token de lane e o lease. Reivindicar um turno que estava em <see cref="AiTurnState.Running"/> (lease vencido: o processo caiu) conta uma
    /// tentativa (T9, lease perdido).
    /// </summary>
    public void Claim(long laneToken, string owner, DateTimeOffset now, TimeSpan lease)
    {
        Require(IsOpen, nameof(Claim));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (State == AiTurnState.Running)
        {
            Attempts++;
        }

        State = AiTurnState.Running;
        LaneToken = laneToken;
        ClaimedBy = owner.Length > 200 ? owner[..200] : owner;
        LeaseExpiresAt = now + lease;
        NextAttemptAt = null;
        InputPending = false;
        Touch(now);
    }

    /// <summary>O grupo ainda tem conteúdo pendente dentro da espera adicional (T3): o turno volta a esperar, sem gastar tentativa, e o modelo não é chamado.</summary>
    public void DeferForContent(DateTimeOffset now, TimeSpan recheck)
    {
        Require(State == AiTurnState.Running, nameof(DeferForContent));
        State = AiTurnState.Scheduled;
        FireAt = now + recheck;
        ContentWaitStartedAt ??= now;
        NextAttemptAt = null;
        ReleaseClaim();
        Touch(now);
    }

    /// <summary>T9 e T11: o turno volta a <see cref="AiTurnState.Ready"/> depois de <paramref name="delay"/>. Só a falha transitória conta tentativa (<paramref name="countAttempt"/>).</summary>
    public void ReleaseForRetry(DateTimeOffset now, TimeSpan delay, bool countAttempt)
    {
        Require(State == AiTurnState.Running, nameof(ReleaseForRetry));
        State = AiTurnState.Ready;
        NextAttemptAt = now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        if (countAttempt)
        {
            Attempts++;
        }

        ReleaseClaim();
        Touch(now);
    }

    /// <summary>Guarda o handoff decidido: se o processo cair daqui para a frente, quem retoma só repete o pedido (idempotente pelo <c>TurnId</c>), sem nova chamada ao modelo.</summary>
    public void RecordHandoff(string reason, DateTimeOffset now)
    {
        Require(State == AiTurnState.Running, nameof(RecordHandoff));
        if (HandoffReason is not null)
        {
            return;
        }

        HandoffReason = Clamp(reason);
        Touch(now);
    }

    public void RecordNoticeDone(DateTimeOffset now)
    {
        Require(State == AiTurnState.Running, nameof(RecordNoticeDone));
        if (NoticeDone)
        {
            return;
        }

        NoticeDone = true;
        Touch(now);
    }

    /// <summary>T5: a resposta foi aceita (ou o atendimento humano foi pedido com sucesso).</summary>
    public void Complete(DateTimeOffset now, string reason)
    {
        Require(State == AiTurnState.Running, nameof(Complete));
        End(AiTurnState.Completed, reason, now);
    }

    /// <summary>T7 e T7b: a resposta foi recusada pelas cercas, ou não havia o que responder.</summary>
    public void Discard(DateTimeOffset now, string reason)
    {
        Require(State == AiTurnState.Running, nameof(Discard));
        End(AiTurnState.Discarded, reason, now);
    }

    /// <summary>T4b e T8: a IA deixou de ser a responsável, a sessão acabou, ou o turno foi cancelado. Vale em qualquer estado não terminal.</summary>
    public void Cancel(DateTimeOffset now, string reason)
    {
        Require(IsOpen, nameof(Cancel));
        End(AiTurnState.Cancelled, reason, now);
    }

    /// <summary>T10: falha permanente, ou tentativas esgotadas.</summary>
    public void Kill(DateTimeOffset now, string reason)
    {
        Require(State == AiTurnState.Running, nameof(Kill));
        End(AiTurnState.Dead, reason, now);
    }

    private void End(AiTurnState state, string reason, DateTimeOffset now)
    {
        State = state;
        EndReason = Clamp(reason);
        EndedAt = now;
        NextAttemptAt = null;
        ReleaseClaim();
        Touch(now);
    }

    private void ReleaseClaim()
    {
        LaneToken = null;
        ClaimedBy = null;
        LeaseExpiresAt = null;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private void Require(bool condition, string operation)
    {
        if (!condition)
        {
            throw new InvalidAiTurnTransitionException(State, operation);
        }
    }

    private static string Clamp(string reason)
    {
        var texto = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
        return texto.Length > ReasonMaxLength ? texto[..ReasonMaxLength] : texto;
    }
}
