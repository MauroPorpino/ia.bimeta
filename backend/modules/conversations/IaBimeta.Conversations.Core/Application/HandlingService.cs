using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Estado de atendimento e recuperação de falha de envio. Quem decide se e para quem transferir é o ContactCenter; aqui o
/// Conversations só impõe a máquina de estados e a <c>StateVersion</c>. Contexto de sistema (o ContactCenter em processo)
/// passa; usuário autenticado precisa da permissão sobre a organização da sessão, e sem leitura a sessão responde como
/// inexistente (TEN-2).
/// </summary>
public sealed class HandlingService : IHandlingCommands
{
    private readonly IConversationStore _store;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;
    private readonly IBusinessUnitDirectory _units;
    private readonly IDepartmentDirectory _departments;
    private readonly IChannelUnitAuthorization _channelUnits;

    public HandlingService(
        IConversationStore store,
        IRequestContextAccessor context,
        TimeProvider clock,
        IAuditWriter audit,
        IBusinessUnitDirectory units,
        IDepartmentDirectory departments,
        IChannelUnitAuthorization channelUnits)
    {
        _store = store;
        _context = context;
        _clock = clock;
        _audit = audit;
        _units = units;
        _departments = departments;
        _channelUnits = channelUnits;
    }

    public async Task<TransitionResult> TransitionHandlingModeAsync(TransitionHandlingModeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        ConversationLimits.ValidateReason(command.Reason);
        if (command.TransitionId == Guid.Empty)
        {
            throw new ConversationsValidationException("A transição precisa de um identificador (chave de idempotência).");
        }

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            return new TransitionResult(TransitionOutcome.SessionNotFound, null);
        }

        contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

        // A cerca de quem executa vale ANTES de qualquer outra regra, dentro da trava da sessão: quem perdeu a exclusividade (outro
        // executor, com token maior, assumiu) não aplica nem repete nada, mesmo lendo o estado atual da sessão.
        if (command.FenceToken is { } cerca && (cerca <= 0 || sessao.IsFencedOut(cerca)))
        {
            return new TransitionResult(TransitionOutcome.FencedOut, sessao.StateVersion);
        }

        // Idempotência por TransitionId: repetir uma transição já tratada (aplicada, ou avaliada sem mudar nada) devolve o mesmo
        // resultado, sem mudar nada.
        var anterior = await trabalho.FindTransitionAsync(sessao.Id, command.TransitionId, cancellationToken);
        if (anterior is not null)
        {
            return new TransitionResult(TransitionOutcome.AlreadyApplied, anterior.StateVersionAfter, anterior.MessageSeqAt, anterior.HandlingPeriodAfter);
        }

        // A autorização de quem comanda foi dada sobre um estado (responsável e período): ela só vale para alterar ESTE estado, conferido aqui,
        // na mesma trava e transação da gravação. Renovar a versão esperada não a renova: se o responsável já é outro, nada é gravado. Um
        // comando que já encontra a sessão no estado pedido não altera nada e dispensa a condição (a repetição de um comando já aplicado foi
        // reconhecida acima, antes dela).
        if (command.Precondition is { } condicao
            && sessao.IsOpen
            && !(sessao.HandlingMode == command.To && sessao.ResponderRef == command.ResponderRef)
            && !sessao.IsInHandlingState(condicao.Mode, condicao.ResponderRef, condicao.HandlingPeriod, condicao.PlacementEpoch))
        {
            return new TransitionResult(TransitionOutcome.HandlingChanged, sessao.StateVersion);
        }

