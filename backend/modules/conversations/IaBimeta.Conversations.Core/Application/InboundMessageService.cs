using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Entrada do Channels no Conversations: recebe a mensagem do cliente com idempotência, atribui a ordem, e aplica o estado
/// do conteúdo e o status de entrega. Só um contexto de sistema chama (<see cref="Permission.SystemOperation"/>). O tenant
/// vem do contexto confiável, nunca do comando. O texto da mensagem é guardado como veio; o idioma da conversa nova é
/// escolhido por <see cref="LocaleResolution"/> e o idioma do tenant só entra como suposição de último recurso.
/// </summary>
public sealed class InboundMessageService : IConversationIngestion
{
    private readonly IConversationStore _store;
    private readonly IOrganizationDirectory _organizations;
    private readonly IBusinessUnitDirectory _units;
    private readonly IDepartmentDirectory _departments;
    private readonly ITenantSettings _tenantSettings;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IChannelUnitAuthorization _channelUnits;

    public InboundMessageService(
        IConversationStore store,
        IOrganizationDirectory organizations,
        IBusinessUnitDirectory units,
        IDepartmentDirectory departments,
        ITenantSettings tenantSettings,
        IRequestContextAccessor context,
        TimeProvider clock,
        IChannelUnitAuthorization channelUnits)
    {
        _store = store;
        _organizations = organizations;
        _units = units;
        _departments = departments;
        _tenantSettings = tenantSettings;
        _context = context;
        _clock = clock;
        _channelUnits = channelUnits;
    }

    public async Task<IngestInboundMessageResult> IngestInboundMessageAsync(IngestInboundMessageCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        // Valida a forma antes de qualquer consulta ou gravação (as regras de formato moram no domínio).
        if (command.ChannelInstanceId.IsEmpty)
        {
            throw new ConversationsValidationException("A instância de canal é obrigatória.");
        }

        var evento = ConversationLimits.ValidateProviderEventId(command.ProviderEventId);

        var contato = Contact.Create(contexto.TenantId, command.ChannelInstanceId, command.ContactExternalRef, command.ContactDisplayName, DateTimeOffset.UnixEpoch);
        if (!Enum.IsDefined(command.Kind))
        {
            throw new ConversationsValidationException("O tipo da mensagem é inválido.");
        }

        // O modo inicial é o vocabulário único de HandlingMode: AI ou Human. Hybrid (AiSupervised) ainda não foi especificado.
        if (!Enum.IsDefined(command.InitialHandlingMode) || command.InitialHandlingMode == HandlingMode.AiSupervised)
        {
            throw new ConversationsValidationException("O modo inicial deve ser IA autônoma ou atendimento humano; o modo híbrido ainda não está especificado.");
        }

        // O modo de associação: hoje só existe o do contato (chat). Qualquer outro valor é RECUSADO antes de ler ou gravar qualquer coisa, e nunca rebaixado
        // para o contato: rebaixar trataria uma mensagem de outro tipo de associação (a thread de e-mail, de uma fatia posterior) como chat (ADR 0012, seção B).
        if (command.AssociationMode != AssociationMode.Contact)
        {
            throw new ConversationsValidationException("O modo de associação informado ainda não é suportado: só a associação por contato (chat).");
        }

        Message.ValidateText(command.Text, command.Kind, required: command.Kind == MessageKind.Text);
        if (command.ProviderTimestamp == default)
        {
            throw new ConversationsValidationException("O momento da mensagem no provedor é obrigatório.");
        }

        // Metadados do canal (A3): validados na forma ANTES de qualquer acesso ao armazenamento, e guardados com a mensagem (versão e pares, sem interpretá-los). Uma duplicata
        // não os regrava: a primeira gravação vale, e a repetição devolve a mensagem que já existe.
        if (command.ChannelData is { } dadosDoCanal)
        {
            ConversationLimits.ValidateChannelData(dadosDoCanal);
        }

        // Caminho rápido da duplicata: o mesmo evento do provedor já gravado devolve o resultado original, sem travar nada.
        var existente = await _store.FindMessageByProviderEventAsync(command.ChannelInstanceId, evento, cancellationToken);
        if (existente is not null)
        {
            return Duplicate(existente);
        }

        // A unidade e o setor responsáveis são identificados e validados (um canal pode representar várias unidades, ARCH §6).
        // Quem escolhe entre as autorizadas do canal é o Channels; aqui se confere o que é do tenant (organização, unidade, setor)
        // e, na sessão nova, que a unidade está autorizada para o número (mais abaixo, sob a trava da sessão).
        if (!await _organizations.ExistsAsync(command.OrganizationId, cancellationToken))
        {
            throw new ConversationsValidationException("A organização informada não existe neste tenant.");
        }

        var unidadeId = command.BusinessUnitId;
        if (command.DepartmentId is { } setorId)
        {
            var unidadeDoSetor = await _departments.FindBusinessUnitIdAsync(setorId, cancellationToken)
                ?? throw new ConversationsValidationException("O setor informado não existe neste tenant.");
            if (unidadeId is not null && unidadeId != unidadeDoSetor)
            {
                throw new ConversationsValidationException("O setor informado não pertence à unidade informada.");
            }

            unidadeId = unidadeDoSetor;
        }

        if (unidadeId is { } unidade
            && await _units.FindOrganizationIdAsync(unidade, cancellationToken) != command.OrganizationId)
        {
            throw new ConversationsValidationException("A unidade informada não existe ou não pertence à organização informada.");
        }

        try
        {
            return await RegistrarAsync(contexto, command, evento, contato.ExternalRef, unidadeId, cancellationToken);
        }
        catch (DuplicateProviderEventException)
        {
            // Outra transação gravou o mesmo evento no mesmo instante: o índice único decidiu, e o resultado é o dela.
            var gravado = await _store.FindMessageByProviderEventAsync(command.ChannelInstanceId, evento, cancellationToken)
                ?? throw new InvalidOperationException("O evento duplicado do provedor não foi encontrado depois do conflito.");
            return Duplicate(gravado);
        }
    }

