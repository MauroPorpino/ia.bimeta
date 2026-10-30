using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Autoridade sobre o turno de IA e sobre a aceitação da resposta (ADR 0007). Toda decisão é do Conversations, numa única
/// transação com a linha da sessão travada, e não depende de nada que o Agents declare: a resposta defasada é SEMPRE
/// recusada por regra fixa, sem configuração, sem parâmetro de contrato e sem consultar o Audit. Só um contexto de sistema
/// chama (<see cref="Permission.SystemOperation"/>): um usuário autenticado nunca produz resposta de IA.
/// </summary>
public sealed class TurnService : ITurnCommands
{
    public const int MaxAiRequestIds = 50;

    private readonly IConversationStore _store;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public TurnService(IConversationStore store, IRequestContextAccessor context, TimeProvider clock)
    {
        _store = store;
        _context = context;
        _clock = clock;
    }

    public async Task<BeginTurnResult> BeginTurnAsync(BeginTurnCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _context.GetRequired().Require(Permission.SystemOperation);

        if (command.TurnId == Guid.Empty || command.LeaseToken < 0)
        {
            throw new ConversationsValidationException("O turno precisa de um identificador e de um token de lane não negativo.");
        }

        var limite = Math.Clamp(command.MaxSnapshotMessages, 1, ConversationLimits.MaxSnapshotMessages);

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null)
        {
            return new BeginTurnResult(BeginTurnOutcome.SessionNotFound, null, null);
        }

        var resultado = sessao.BeginTurn(command.TurnId, command.LeaseToken, out var grant);
        if (resultado != BeginTurnOutcome.Granted)
        {
            return new BeginTurnResult(resultado, null, null);
        }

        // O snapshot sai da MESMA leitura consistente do grant: a linha da sessão está travada, então nenhuma mensagem
        // nova entra entre o grant e o snapshot, e o basedOnInboundSeq descreve exatamente o que o modelo vai ver.
        var recentes = await trabalho.RecentMessagesAsync(sessao.Id, limite, cancellationToken);
        var ultimaSaida = await trabalho.LastOutboundAsync(sessao.Id, cancellationToken);

        // O idioma da resposta é o efetivo desta conversa (não o do tenant), lido na mesma leitura travada do grant: uma
        // mudança de idioma avança a StateVersion, então a resposta escrita no idioma anterior é recusada.
        var conversa = await trabalho.FindSessionConversationAsync(sessao, cancellationToken)
            ?? throw new InvalidOperationException("A sessão aberta não tem conversa.");
        var snapshot = new TurnSnapshot(
            sessao.Id,
            sessao.HandlingMode,
            sessao.StateVersion,
            sessao.LastInboundSeq,
            sessao.AnsweredThroughSeq,
            sessao.ReplyDeliveryFailed,
            ultimaSaida?.DeliveryState,
            recentes.Select(m => m.ToSnapshot(sessao.AnsweredThroughSeq)).ToList(),
            conversa.Id,
            conversa.EffectiveLocale,
            conversa.LocaleSource,
            sessao.ChannelInstanceId,
            sessao.BusinessUnitId,
            sessao.DepartmentId);

