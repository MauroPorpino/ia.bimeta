using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>Resultado de registrar uma mensagem do cliente na sessão: as duas sequências e a marca de chegada tardia.</summary>
public readonly record struct InboundRegistration(long MessageSeq, long InboundSeq, bool LateArrival);

/// <summary>
/// Sessão de atendimento (atendimento lógico). É a linha em que o Conversations serializa tudo o que importa: o contador
/// <c>InboundSeq</c>, a cobertura <c>AnsweredThroughSeq</c>, o turno logicamente ativo (uma coluna: no máximo um por
/// construção), a <c>StateVersion</c> do estado de atendimento e a época do turno. As cercas de resposta do ADR 0007 são
/// métodos puros daqui: não dependem de banco nem de relógio, então se provam em testes de unidade.
/// Quem muda estes campos é só o Conversations (ARQ-22).
/// </summary>
public sealed class ServiceSession : ITenantOwned
{
    private ServiceSession()
    {
        TenantId = null!;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>A unidade responsável (matriz ou filial). Muda só por <see cref="TransferTo"/>, que exige autorização e deixa rastro.</summary>
    public Guid? BusinessUnitId { get; private set; }

    /// <summary>O setor responsável, quando há. Sempre pertence à <see cref="BusinessUnitId"/>.</summary>
    public Guid? DepartmentId { get; private set; }

    public Guid ContactId { get; private set; }

    public ChannelInstanceId ChannelInstanceId { get; private set; }

    public SessionStatus Status { get; private set; }

    public HandlingMode HandlingMode { get; private set; }

    /// <summary>O atendente, no modo humano. Nulo no modo de IA, e nulo no modo humano significa atendimento pendente na fila.</summary>
    public Guid? ResponderRef { get; private set; }

    /// <summary>
    /// Versão do estado de atendimento (token de fencing): cresce a cada transição de modo, mudança de idioma da conversa,
    /// transferência entre unidades e ao encerrar.
    /// </summary>
    public long StateVersion { get; private set; }

    /// <summary>Ordem de ingestão de TODAS as mensagens da sessão (cliente e saída), base do histórico.</summary>
    public long LastMessageSeq { get; private set; }

    /// <summary>O contador de entrada do cliente (<c>InboundSeq</c>).</summary>
    public long LastInboundSeq { get; private set; }

    /// <summary>Cobertura LÓGICA: o maior <c>InboundSeq</c> coberto por resposta aceita. Nunca decresce e nunca depende de envio.</summary>
    public long AnsweredThroughSeq { get; private set; }

    public Guid? ActiveTurnId { get; private set; }

    public long TurnEpoch { get; private set; }

    public long LastLeaseToken { get; private set; }

    public DateTimeOffset? LastReplyAcceptedAt { get; private set; }

    public bool ReplyDeliveryFailed { get; private set; }

    /// <summary>O <c>AnsweredThroughSeq</c> da rajada já sinalizada: garante um só sinal por rajada.</summary>
    public long? AttentionRaisedForSeq { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastMessageAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>A IA está no comando: só neste estado ela abre turno e registra resposta. (<c>AiSupervised</c> não está especificado.)</summary>
    public bool IsAiResponder => HandlingMode == HandlingMode.AiAutonomous;

    public bool IsOpen => Status == SessionStatus.Open;

    public static ServiceSession Create(
        TenantId tenantId,
        Guid organizationId,
        Guid? businessUnitId,
        Guid contactId,
        ChannelInstanceId channelInstanceId,
        DateTimeOffset now,
        Guid? departmentId = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (organizationId == Guid.Empty || contactId == Guid.Empty || channelInstanceId.IsEmpty)
        {
            throw new ConversationsValidationException("A sessão precisa de organização, contato e canal.");
        }

        if (departmentId is not null && businessUnitId is null)
        {
            throw new ConversationsValidationException("Um setor só existe dentro de uma unidade.");
        }

        return new ServiceSession
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            BusinessUnitId = businessUnitId,
            DepartmentId = departmentId,
            ContactId = contactId,
            ChannelInstanceId = channelInstanceId,
            Status = SessionStatus.Open,
            HandlingMode = HandlingMode.AiAutonomous,
            StateVersion = 1,
            CreatedAt = now,
            LastMessageAt = now,
        };
    }

    // Entrada

    /// <summary>
    /// Registra uma mensagem do cliente: avança <c>InboundSeq</c> e a sequência do histórico. Deve ser chamado com a linha da
    /// sessão travada, o que faz da ordem uma ordem total mesmo com mensagens simultâneas (CON-2).
    /// </summary>
    public InboundRegistration RegisterInbound(DateTimeOffset providerTimestamp, DateTimeOffset receivedAt)
    {
        if (!IsOpen)
        {
            throw new ConversationsValidationException("A sessão está encerrada e não recebe mensagens.");
        }

        LastMessageSeq++;
        LastInboundSeq++;
        LastMessageAt = receivedAt;

        // ORD-6: chegou depois de uma resposta aceita, mas é de antes dela no provedor. Entra como pendente normal, marcada.
        var tardia = LastReplyAcceptedAt is { } resposta && providerTimestamp < resposta;
        return new InboundRegistration(LastMessageSeq, LastInboundSeq, tardia);
    }

    /// <summary>Avança a sequência do histórico para uma mensagem de saída.</summary>
    public long NextOutboundSeq(DateTimeOffset now)
    {
        LastMessageSeq++;
        LastMessageAt = now;
        return LastMessageSeq;
    }

    // Turno logicamente ativo

    /// <summary>
    /// <c>BeginTurn</c>: comparar-e-trocar atômico (a linha está travada). Exige sessão aberta, a IA no comando e token de
    /// lane não menor que o maior já visto. Assume o turno ativo e avança a época: qualquer turno anterior deixa de ser
    /// ativo neste mesmo instante.
    /// </summary>
    public BeginTurnOutcome BeginTurn(Guid turnId, long leaseToken, out TurnGrant? grant)
    {
        grant = null;

        if (!IsOpen)
        {
            return BeginTurnOutcome.SessionClosed;
        }

        if (!IsAiResponder)
        {
            return BeginTurnOutcome.NotAiResponder;
        }

        if (leaseToken < LastLeaseToken)
        {
            return BeginTurnOutcome.LeaseTokenStale;
        }

        ActiveTurnId = turnId;
        TurnEpoch++;
        LastLeaseToken = leaseToken;
        grant = new TurnGrant(TurnEpoch, LastInboundSeq, StateVersion);
        return BeginTurnOutcome.Granted;
    }

    /// <summary><c>EndTurn</c>: libera o turno só se ainda for este turno e esta época. Devolve se liberou.</summary>
    public bool EndTurn(Guid turnId, long epoch)
    {
        if (ActiveTurnId != turnId || TurnEpoch != epoch)
        {
            return false;
        }

        ActiveTurnId = null;
        return true;
    }

    /// <summary><c>CancelTurn</c>: limpa o turno ativo e avança a época, de modo que a resposta de qualquer turno em andamento seja recusada.</summary>
    public bool CancelTurn()
    {
        var havia = ActiveTurnId is not null;
        ActiveTurnId = null;
        TurnEpoch++;
        return havia;
    }

    // Cercas de resposta (CONVERSATION-LIFECYCLE.md §6.1, passos 2 a 4)

    /// <summary>
    /// Valida as cercas na ordem do ADR 0007: turno ativo e época; responsável e versão do estado; cerca de entrada.
    /// Devolve <c>null</c> se todas passam. A resposta defasada é SEMPRE recusada: não há parâmetro nem configuração que
    /// altere isso, e nada que o Agents declare muda o resultado.
    /// </summary>
    public RegisterReplyOutcome? CheckReplyFences(Guid turnId, long epoch, long expectedStateVersion, long basedOnInboundSeq)
    {
        if (ActiveTurnId != turnId || TurnEpoch != epoch)
        {
            return RegisterReplyOutcome.TurnNotActive;
        }

        if (!IsOpen || !IsAiResponder)
        {
            return RegisterReplyOutcome.NotAiResponder;
        }

        if (StateVersion != expectedStateVersion)
        {
            return RegisterReplyOutcome.StaleState;
        }

        if (basedOnInboundSeq != LastInboundSeq)
        {
            return RegisterReplyOutcome.StaleInput;
        }

        return null;
    }

    /// <summary>
    /// Aceita a resposta (passo 6): cobre logicamente até <paramref name="basedOnInboundSeq"/> (nunca decresce), limpa o
    /// turno ativo e registra o instante, que serve à marca de chegada tardia. Chamado só depois de <see cref="CheckReplyFences"/>.
    /// </summary>
    public void AcceptAiReply(long basedOnInboundSeq, DateTimeOffset now)
    {
        AnsweredThroughSeq = Math.Max(AnsweredThroughSeq, basedOnInboundSeq);
        ActiveTurnId = null;
        LastReplyAcceptedAt = now;
    }

    // Estado de atendimento

    /// <summary>
    /// Transição de modo. Exige sessão aberta, modo suportado, combinação válida e a versão esperada. Não toca no turno ativo:
    /// a nova <c>StateVersion</c> e o novo responsável fazem a resposta do turno em andamento ser recusada.
    /// </summary>
    public TransitionOutcome Transition(HandlingMode to, Guid? responderRef, long expectedStateVersion)
    {
        if (!IsOpen)
        {
            return TransitionOutcome.SessionClosed;
        }

        if (!Enum.IsDefined(to))
        {
            return TransitionOutcome.InvalidTransition;
        }

        if (to == HandlingMode.AiSupervised)
        {
            return TransitionOutcome.ModeNotSupported;
        }

        if ((to == HandlingMode.AiAutonomous && responderRef is not null) || responderRef == Guid.Empty)
        {
            return TransitionOutcome.InvalidTransition;
        }

        if (expectedStateVersion != StateVersion)
        {
            return TransitionOutcome.StaleState;
        }

        if (to == HandlingMode && responderRef == ResponderRef)
        {
            return TransitionOutcome.InvalidTransition;
        }

        HandlingMode = to;
        ResponderRef = responderRef;
        StateVersion++;
        return TransitionOutcome.Applied;
    }

    /// <summary>
    /// Avança a versão do estado por uma mudança que a resposta em andamento precisa enxergar (hoje, o idioma efetivo da
    /// conversa): a resposta escrita no estado anterior passa a ser recusada como <c>StaleState</c>.
    /// </summary>
    public void AdvanceStateVersion()
    {
        StateVersion++;
    }

    /// <summary>
    /// Passa a responsabilidade para outra unidade (e setor, se houver) da MESMA organização. Avança a <c>StateVersion</c>:
    /// o turno de IA em andamento, montado para a unidade anterior, tem a resposta recusada. Devolve <c>false</c> se a
    /// unidade e o setor já eram esses. A autorização e a validação da unidade e do setor são do caso de uso.
    /// </summary>
    public bool TransferTo(Guid businessUnitId, Guid? departmentId)
    {
        if (!IsOpen)
        {
            throw new ConversationsValidationException("A sessão está encerrada e não pode ser transferida.");
        }

        if (businessUnitId == Guid.Empty || departmentId == Guid.Empty)
        {
            throw new ConversationsValidationException("A unidade e o setor de destino precisam de identificador.");
        }

        if (BusinessUnitId == businessUnitId && DepartmentId == departmentId)
        {
            return false;
        }

        BusinessUnitId = businessUnitId;
        DepartmentId = departmentId;
        StateVersion++;
        return true;
    }

    /// <summary>Encerra a sessão. A versão do estado avança, e as respostas pendentes passam a ser recusadas.</summary>
    public bool Close(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return false;
        }

        Status = SessionStatus.Closed;
        ClosedAt = now;
        StateVersion++;
        return true;
    }

    // Sinais

    /// <summary>Um sinal de atenção por rajada: a rajada termina quando uma resposta é aceita (o <c>AnsweredThroughSeq</c> muda).</summary>
    public RaiseAttentionOutcome RaiseAttention()
    {
        if (!IsOpen)
        {
            return RaiseAttentionOutcome.SessionClosed;
        }

        if (AttentionRaisedForSeq == AnsweredThroughSeq)
        {
            return RaiseAttentionOutcome.AlreadyRaised;
        }

        AttentionRaisedForSeq = AnsweredThroughSeq;
        return RaiseAttentionOutcome.Raised;
    }

    public void MarkReplyDeliveryFailed() => ReplyDeliveryFailed = true;

    public void ClearReplyDeliveryFailed() => ReplyDeliveryFailed = false;
}