    private async Task<IngestInboundMessageResult> RegistrarAsync(
        RequestContext contexto,
        IngestInboundMessageCommand command,
        string evento,
        string referenciaDoContato,
        Guid? unidadeId,
        CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();

        // Uma sessão fechada entre achar e travar é rara (o encerramento é uma ação administrativa): tenta de novo.
        for (var tentativa = 0; tentativa < 3; tentativa++)
        {
            await using var trabalho = await _store.BeginAsync(cancellationToken);

            var contato = await trabalho.GetOrCreateContactAsync(command.ChannelInstanceId, referenciaDoContato, command.ContactDisplayName, agora, cancellationToken);
            var sessaoId = await trabalho.GetOrCreateOpenSessionIdAsync(
                contato, command.OrganizationId, unidadeId, command.DepartmentId, command.InitialHandlingMode, command.AssociationMode, agora, cancellationToken);
            var sessao = await trabalho.LockSessionAsync(sessaoId, cancellationToken);
            if (sessao is null || !sessao.IsOpen)
            {
                continue;
            }

            // Sob a trava da sessão: dois eventos iguais simultâneos se enfileiram aqui, e o segundo vê o primeiro.
            var duplicada = await trabalho.FindMessageByProviderEventAsync(command.ChannelInstanceId, evento, cancellationToken);
            if (duplicada is not null)
            {
                return Duplicate(duplicada);
            }

            // Roteamento inicial: a unidade da sessão NOVA precisa estar entre as autorizadas do número. Só vale para a sessão
            // recém-criada (sem mensagens): uma sessão já aberta, inclusive uma transferida, não é reencaminhada por mensagem
            // nova, e recusar a mensagem do cliente por causa de uma associação revogada depois perderia o que ele disse.
            // Sob a trava da sessão a pergunta é atual, e recusar aqui descarta a transação inteira (nem contato nem sessão ficam).
            if (sessao.LastMessageSeq == 0
                && sessao.BusinessUnitId is { } unidadeDaSessao
                && !await _channelUnits.IsBusinessUnitAuthorizedAsync(command.ChannelInstanceId, unidadeDaSessao, cancellationToken))
            {
                throw new ChannelUnitNotAuthorizedException();
            }

            var conversa = await trabalho.FindSessionConversationAsync(sessao, cancellationToken);
            if (conversa is null)
            {
                // Conversa nova: o idioma sai do que se sabe do cliente e só no fim do idioma do tenant, marcado como suposição.
                var padraoDoTenant = await _tenantSettings.GetDefaultLocaleAsync(cancellationToken);
                var (idioma, origem) = LocaleResolution.ForNewConversation(contato.PreferredLocale, command.ContactLocaleHint, command.ChannelDefaultLocale, padraoDoTenant);
                conversa = await trabalho.GetOrCreateConversationAsync(sessao, agora, idioma, origem, cancellationToken);
            }

            var registro = sessao.RegisterInbound(command.ProviderTimestamp, agora);
            var mensagem = Message.CreateInbound(
                contexto.TenantId, sessao.Id, conversa.Id, command.ChannelInstanceId, registro, evento, command.ProviderTimestamp, command.Kind, command.Text, agora, command.MessageLocale,
                command.ChannelData);
            trabalho.Add(mensagem);
            await trabalho.CommitAsync(
                new[]
                {
                    ConversationEventFactory.Create(
                        contexto.TenantId,
                        sessao.Id,
                        ConversationEventTypes.InboundMessageRegistered,
                        new InboundMessageRegisteredV1(sessao.Id, conversa.Id, mensagem.Id, registro.InboundSeq, mensagem.Kind, mensagem.ContentState, registro.LateArrival, mensagem.ChannelInstanceId),
                        agora),
                },
                cancellationToken);

            return new IngestInboundMessageResult(
                IngestOutcome.Registered, sessao.Id, conversa.Id, mensagem.Id, registro.InboundSeq, registro.MessageSeq, registro.LateArrival, mensagem.ContentState);
        }

        throw new InvalidOperationException("Não foi possível obter uma sessão aberta para o contato.");
    }