        var de = sessao.HandlingMode;
        var resultado = sessao.Transition(command.To, command.ResponderRef, command.ExpectedStateVersion);
        if (resultado != TransitionOutcome.Applied)
        {
            // A sessão JÁ estava no estado pedido (mesma versão esperada, mesmo modo e responsável): a operação não muda nada, mas o
            // identificador fica registrado, para que uma repetição tardia (de um executor atrasado) não seja reavaliada contra o estado
            // que a sessão tiver depois.
            if (resultado == TransitionOutcome.InvalidTransition && sessao.IsOpen && sessao.HandlingMode == command.To && sessao.ResponderRef == command.ResponderRef
                && command.ExpectedStateVersion == sessao.StateVersion)
            {
                // A fronteira fica DESCONHECIDA (nula), na primeira resposta e em toda repetição: quem mudou o estado antes foi outra
                // operação, em outro instante, e a sequência de agora não é a dessa mudança.
                AdvanceFence(sessao, command.FenceToken);
                trabalho.Add(HandlingTransition.Create(contexto.TenantId, sessao.Id, command.TransitionId, sessao.StateVersion, null, null, _clock.GetUtcNow()));
                await trabalho.CommitAsync(cancellationToken);
                return new TransitionResult(resultado, sessao.StateVersion);
            }

            return new TransitionResult(resultado, sessao.StateVersion);
        }