        await trabalho.CommitAsync(cancellationToken);
        return new BeginTurnResult(BeginTurnOutcome.Granted, grant, snapshot);
    }

    public async Task<EndTurnResult> EndTurnAsync(EndTurnCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _context.GetRequired().Require(Permission.SystemOperation);

        if (!Enum.IsDefined(command.Reason))
        {
            throw new ConversationsValidationException("O motivo do fim do turno é inválido.");
        }

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null)
        {
            return new EndTurnResult(EndTurnOutcome.SessionNotFound);
        }

        if (!sessao.EndTurn(command.TurnId, command.Epoch))
        {
            return new EndTurnResult(EndTurnOutcome.NotActive);
        }

        await trabalho.CommitAsync(cancellationToken);
        return new EndTurnResult(EndTurnOutcome.Ended);
    }

    public async Task<CancelTurnResult> CancelTurnAsync(CancelTurnCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _context.GetRequired().Require(Permission.SystemOperation);
        ConversationLimits.ValidateReason(command.Reason);

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null)
        {
            return new CancelTurnResult(false, false, 0);
        }

        var havia = sessao.CancelTurn();
        await trabalho.CommitAsync(cancellationToken);
        return new CancelTurnResult(true, havia, sessao.TurnEpoch);
    }

    public async Task<RegisterAiReplyResult> RegisterAiReplyAsync(RegisterAiReplyCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        // Tudo numa única transação, com a linha da sessão travada: o comparar-e-trocar das cercas não tem corrida (CON-1).
        // A sessão é buscada por (tenant, id): uma sessão de outro tenant é "não encontrada", e nada é gravado.
        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null)
        {
            return new RegisterAiReplyResult(RegisterReplyOutcome.SessionNotFound, null, null, null);
        }

        // 1. Idempotência: o mesmo TurnId já registrado devolve a resposta que existe.
        var existente = await trabalho.FindReplyByTurnAsync(sessao.Id, command.TurnId, cancellationToken);
        if (existente is not null)
        {
            return new RegisterAiReplyResult(RegisterReplyOutcome.AlreadyRegistered, existente.Id, sessao.AnsweredThroughSeq, sessao.LastInboundSeq);
        }

        // 2 a 4. Turno ativo e época; responsável e versão do estado; cerca de entrada. Regras fixas do domínio.
        var recusa = sessao.CheckReplyFences(command.TurnId, command.Epoch, command.ExpectedStateVersion, command.BasedOnInboundSeq);
        if (recusa is { } motivo)
        {
            return new RegisterAiReplyResult(motivo, null, sessao.AnsweredThroughSeq, sessao.LastInboundSeq);
        }

        // 5. Forma da resposta: tipo e conteúdo permitidos, e a conversa de destino pertence à sessão.
        if (!Enum.IsDefined(command.ReplyKind)
            || string.IsNullOrWhiteSpace(command.Text)
            || command.Text.Length > ConversationLimits.TextMaxLength
            || command.AiRequestIds is null
            || command.AiRequestIds.Count > MaxAiRequestIds)
        {
            return new RegisterAiReplyResult(RegisterReplyOutcome.ShapeNotAllowed, null, sessao.AnsweredThroughSeq, sessao.LastInboundSeq);
        }

        var conversa = await trabalho.FindConversationAsync(command.TargetConversationId, cancellationToken);
        if (conversa is null || conversa.SessionId != sessao.Id)
        {
            return new RegisterAiReplyResult(RegisterReplyOutcome.ShapeNotAllowed, null, sessao.AnsweredThroughSeq, sessao.LastInboundSeq);
        }

        // 6. Commit atômico: cobertura, turno ativo, mensagem de saída. Tudo ou nada.
        var agora = _clock.GetUtcNow();
        var sequencia = sessao.NextOutboundSeq(agora);
        var resposta = Message.CreateAiReply(
            contexto.TenantId, sessao.Id, conversa.Id, conversa.ChannelInstanceId, sequencia, command.TurnId, command.ReplyKind,
            command.BasedOnInboundSeq, command.Text, command.AiRequestIds, agora, conversa.EffectiveLocale);
        sessao.AcceptAiReply(command.BasedOnInboundSeq, agora);
        trabalho.Add(resposta);
        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.OutboundMessageRequested,
                    new OutboundMessageRequestedV1(sessao.Id, conversa.Id, resposta.Id, resposta.Id, null, resposta.ChannelInstanceId),
                    agora),
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.AiReplyAccepted,
                    new AiReplyAcceptedV1(sessao.Id, command.TurnId, resposta.Id, sessao.AnsweredThroughSeq),
                    agora),
            },
            cancellationToken);

        return new RegisterAiReplyResult(RegisterReplyOutcome.Accepted, resposta.Id, sessao.AnsweredThroughSeq, sessao.LastInboundSeq);
    }

    public async Task<RaiseSessionAttentionResult> RaiseSessionAttentionAsync(RaiseSessionAttentionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        if (!Enum.IsDefined(command.Reason) || command.MessagesInBurst < 0 || command.InterruptionsInBurst < 0)
        {
            throw new ConversationsValidationException("O sinal de atenção é inválido.");
        }

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null)
        {
            return new RaiseSessionAttentionResult(RaiseAttentionOutcome.SessionNotFound);
        }

        var resultado = sessao.RaiseAttention();
        if (resultado != RaiseAttentionOutcome.Raised)
        {
            return new RaiseSessionAttentionResult(resultado);
        }

        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.SessionAttentionRaised,
                    new SessionAttentionRaisedV1(sessao.Id, command.Reason, command.MessagesInBurst, command.InterruptionsInBurst),
                    _clock.GetUtcNow()),
            },
            cancellationToken);
        return new RaiseSessionAttentionResult(RaiseAttentionOutcome.Raised);
    }
}