    public async Task<ContentChangeResult> SetMessageContentAsync(SetMessageContentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);
        Message.ValidateText(command.Text, MessageKind.Text, required: true);

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        if (await trabalho.LockMessageAsync(command.MessageId, cancellationToken) is not var (sessao, mensagem))
        {
            return new ContentChangeResult(ContentChangeOutcome.MessageNotFound, null);
        }

        var resultado = mensagem.SetContent(command.Text);
        if (resultado != ContentChangeOutcome.Changed)
        {
            return new ContentChangeResult(resultado, mensagem.ContentState);
        }

        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.MessageContentReady,
                    new MessageContentReadyV1(sessao.Id, mensagem.Id, mensagem.InboundSeq!.Value),
                    _clock.GetUtcNow()),
            },
            cancellationToken);
        return new ContentChangeResult(ContentChangeOutcome.Changed, mensagem.ContentState);
    }

    public async Task<ContentChangeResult> FailMessageContentAsync(FailMessageContentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);
        var motivo = ConversationLimits.ValidateReason(command.Reason);

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        if (await trabalho.LockMessageAsync(command.MessageId, cancellationToken) is not var (sessao, mensagem))
        {
            return new ContentChangeResult(ContentChangeOutcome.MessageNotFound, null);
        }

        var resultado = mensagem.FailContent();
        if (resultado != ContentChangeOutcome.Changed)
        {
            return new ContentChangeResult(resultado, mensagem.ContentState);
        }

        await trabalho.CommitAsync(
            new[]
            {
                ConversationEventFactory.Create(
                    contexto.TenantId,
                    sessao.Id,
                    ConversationEventTypes.MessageContentFailed,
                    new MessageContentFailedV1(sessao.Id, mensagem.Id, mensagem.InboundSeq!.Value, motivo),
                    _clock.GetUtcNow()),
            },
            cancellationToken);
        return new ContentChangeResult(ContentChangeOutcome.Changed, mensagem.ContentState);
    }

    public async Task<DeliveryStatusResult> RecordDeliveryStatusAsync(RecordDeliveryStatusCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        if (!Enum.IsDefined(command.State))
        {
            throw new ConversationsValidationException("O estado de entrega é inválido.");
        }

        if (!Enum.IsDefined(command.Evidence))
        {
            throw new ConversationsValidationException("A origem da evidência do status é inválida.");
        }

        // A declaração humana só CONFIRMA o envio (DP-08; ADR 0011, seção F): nunca produz falha, expiração, entrega nem leitura. A recusa vem antes de ler ou
        // gravar qualquer coisa, e a máquina de estados do envio não muda: a origem padrão do provedor segue exatamente como era.
        if (!IsEvidenceAllowed(command.Evidence, command.State))
        {
            return new DeliveryStatusResult(DeliveryStatusOutcome.EvidenceNotAllowed, null);
        }

        var motivo = DeliveryStateRules.IsDefinitiveFailure(command.State) ? ConversationLimits.ValidateReason(command.FailureReason) : null;

        await using var trabalho = await _store.BeginAsync(cancellationToken);
        if (await trabalho.LockMessageAsync(command.MessageId, cancellationToken) is not var (sessao, mensagem))
        {
            return new DeliveryStatusResult(DeliveryStatusOutcome.MessageNotFound, null);
        }

        if (mensagem.Direction != MessageDirection.Outbound)
        {
            return new DeliveryStatusResult(DeliveryStatusOutcome.NotAnOutboundMessage, null);
        }

        if (!mensagem.TryApplyDelivery(command.State, motivo))
        {
            // O status nunca regride: igual, anterior ou depois de um estado final é ignorado.
            return new DeliveryStatusResult(DeliveryStatusOutcome.Ignored, mensagem.DeliveryState);
        }

        var eventos = new List<EventEnvelope>();
        if (DeliveryStateRules.IsDefinitiveFailure(command.State))
        {
            // COV-3: a falha definitiva NÃO altera AnsweredThroughSeq, NÃO cria turno de IA. Só marca e sinaliza (ao ContactCenter).
            sessao.MarkReplyDeliveryFailed();
            eventos.Add(ConversationEventFactory.Create(
                contexto.TenantId,
                sessao.Id,
                ConversationEventTypes.OutboundMessageFailed,
                new OutboundMessageFailedV1(mensagem.Id, sessao.Id, mensagem.CoveredThroughSeq ?? sessao.AnsweredThroughSeq, motivo!),
                _clock.GetUtcNow()));
        }
        else if (command.State is DeliveryState.Sent or DeliveryState.Delivered or DeliveryState.Read)
        {
            sessao.ClearReplyDeliveryFailed();
        }

        // Os eventos (se houver) entram no outbox na mesma transação da mudança de estado.
        await trabalho.CommitAsync(eventos, cancellationToken);

        return new DeliveryStatusResult(DeliveryStatusOutcome.Applied, mensagem.DeliveryState);
    }

    /// <summary>Só a declaração humana tem restrição: ela confirma <see cref="DeliveryState.Sent"/> e nada mais.</summary>
    private static bool IsEvidenceAllowed(EvidenceSource evidence, DeliveryState state) =>
        evidence != EvidenceSource.Manual || state == DeliveryState.Sent;

    public async Task<RecordTranslationResult> RecordMessageTranslationAsync(RecordMessageTranslationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        if (command.TargetLocale is null)
        {
            throw new ConversationsValidationException("O idioma da tradução é obrigatório.");
        }

        Message.ValidateText(command.Text, MessageKind.Text, required: true);
        ConversationLimits.ValidateProvenance(command.Provenance);

        // A sessão da mensagem é travada: duas traduções simultâneas do mesmo par se enfileiram, e a segunda vê a primeira.
        await using var trabalho = await _store.BeginAsync(cancellationToken);
        if (await trabalho.LockMessageAsync(command.MessageId, cancellationToken) is not var (_, mensagem))
        {
            return new RecordTranslationResult(RecordTranslationOutcome.MessageNotFound, null);
        }

        if (mensagem.ContentState != ContentState.Ready || mensagem.Text is null)
        {
            return new RecordTranslationResult(RecordTranslationOutcome.ContentNotReady, null);
        }

        var existente = await trabalho.FindTranslationAsync(mensagem.Id, command.TargetLocale, cancellationToken);
        if (existente is not null)
        {
            // Nunca sobrescreve: a mesma tradução é idempotente, e uma diferente é conflito.
            return new RecordTranslationResult(
                string.Equals(existente.Text, command.Text, StringComparison.Ordinal) ? RecordTranslationOutcome.AlreadyRecorded : RecordTranslationOutcome.Conflict,
                existente.Id);
        }

        var traducao = MessageTranslation.Create(contexto.TenantId, mensagem, command.TargetLocale, command.Text, command.Provenance, _clock.GetUtcNow());
        trabalho.Add(traducao);
        await trabalho.CommitAsync(cancellationToken);
        return new RecordTranslationResult(RecordTranslationOutcome.Recorded, traducao.Id);
    }

    private static IngestInboundMessageResult Duplicate(Message existente) => new(
        IngestOutcome.Duplicate,
        existente.SessionId,
        existente.ConversationId,
        existente.Id,
        existente.InboundSeq ?? 0,
        existente.MessageSeq,
        existente.LateArrival,
        existente.ContentState);
}