        var agora = _clock.GetUtcNow();
        AdvanceFence(sessao, command.FenceToken);
        trabalho.Add(HandlingTransition.Create(contexto.TenantId, sessao.Id, command.TransitionId, sessao.StateVersion, sessao.LastMessageSeq, sessao.HandlingPeriod, agora));
        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.HandlingModeChanged,
                    new HandlingModeChangedV1(
                        sessao.Id, de, sessao.HandlingMode, sessao.ResponderRef, sessao.StateVersion, command.TransitionId, sessao.LastMessageSeq,
                        sessao.HandlingPeriod, sessao.PlacementEpoch),
                    agora),
            },
            cancellationToken);
        return new TransitionResult(TransitionOutcome.Applied, sessao.StateVersion, sessao.LastMessageSeq, sessao.HandlingPeriod);
    }

    // A cerca só é guardada junto de uma gravação que será confirmada.
    private static void AdvanceFence(ServiceSession sessao, long? token)
    {
        if (token is { } cerca)
        {
            sessao.AdvanceFence(cerca);
        }
    }

    public async Task<FenceHandlingResult> FenceHandlingAsync(FenceHandlingCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        if (command.FenceToken <= 0)
        {
            throw new ConversationsValidationException("A cerca do executor precisa de um token positivo.");
        }

        await using (var trabalho = await _store.BeginAsync(cancellationToken))
        {
            var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
            if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
            {
                return new FenceHandlingResult(FenceOutcome.SessionNotFound, null);
            }

            contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

            if (!sessao.TryAdvanceFence(command.FenceToken))
            {
                return new FenceHandlingResult(FenceOutcome.FencedOut, null);
            }

            await trabalho.CommitAsync(cancellationToken);
        }

        // A cerca já vale (confirmada): o que um executor mais antigo conseguiu gravar veio antes dela, então esta leitura o inclui.
        var linha = await _store.FindSessionAsync(command.SessionId, cancellationToken);
        return linha is null ? new FenceHandlingResult(FenceOutcome.SessionNotFound, null) : new FenceHandlingResult(FenceOutcome.Fenced, linha.ToView());
    }

    public async Task<SetLocaleResult> SetConversationLocaleAsync(SetConversationLocaleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        if (command.Locale is null)
        {
            throw new ConversationsValidationException("O idioma da conversa é obrigatório.");
        }

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            return new SetLocaleResult(SetLocaleOutcome.SessionNotFound, null, null, null);
        }

        contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

        var conversa = await trabalho.FindSessionConversationAsync(sessao, cancellationToken)
            ?? throw new InvalidOperationException("A sessão não tem conversa.");
        if (!sessao.IsOpen)
        {
            return new SetLocaleResult(SetLocaleOutcome.SessionClosed, sessao.StateVersion, conversa.EffectiveLocale, conversa.LocaleSource);
        }

        if (command.ExpectedStateVersion != sessao.StateVersion)
        {
            return new SetLocaleResult(SetLocaleOutcome.StaleState, sessao.StateVersion, conversa.EffectiveLocale, conversa.LocaleSource);
        }

        // Quem escolhe vem do contexto, nunca do comando: um usuário autenticado é o atendente; o contexto de sistema relata
        // a escolha do próprio cliente (por exemplo, o idioma pedido em um menu).
        var origem = contexto.IsSystem ? LocaleSource.CustomerChoice : LocaleSource.OperatorChoice;
        var de = conversa.EffectiveLocale;
        if (!conversa.ChangeLocale(command.Locale, origem))
        {
            return new SetLocaleResult(SetLocaleOutcome.Unchanged, sessao.StateVersion, conversa.EffectiveLocale, conversa.LocaleSource);
        }

        // O idioma entra no estado versionado: a resposta de um turno em andamento, escrita no idioma anterior, é recusada.
        sessao.AdvanceStateVersion();
        var agora = _clock.GetUtcNow();
        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.ConversationLocaleChanged,
                    new ConversationLocaleChangedV1(sessao.Id, conversa.Id, de, conversa.EffectiveLocale, origem, sessao.StateVersion),
                    agora),
            },
            cancellationToken);

        if (contexto.Principal is not null)
        {
            await _audit.WriteAsync(new AuditEntry("conversations.locale.change", "session", sessao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return new SetLocaleResult(SetLocaleOutcome.Applied, sessao.StateVersion, conversa.EffectiveLocale, conversa.LocaleSource);
    }

    public async Task<SetContactLocaleResult> SetContactPreferredLocaleAsync(SetContactPreferredLocaleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();

        // O contato é alcançado pela sessão, e a organização da sessão decide a permissão: não há acesso a contato por id.
        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            return new SetContactLocaleResult(SetContactLocaleOutcome.SessionNotFound);
        }

        contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

        var contato = await trabalho.FindContactAsync(sessao.ContactId, cancellationToken)
            ?? throw new InvalidOperationException("A sessão não tem contato.");
        if (!contato.SetPreferredLocale(command.PreferredLocale))
        {
            return new SetContactLocaleResult(SetContactLocaleOutcome.Unchanged);
        }

        await trabalho.CommitAsync(cancellationToken);
        if (contexto.Principal is not null)
        {
            await _audit.WriteAsync(new AuditEntry("conversations.contact.locale.set", "session", sessao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        }

        return new SetContactLocaleResult(SetContactLocaleOutcome.Applied);
    }

    public async Task<TransferSessionResult> TransferSessionAsync(TransferSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        var motivo = ConversationLimits.ValidateReason(command.Reason);
        if (command.TransferId == Guid.Empty || command.TargetBusinessUnitId == Guid.Empty || command.TargetDepartmentId == Guid.Empty)
        {
            throw new ConversationsValidationException("O encaminhamento precisa de identificador (chave de idempotência) e de unidade de destino.");
        }

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            return new TransferSessionResult(TransferOutcome.SessionNotFound, null);
        }

        // Autorização explícita: encaminhar a conversa é gerir a conversa na organização dela.
        contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

        // A cerca de quem executa vale antes de qualquer outra regra (ver TransitionHandlingModeAsync).
        if (command.FenceToken is { } cerca && (cerca <= 0 || sessao.IsFencedOut(cerca)))
        {
            return new TransferSessionResult(TransferOutcome.FencedOut, sessao.StateVersion);
        }

        // Idempotência por TransferId: repetir um encaminhamento já aplicado devolve o mesmo resultado, sem mudar nada. O que foi
        // avaliado e não mudou nada (a sessão já estava na unidade pedida) também fica registrado, junto das transições, e vale igual.
        var anterior = await trabalho.FindTransferAsync(sessao.Id, command.TransferId, cancellationToken);
        if (anterior is not null)
        {
            return new TransferSessionResult(TransferOutcome.AlreadyApplied, anterior.StateVersionAfter);
        }

        if (await trabalho.FindTransitionAsync(sessao.Id, command.TransferId, cancellationToken) is { } semMudanca)
        {
            return new TransferSessionResult(TransferOutcome.AlreadyApplied, semMudanca.StateVersionAfter);
        }

        if (!sessao.IsOpen)
        {
            return new TransferSessionResult(TransferOutcome.SessionClosed, sessao.StateVersion);
        }

        // A autorização foi dada sobre um responsável e um período: vale só para alterar esse estado (ver TransitionHandlingModeAsync). Quem já
        // encontra a sessão na unidade e no setor pedidos não altera nada e dispensa a condição.
        if (command.Precondition is { } condicao
            && !(sessao.BusinessUnitId == command.TargetBusinessUnitId && sessao.DepartmentId == command.TargetDepartmentId)
            && !sessao.IsInHandlingState(condicao.Mode, condicao.ResponderRef, condicao.HandlingPeriod, condicao.PlacementEpoch))
        {
            return new TransferSessionResult(TransferOutcome.HandlingChanged, sessao.StateVersion);
        }

        if (command.ExpectedStateVersion != sessao.StateVersion)
        {
            return new TransferSessionResult(TransferOutcome.StaleState, sessao.StateVersion);
        }

        // O destino é validado no tenant do contexto: a unidade precisa ser da organização da sessão (o encaminhamento
        // entre organizações exige revalidação própria e não é suportado aqui), e o setor, da unidade de destino.
        if (await _units.FindOrganizationIdAsync(command.TargetBusinessUnitId, cancellationToken) != sessao.OrganizationId
            || (command.TargetDepartmentId is { } setor
                && await _departments.FindBusinessUnitIdAsync(setor, cancellationToken) != command.TargetBusinessUnitId))
        {
            return new TransferSessionResult(TransferOutcome.TargetNotAllowed, sessao.StateVersion);
        }

        // A lista de unidades autorizadas do número é do Channels e vale também aqui, nas transferências posteriores ao roteamento
        // inicial: consultada a cada pedido (sem cópia que fique velha) e com falha fechada. Não se escolhe outra unidade no lugar.
        if (!await _channelUnits.IsBusinessUnitAuthorizedAsync(sessao.ChannelInstanceId, command.TargetBusinessUnitId, cancellationToken))
        {
            return new TransferSessionResult(TransferOutcome.TargetNotAuthorizedForChannel, sessao.StateVersion);
        }

        var deUnidade = sessao.BusinessUnitId;
        var deSetor = sessao.DepartmentId;
        var deGeracao = sessao.PlacementEpoch; // capturada ANTES de a colocação mudar: a geração da qual a sessão sai
        if (!sessao.TransferTo(command.TargetBusinessUnitId, command.TargetDepartmentId))
        {
            // Nada mudou e nenhum rastro de encaminhamento é criado, mas o identificador fica registrado (e a cerca, confirmada).
            AdvanceFence(sessao, command.FenceToken);
            trabalho.Add(HandlingTransition.Create(contexto.TenantId, sessao.Id, command.TransferId, sessao.StateVersion, null, null, _clock.GetUtcNow()));
            await trabalho.CommitAsync(cancellationToken);
            return new TransferSessionResult(TransferOutcome.Unchanged, sessao.StateVersion);
        }

        var agora = _clock.GetUtcNow();
        AdvanceFence(sessao, command.FenceToken);
        trabalho.Add(SessionTransfer.Create(
            contexto.TenantId, sessao.Id, command.TransferId, deUnidade, deSetor,
            command.TargetBusinessUnitId, command.TargetDepartmentId, contexto.Principal?.UserId, motivo, sessao.StateVersion, agora));
        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.SessionTransferred,
                    new SessionTransferredV1(
                        sessao.Id, command.TransferId, deUnidade, deSetor, command.TargetBusinessUnitId, command.TargetDepartmentId, sessao.StateVersion, sessao.LastMessageSeq,
                        deGeracao, sessao.PlacementEpoch),
                    agora),
            },
            cancellationToken);

        // Rastreabilidade: além da linha do encaminhamento, a trilha de auditoria registra quem (ou qual contexto) o fez.
        await _audit.WriteAsync(new AuditEntry("conversations.session.transfer", "session", sessao.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new TransferSessionResult(TransferOutcome.Applied, sessao.StateVersion);
    }

    public async Task<CloseSessionResult> CloseSessionAsync(CloseSessionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        var motivo = ConversationLimits.ValidateReason(command.Reason);

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        var sessao = await trabalho.LockSessionAsync(command.SessionId, cancellationToken);
        if (sessao is null || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            return new CloseSessionResult(false, false, null);
        }

        contexto.Require(Permission.ConversationManage, sessao.OrganizationId);

        var agora = _clock.GetUtcNow();
        if (!sessao.Close(agora))
        {
            return new CloseSessionResult(true, false, sessao.StateVersion);
        }

        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId, sessao.Id, ConversationEventTypes.SessionClosed, new SessionClosedV1(sessao.Id, motivo, sessao.LastMessageSeq), agora),
            },
            cancellationToken);
        return new CloseSessionResult(true, true, sessao.StateVersion);
    }

    public async Task<RequestResendResult> RequestResendAsync(RequestResendCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();

        if (command.RequestId == Guid.Empty)
        {
            throw new ConversationsValidationException("O reenvio precisa de um identificador de pedido (chave de idempotência).");
        }

        // COV-7.1: só nasce de uma ação humana explícita. O autor é o usuário autenticado do contexto, nunca um parâmetro, e
        // nenhum componente automático (contexto de sistema) chama isto.
        if (contexto.Principal is not { } autor)
        {
            return new RequestResendResult(ResendOutcome.NotAuthorized, null);
        }

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        if (await trabalho.LockMessageAsync(command.MessageId, cancellationToken) is not var (sessao, original)
            || !contexto.IsAllowed(Permission.ConversationRead, sessao.OrganizationId))
        {
            return new RequestResendResult(ResendOutcome.MessageNotFound, null);
        }

        if (!contexto.IsAllowed(Permission.ConversationManage, sessao.OrganizationId))
        {
            return new RequestResendResult(ResendOutcome.NotAuthorized, null);
        }

        // COV-7.3: o mesmo requestId devolve a mesma solicitação.
        var anterior = await trabalho.FindResendByRequestAsync(command.RequestId, cancellationToken);
        if (anterior is not null)
        {
            return anterior.ResendOfMessageId == original.Id
                ? new RequestResendResult(ResendOutcome.AlreadyRequested, anterior.Id)
                : throw new ConversationsValidationException("O identificador do pedido já foi usado para outra mensagem.");
        }

        if (!sessao.IsOpen)
        {
            return new RequestResendResult(ResendOutcome.SessionClosed, null);
        }

        // COV-7.4 e 7.5: só falha definitiva. SendUnknown NÃO é falha definitiva e nunca é reenviado sem confirmação segura.
        if (original.Direction != MessageDirection.Outbound
            || original.DeliveryState is not { } estado
            || !DeliveryStateRules.IsDefinitiveFailure(estado))
        {
            return new RequestResendResult(ResendOutcome.NotDefinitiveFailure, null);
        }

        // COV-7.3: no máximo um reenvio ativo por mensagem original, mesmo com requestId diferente.
        if (await trabalho.HasActiveResendAsync(original.Id, cancellationToken))
        {
            return new RequestResendResult(ResendOutcome.ResendInProgress, null);
        }

        var agora = _clock.GetUtcNow();
        var novo = Message.CreateResend(original, sessao.NextOutboundSeq(agora), command.RequestId, autor.UserId, agora);
        trabalho.Add(novo);

        // O reenvio só publica a solicitação de saída (consumida pelo Channels): não cria turno de IA e não altera a
        // cobertura. O operation_id do envio deriva do requestId (EFX).
        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.OutboundMessageRequested,
                    new OutboundMessageRequestedV1(sessao.Id, novo.ConversationId, novo.Id, command.RequestId, original.Id, novo.ChannelInstanceId),
                    agora),
            },
            cancellationToken);

        // COV-7.2: vínculo auditável. A linha guarda a mensagem original, o autor e o horário; a trilha, quem pediu.
        await _audit.WriteAsync(new AuditEntry("conversations.message.resend", "message", novo.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new RequestResendResult(ResendOutcome.Requested, novo.Id);
    }
}
