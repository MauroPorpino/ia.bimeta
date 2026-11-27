using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>Lê e decifra a credencial de uma conta, só para o uso imediato. Falha fechada: chave mestra ausente ou texto adulterado devolvem nulo, sem repetir nada do que falhou.</summary>
internal static class ChannelSecretReader
{
    public static async Task<string?> ReadAsync(
        IChannelRepository repository, ISecretProtector protector, TenantId tenant, Guid accountId, ChannelSecretKind kind, CancellationToken cancellationToken)
    {
        var segredo = await repository.FindSecretAsync(accountId, kind, cancellationToken);
        if (segredo is null)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(new ProtectedSecret(segredo.KeyId, segredo.Ciphertext), new SecretBinding(tenant, segredo.Id, kind));
        }
        catch (Exception ex) when (ex is ChannelSecretsNotConfiguredException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// O SENDER do Channels (ADR 0011, seção E): consome <c>OutboundMessageRequestedV1</c> (a mensagem da IA ou do atendente, já aceita no Conversations) e a envia pelo provedor da instância, com a mesma disciplina
/// de efeito externo da especificação: (1) a INTENÇÃO de enviar é gravada e confirmada ANTES da chamada (<see cref="OutboundState.Dispatching"/>), com o <c>OperationId</c> estável e único por tenant; (2) a chamada
/// ao provedor roda sem transação aberta; (3) o desfecho é gravado e informado ao Conversations (<c>RecordDeliveryStatus</c>). A estratégia do WhatsApp é a (c), sem idempotência de envio: um resultado ambíguo
/// (tempo esgotado, queda, 5xx) vira <see cref="OutboundState.SendUnknown"/> e NUNCA é reenviado; um despacho que ficou em <see cref="OutboundState.Dispatching"/> porque o processo caiu também vira
/// <c>SendUnknown</c>, nunca um novo envio (EFX-3). Só a recusa transitória COM prova de que nada saiu (limite de taxa) é tentada de novo, até um teto. Só uma instância <c>Active</c> envia. A falha definitiva é
/// registrada no Conversations (que publica <c>OutboundMessageFailedV1</c>: não é entrada para a IA). O token de acesso é decifrado para esta chamada e nunca guardado nem registrado.
/// </summary>
public sealed class OutboundMessageRequestedHandler : EventHandlerBase<OutboundMessageRequestedV1>
{
    /// <summary>O teto de chamadas por mensagem quando o provedor recusa de forma transitória (a espera entre elas é a do relay).</summary>
    public const int MaxSendAttempts = 3;

    private readonly IOutboundDispatchQueries _dispatch;
    private readonly ITenantOperationGate? _gate;
    private readonly IOutboundAttachmentReader _files;
    private readonly IConversationIngestion _ingestion;
    private readonly IChannelRepository _repository;
    private readonly ISecretProtector _protector;
    private readonly ChannelProviderAdapters _adapters;
    private readonly IOutboundMessageStore _store;
    private readonly IAccountSendStateStore _sendState;
    private readonly ChannelSendingOptions _sending;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;

    public OutboundMessageRequestedHandler(
        IOutboundDispatchQueries dispatch,
        IOutboundAttachmentReader files,
        IConversationIngestion ingestion,
        IChannelRepository repository,
        ISecretProtector protector,
        ChannelProviderAdapters adapters,
        IOutboundMessageStore store,
        IAccountSendStateStore sendState,
        ChannelSendingOptions sending,
        IAuditWriter audit,
        TimeProvider clock,
        ITenantOperationGate? operationGate = null)
    {
        _gate = operationGate;
        _audit = audit;
        _dispatch = dispatch;
        _files = files;
        _ingestion = ingestion;
        _repository = repository;
        _protector = protector;
        _adapters = adapters;
        _store = store;
        _sendState = sendState;
        _sending = sending;
        _clock = clock;
    }

    public override string EventType => ConversationEventTypes.OutboundMessageRequested;

    public override int EventVersion => 1;

    protected override async Task<HandlerResult> HandleAsync(EventEnvelope envelope, OutboundMessageRequestedV1 payload, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(envelope, payload, cancellationToken);
        }
        catch (OutboundStateConflictException)
        {
            // Outro processo gravou o estado do despacho depois de este o ler (por exemplo, este handler perdeu o lease e o novo dono ou o tratamento de morto já decidiu): a gravação foi DESCARTADA e o que está gravado
            // vale. Nada é reenviado e nada é sobrescrito; a próxima entrega (se ainda houver uma para este lease) relê o estado.
            ChannelsMetrics.OutboundStateConflict.Add(1);
            return HandlerResult.Retry(null, "outbound-state-conflict");
        }
    }

    private async Task<HandlerResult> SendAsync(EventEnvelope envelope, OutboundMessageRequestedV1 payload, CancellationToken cancellationToken)
    {
        // Suspensão da empresa (Etapa 02): nenhuma mensagem sai e nenhuma intenção é gravada. A entrega é ADIADA sem gastar tentativa (Defer, ADR 0020); a mensagem já aceita no Conversations continua guardada.
        TenantOperationState? estadoDaEmpresa = null;
        if (_gate is not null)
        {
            estadoDaEmpresa = await _gate.GetAsync(envelope.TenantId, cancellationToken);
            if (!estadoDaEmpresa.IsOperational)
            {
                return HandlerResult.Defer(TimeSpan.FromMinutes(2), "tenant-suspended");
            }
        }

        var agora = _clock.GetUtcNow();
        OutboundMessage mensagem;

        var existente = await _store.FindByOperationAsync(payload.OperationId, cancellationToken);
        if (existente is null)
        {
            // (1) A intenção, gravada e confirmada ANTES de qualquer chamada ao provedor.
            mensagem = OutboundMessage.Intend(payload.MessageId, envelope.TenantId, payload.OperationId, payload.ChannelInstanceId, agora);
            if (!await _store.TryAddAsync(mensagem, cancellationToken))
            {
                return HandlerResult.Retry(null, "concurrent-dispatch");
            }
        }
        else if (existente.MayHaveReachedProvider)
        {
            // Uma tentativa anterior COMEÇOU e não terminou (o processo caiu entre o início da chamada e o desfecho): o provedor PODE ter recebido. Nunca reenviar (EFX-3). A intenção sozinha (zero tentativas,
            // por exemplo depois de uma espera por vazão) NÃO cai aqui: nenhuma chamada foi feita e a entrega segue abaixo.
            existente.MarkSendUnknown("interrupted", agora);
            await _store.SaveAsync(cancellationToken);
            ChannelsMetrics.OutboundUnknown.Add(1);
            return await NotifyAsync(existente, cancellationToken);
        }
        else if (existente.IsResolved)
        {
            return await NotifyAsync(existente, cancellationToken);
        }
        else
        {
            mensagem = existente; // Retry: a recusa anterior provou que nada saiu.
        }

        // Pedida ANTES da última reativação (durante a suspensão ou antes): não é liberada em bloco. Falha visível e definitiva; o atendente a reenvia por ação autorizada (RequestResend), sem nenhuma chamada ao provedor.
        if (estadoDaEmpresa?.OperationalSince is { } desde && envelope.OccurredAt < desde)
        {
            return await FailAsync(mensagem, "tenant-suspended", cancellationToken);
        }

        var visao = await _dispatch.GetAsync(payload.MessageId, cancellationToken);
        if (visao is null)
        {
            return await FailAsync(mensagem, "message-not-found", cancellationToken);
        }

        // A mensagem de ARQUIVO (ADR 0031) vai pelo mesmo caminho: sem o arquivo (o prazo de guarda venceu) ela falha de forma visível, sem gastar ficha, tentativa nem chamada; a de texto precisa do texto.
        var arquivo = visao.Kind == MessageKind.Text ? null : visao.Attachment;
        if (visao.Kind == MessageKind.Text ? string.IsNullOrWhiteSpace(visao.Text) : arquivo is null)
        {
            return await FailAsync(mensagem, visao.Kind == MessageKind.Text ? "message-not-found" : "attachment-unavailable", cancellationToken);
        }

        // Só o que o Conversations liberou e ainda não saiu: uma mensagem já enviada (ou com falha) não é enviada de novo.
        if (visao.DeliveryState is not (DeliveryState.Requested or DeliveryState.Queued or DeliveryState.Sending))
        {
            mensagem.MarkSent("already-handled", agora);
            await _store.SaveAsync(cancellationToken);
            mensagem.MarkNotified(agora);
            await _store.SaveAsync(cancellationToken);
            return HandlerResult.Success;
        }

        var instancia = await _repository.FindInstanceReadOnlyAsync(payload.ChannelInstanceId, cancellationToken);
        if (instancia is null || instancia.Status != ChannelStatus.Active)
        {
            return await FailAsync(mensagem, "channel-inactive", cancellationToken);
        }

        var adaptador = _adapters.Find(instancia.Provider);
        if (adaptador is null)
        {
            return await FailAsync(mensagem, "provider-unavailable", cancellationToken);
        }

        // Uma mensagem por MODELO aprovado (ADR 0027) leva o modelo e os parâmetros no bloco de canal. O plano é montado e conferido ANTES de gastar ficha, tentativa ou chamada: um modelo que sumiu, que não é
        // da conta do número, que deixou de estar aprovado ou cuja estrutura mudou desde a validação falha de forma visível, sem nada enviado.
        TemplateSendPlan? plano = null;
        if (visao.ChannelData is { } dadosDoCanal && dadosDoCanal.Values.ContainsKey(TemplateRenderer.KeyTemplateId))
        {
            plano = await PlanTemplateAsync(dadosDoCanal, instancia.AccountId, cancellationToken);
            if (plano.FailureCode is not null)
            {
                return await FailAsync(mensagem, plano.FailureCode, cancellationToken);
            }
        }

        var token = await ChannelSecretReader.ReadAsync(_repository, _protector, envelope.TenantId, instancia.AccountId, ChannelSecretKind.AccessToken, cancellationToken);
        if (token is null)
        {
            return await FailAsync(mensagem, "account-credentials-missing", cancellationToken);
        }

        // O conteúdo do arquivo (ADR 0031), lido do armazenamento privado do Conversations ANTES de gastar ficha: arquivo que sumiu entre a leitura da mensagem e agora falha de forma visível, sem chamada.
        byte[]? conteudoDoArquivo = null;
        if (arquivo is not null)
        {
            conteudoDoArquivo = await ReadFileAsync(payload.MessageId, arquivo.SizeBytes, cancellationToken);
            if (conteudoDoArquivo is null)
            {
                return await FailAsync(mensagem, "attachment-unavailable", cancellationToken);
            }
        }

        // SND-6 e SND-2, ANTES de registrar a tentativa e de qualquer chamada ao provedor: conta pausada não envia (a mensagem falha de forma visível, sem gastar tentativa do relay), e o balde da conta limita a vazão.
        // A espera curta é feita aqui mesmo (a ordem da lane e as tentativas do relay ficam intactas); uma espera longa devolve a entrega ao relay.
        var tempoLimite = _clock.GetUtcNow() + _sending.MaxLocalWait;
        while (true)
        {
            var ficha = await _sendState.TryAcquireAsync(instancia.AccountId, cancellationToken);
            if (ficha.Paused)
            {
                ChannelsMetrics.OutboundBlockedByPause.Add(1);
                return await FailAsync(mensagem, "account-paused", cancellationToken);
            }

            if (ficha.Granted)
            {
                break;
            }

            if (ficha.Wait > tempoLimite - _clock.GetUtcNow())
            {
                // Espera longa: NÃO é falha, então o relay adia a entrega SEM gastar tentativa (Defer). Quem adia garante o próprio limite: a validade operacional da mensagem (SND-4). Uma resposta que esperou
                // além dela deixou de fazer sentido e falha de forma visível (expired), em vez de ficar parada para sempre.
                if (_clock.GetUtcNow() - envelope.OccurredAt + ficha.Wait > _sending.MaxQueueAge)
                {
                    return await FailAsync(mensagem, "expired", cancellationToken);
                }

                ChannelsMetrics.OutboundThrottled.Add(1);
                return HandlerResult.Defer(ficha.Wait, "rate-limited-local");
            }

            await Task.Delay(ficha.Wait, _clock, cancellationToken);
        }

        // Verificação FINAL da empresa (P1-04): as esperas acima (vazão, SND-2) podem ter atravessado uma suspensão. Reconsulta o estado PERSISTIDO e confere de novo o corte da reativação, ANTES de registrar a tentativa
        // e de iniciar a chamada externa. Suspensa: adia sem gastar tentativa (a ficha de vazão já tomada é só um token de balde, sem efeito externo). Reativada depois do pedido: falha visível e definitiva, nunca envio automático.
        // Janela residual (declarada no ADR 0043): entre esta leitura e o início da chamada ficam apenas as duas gravações curtas abaixo; uma suspensão que caia exatamente aí não cancela a chamada, igual a uma chamada já iniciada.
        // Fechá-la por inteiro exigiria segurar uma trava durante a chamada externa, o que não se faz (transação aberta em chamada de rede).
        if (_gate is not null)
        {
            var estadoFinal = await _gate.GetAsync(envelope.TenantId, cancellationToken);
            if (!estadoFinal.IsOperational)
            {
                return HandlerResult.Defer(TimeSpan.FromMinutes(2), "tenant-suspended");
            }

            if (estadoFinal.OperationalSince is { } reativadaEm && envelope.OccurredAt < reativadaEm)
            {
                return await FailAsync(mensagem, "tenant-suspended", cancellationToken);
            }
        }

        // O Conversations passa a mostrar "Enviando" (e só a partir daí um resultado ambíguo pode virar SendUnknown); o despacho registra a tentativa ANTES da chamada.
        await _ingestion.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(payload.MessageId, DeliveryState.Sending, null), cancellationToken);
        mensagem.BeginAttempt(_clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);

        // ADMISSÃO (decisão ratificada, ADR 0043): a verificação FINAL, depois de TODAS as gravações e imediatamente antes de invocar o adaptador, é o ponto em que a operação é admitida. Reconsulta o estado persistido (falha
        // fechada) e o corte da reativação. Recusada: a tentativa que não aconteceu NÃO é contabilizada (desfeita e gravada), nada é marcado como enviado e a mensagem é adiada (suspensa) ou falha de forma visível (anterior à
        // reativação). Entre esta verificação e a invocação do adaptador não há espera de banco, de vazão nem preparação (os dados do pedido já foram lidos acima). Admitida antes da suspensão, a operação pode concluir.
        // Limite declarado: a intercalação entre esta leitura e a chamada imediata ao adaptador NÃO é eliminada (uma suspensão exatamente aí não cancela uma chamada já admitida).
        if (_gate is not null)
        {
            TenantOperationState admissao;
            try
            {
                admissao = await _gate.GetAsync(envelope.TenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                admissao = new TenantOperationState(false, null); // falha fechada: sem leitura do estado não há admissão (e a tentativa que não aconteceu é desfeita abaixo)
            }

            var suspensa = !admissao.IsOperational;
            var anteriorAReativacao = admissao.OperationalSince is { } reativadaEm && envelope.OccurredAt < reativadaEm;
            if (suspensa || anteriorAReativacao)
            {
                mensagem.RevertAttempt(_clock.GetUtcNow());
                await _store.SaveAsync(cancellationToken);
                return suspensa ? HandlerResult.Defer(TimeSpan.FromMinutes(2), "tenant-suspended") : await FailAsync(mensagem, "tenant-suspended", cancellationToken);
            }
        }

        // (2) A chamada ao provedor, sem transação aberta. O identificador da nossa mensagem vai como dado opaco: o provedor o devolve nos status.
        var resultado = arquivo is not null
            ? await adaptador.SendMediaAsync(
                new ProviderMediaSendRequest(
                    token, instancia.PhoneNumberId, visao.ContactExternalRef, ProviderKindOf(arquivo.Kind), arquivo.MimeType, arquivo.FileName, conteudoDoArquivo!, arquivo.Caption, payload.MessageId.ToString("N")),
                cancellationToken)
            : plano is null
            ? await adaptador.SendTextAsync(
                new ProviderSendRequest(token, instancia.PhoneNumberId, visao.ContactExternalRef, visao.Text!, payload.MessageId.ToString("N")), cancellationToken)
            : await adaptador.SendTemplateAsync(
                new ProviderTemplateSendRequest(
                    token, instancia.PhoneNumberId, visao.ContactExternalRef, plano.Template!.Name, plano.Template.Language, plano.Parameters, payload.MessageId.ToString("N")),
                cancellationToken);

        // (3) O desfecho.
        var depois = _clock.GetUtcNow();
        switch (resultado.Outcome)
        {
            case ProviderSendOutcome.Accepted:
                mensagem.MarkSent(resultado.ProviderMessageId ?? "accepted", depois);
                ChannelsMetrics.OutboundSent.Add(1);
                break;

            case ProviderSendOutcome.RejectedTransient when mensagem.Attempts < MaxSendAttempts:
            {
                mensagem.MarkRetry(resultado.ErrorCode, depois);
                await _store.SaveAsync(cancellationToken);

                // SND-2: o limite de taxa da CONTA vale para a conta toda (os envios das outras conversas também esperam, sem gastar tentativa do provedor); o do par remetente-destinatário, só para esta mensagem.
                // A recusa transitória do provedor prova que nada saiu e é limitada a MaxSendAttempts chamadas: a espera é Defer (não gasta tentativa do relay, que é para falhas de verdade).
                var espera = _sending.CooldownFor(resultado.RetryAfter);
                if (resultado.RateLimitScope == ProviderRateLimitScope.Account)
                {
                    await _sendState.ApplyCooldownAsync(instancia.AccountId, espera, cancellationToken);
                    ChannelsMetrics.OutboundRateLimited.Add(1);
                    return HandlerResult.Defer(espera, "provider-rate-limited");
                }

                if (resultado.RateLimitScope == ProviderRateLimitScope.Recipient)
                {
                    // O par remetente-destinatário: a Meta manda repetir depois de 4^X segundos (X = 0, 1, 2...), e só este destinatário espera.
                    ChannelsMetrics.OutboundRateLimited.Add(1);
                    return HandlerResult.Defer(resultado.RetryAfter ?? PairBackoff(mensagem.Attempts), "provider-pair-rate-limited");
                }

                return HandlerResult.Defer(resultado.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Max(1, mensagem.Attempts))), "provider-transient");
            }

            case ProviderSendOutcome.RejectedTransient:
                // A falha da SUBIDA do arquivo (ADR 0031) esgotou as tentativas: o motivo diz que foi o envio do arquivo (nada saiu), não o limite de taxa.
                mensagem.MarkFailed(resultado.ErrorCode is { } codigoDaSubida && codigoDaSubida.StartsWith("upload-", StringComparison.Ordinal) ? codigoDaSubida : "rate-limited", depois);
                ChannelsMetrics.OutboundFailed.Add(1);
                if (resultado.RateLimitScope == ProviderRateLimitScope.Account)
                {
                    // Esta mensagem esgotou as chamadas, mas a conta continua limitada: as demais esperam.
                    await _sendState.ApplyCooldownAsync(instancia.AccountId, _sending.CooldownFor(resultado.RetryAfter), cancellationToken);
                    ChannelsMetrics.OutboundRateLimited.Add(1);
                }

                break;

            case ProviderSendOutcome.WindowClosed:
                mensagem.MarkFailed("window-closed", depois);
                ChannelsMetrics.OutboundFailed.Add(1);
                break;

            case ProviderSendOutcome.AccountError:
                mensagem.MarkFailed("account-error", depois);
                ChannelsMetrics.OutboundFailed.Add(1);
                await _store.SaveAsync(cancellationToken);

                // SND-6: o erro atinge a CONTA (token expirado ou inválido, conta restrita): a conta é pausada, e as próximas mensagens falham como "account-paused" sem tocar no provedor nem gastar tentativa. A mensagem
                // já está gravada como falha: se a pausa não puder ser gravada agora, o próximo envio da conta recebe o mesmo erro e tenta de novo.
                if (await _sendState.PauseAsync(instancia.AccountId, $"account-error.{resultado.ErrorCode ?? "unknown"}", cancellationToken))
                {
                    ChannelsMetrics.AccountPaused.Add(1);
                    await _audit.WriteAsync(new AuditEntry("channels.account.pause.provider-error", "channel-account", instancia.AccountId.ToString(), AuditOutcome.Success), cancellationToken);
                }

                break;

            case ProviderSendOutcome.RejectedPermanent:
                mensagem.MarkFailed(resultado.ErrorCode ?? "rejected", depois);
                ChannelsMetrics.OutboundFailed.Add(1);
                break;

            default:
                mensagem.MarkSendUnknown(resultado.ErrorCode, depois);
                ChannelsMetrics.OutboundUnknown.Add(1);
                break;
        }

        await _store.SaveAsync(cancellationToken);

        // A Meta recusou o MODELO (pausado, desativado, inexistente): o cadastro deixa de oferecê-lo ao atendente até o administrador o reaprovar. Melhor esforço, DEPOIS de o desfecho da mensagem estar gravado.
        if (plano?.Template is { } modeloRecusado && resultado.Outcome == ProviderSendOutcome.RejectedPermanent)
        {
            await MarkTemplateUnavailableAsync(modeloRecusado.Id, resultado.ErrorCode, cancellationToken);
        }

        return await NotifyAsync(mensagem, cancellationToken);
    }

    private static ProviderMessageKind ProviderKindOf(MessageKind kind) => kind switch
    {
        MessageKind.Image => ProviderMessageKind.Image,
        MessageKind.Audio => ProviderMessageKind.Audio,
        _ => ProviderMessageKind.Document,
    };

    /// <summary>O conteúdo do arquivo, ou nulo se já não existe ou não tem o tamanho que a mensagem declara (nunca se envia um arquivo diferente do que o atendente viu).</summary>
    private async Task<byte[]?> ReadFileAsync(Guid messageId, long expectedSize, CancellationToken cancellationToken)
    {
        await using var fluxo = await _files.OpenAsync(messageId, cancellationToken);
        if (fluxo is null)
        {
            return null;
        }

        using var memoria = new MemoryStream(expectedSize is > 0 and <= int.MaxValue ? (int)expectedSize : 0);
        await fluxo.CopyToAsync(memoria, cancellationToken);
        return memoria.Length == expectedSize ? memoria.ToArray() : null;
    }

    /// <summary>O modelo de um envio, os parâmetros na ordem do modelo ou o motivo de não poder enviar.</summary>
    private sealed record TemplateSendPlan(ChannelTemplate? Template, IReadOnlyList<ProviderTemplateParameter> Parameters, string? FailureCode);

    private async Task<TemplateSendPlan> PlanTemplateAsync(ChannelData dados, Guid accountId, CancellationToken cancellationToken)
    {
        static TemplateSendPlan Fail(string codigo) => new(null, Array.Empty<ProviderTemplateParameter>(), codigo);

        if (!dados.Values.TryGetValue(TemplateRenderer.KeyTemplateId, out var texto) || !Guid.TryParse(texto, out var id) || id == Guid.Empty)
        {
            return Fail("template-invalid");
        }

        var modelo = await _repository.FindTemplateReadOnlyAsync(id, cancellationToken);
        if (modelo is null)
        {
            return Fail("template-not-found");
        }

        // O modelo é da conta do número: uma mensagem nunca sai por um modelo de outra conta (mesmo do mesmo tenant).
        if (modelo.AccountId != accountId)
        {
            return Fail("template-account-mismatch");
        }

        if (!modelo.IsSendable)
        {
            return Fail("template-not-approved");
        }

        // A estrutura gravada com a mensagem precisa ser a do modelo de agora: se o administrador recadastrou o modelo com outros parâmetros, não se monta um envio pela metade.
        var recebidos = dados.Values.Where(par => par.Key.StartsWith(TemplateRenderer.KeyParameterPrefix, StringComparison.Ordinal)).ToDictionary(par => par.Key, par => par.Value, StringComparer.Ordinal);
        if (recebidos.Count != modelo.Parameters.Count || modelo.Parameters.Any(p => !recebidos.ContainsKey(TemplateRenderer.ChannelDataKey(p.Key))))
        {
            return Fail("template-changed");
        }

        var parametros = modelo.Parameters
            .Select(p => new ProviderTemplateParameter(p.Component, p.Index, p.Name, recebidos[TemplateRenderer.ChannelDataKey(p.Key)]))
            .ToList();
        return new TemplateSendPlan(modelo, parametros, null);
    }

    private async Task MarkTemplateUnavailableAsync(Guid templateId, string? errorCode, CancellationToken cancellationToken)
    {
        var novaSituacao = errorCode switch
        {
            "132015" => (TemplateStatus?)TemplateStatus.Paused,
            "132016" or "132001" => TemplateStatus.Disabled,
            _ => null,
        };
        if (novaSituacao is null)
        {
            return;
        }

        try
        {
            var modelo = await _repository.FindTemplateAsync(templateId, cancellationToken);
            if (modelo is not null && modelo.SetStatus(novaSituacao.Value, _clock.GetUtcNow()))
            {
                await _repository.SaveChangesAsync(cancellationToken);
                await _audit.WriteAsync(new AuditEntry("channels.template.status.provider-error", "channel-template", templateId.ToString(), AuditOutcome.Success), cancellationToken);
            }
        }
        catch (ChannelsConflictException)
        {
            // Outra operação mexeu no modelo ao mesmo tempo: o desfecho da mensagem já está gravado, e o próximo envio com o mesmo erro tenta de novo.
        }
    }

    private static TimeSpan PairBackoff(int attempts) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(4, Math.Max(0, attempts - 1))));

    public override bool HandlesDeadLetters => true;

    /// <summary>
    /// O evento de envio MORREU no relay (tentativas esgotadas, lease vencido sem conclusão ou falha permanente): a mensagem NÃO pode ficar parada sem ninguém saber. O estado final é gravado e informado ao
    /// Conversations (que publica <c>OutboundMessageFailedV1</c> e mostra a falha ao atendente), SEM nova chamada ao provedor: sem intenção gravada, nada foi enviado (<c>Failed</c>); com a recusa transitória
    /// anterior, nada saiu (<c>Failed</c>); com um despacho em andamento (<see cref="OutboundState.Dispatching"/>), o provedor PODE ter recebido (<c>SendUnknown</c>, nunca <c>Failed</c> nem reenvio, EFX-3); o que
    /// já tem desfecho só é informado, se ainda não foi. Idempotente: repetir não muda nada.
    /// </summary>
    protected override async Task<HandlerResult> HandleDeadAsync(EventEnvelope envelope, OutboundMessageRequestedV1 payload, string deadReason, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveDeadAsync(envelope, payload, cancellationToken);
        }
        catch (OutboundStateConflictException)
        {
            // Um handler com o lease vencido (ou outra varredura) gravou o desfecho primeiro: o que está gravado vale, e a próxima varredura relê e só informa o Conversations, se faltar.
            ChannelsMetrics.OutboundStateConflict.Add(1);
            return HandlerResult.Retry(null, "outbound-state-conflict");
        }
    }

    private async Task<HandlerResult> ResolveDeadAsync(EventEnvelope envelope, OutboundMessageRequestedV1 payload, CancellationToken cancellationToken)
    {
        var agora = _clock.GetUtcNow();
        var existente = await _store.FindByOperationAsync(payload.OperationId, cancellationToken);

        OutboundMessage mensagem;
        if (existente is null)
        {
            // A intenção é gravada ANTES de qualquer chamada ao provedor: sem ela, nunca houve chamada.
            mensagem = OutboundMessage.Intend(payload.MessageId, envelope.TenantId, payload.OperationId, payload.ChannelInstanceId, agora);
            if (!await _store.TryAddAsync(mensagem, cancellationToken))
            {
                return HandlerResult.Retry(null, "concurrent-dead"); // outro processo gravou ao mesmo tempo: a próxima varredura relê
            }

            mensagem.MarkFailed("relay-dead", agora);
            ChannelsMetrics.OutboundFailed.Add(1);
            await _store.SaveAsync(cancellationToken);
        }
        else if (existente.MayHaveReachedProvider)
        {
            // A chamada pode ter saído: nunca reenviar e nunca afirmar que falhou.
            existente.MarkSendUnknown("interrupted", agora);
            ChannelsMetrics.OutboundUnknown.Add(1);
            await _store.SaveAsync(cancellationToken);
            mensagem = existente;
        }
        else if (existente.IsResolved)
        {
            mensagem = existente; // já tem desfecho: só falta informar, se não foi
        }
        else
        {
            // Só a intenção (nenhuma chamada feita) ou uma recusa transitória anterior: o provedor não recebeu nada.
            existente.MarkFailed("relay-dead", agora);
            ChannelsMetrics.OutboundFailed.Add(1);
            await _store.SaveAsync(cancellationToken);
            mensagem = existente;
        }

        return await NotifyAsync(mensagem, cancellationToken);
    }

    private async Task<HandlerResult> FailAsync(OutboundMessage mensagem, string codigo, CancellationToken cancellationToken)
    {
        mensagem.MarkFailed(codigo, _clock.GetUtcNow());
        ChannelsMetrics.OutboundFailed.Add(1);
        await _store.SaveAsync(cancellationToken);
        return await NotifyAsync(mensagem, cancellationToken);
    }

    /// <summary>Informa o desfecho ao Conversations (uma vez). Um desfecho gravado e não informado é refeito aqui na próxima entrega, sem tocar no provedor.</summary>
    private async Task<HandlerResult> NotifyAsync(OutboundMessage mensagem, CancellationToken cancellationToken)
    {
        if (mensagem.Notified)
        {
            return HandlerResult.Success;
        }

        switch (mensagem.State)
        {
            case OutboundState.Sent:
                await _ingestion.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(mensagem.Id, DeliveryState.Sent, null, EvidenceSource.ProviderStatus), cancellationToken);
                break;

            case OutboundState.Failed:
                await _ingestion.RecordDeliveryStatusAsync(
                    new RecordDeliveryStatusCommand(mensagem.Id, DeliveryState.Failed, $"send-failed.{mensagem.ErrorCode ?? "unknown"}", EvidenceSource.ProviderRejection), cancellationToken);
                break;

            default:
                // SendUnknown só sai de "Enviando": garante o estado antes (idempotente: o que não avança é ignorado).
                await _ingestion.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(mensagem.Id, DeliveryState.Sending, null), cancellationToken);
                await _ingestion.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(mensagem.Id, DeliveryState.SendUnknown, null, EvidenceSource.ProviderStatus), cancellationToken);
                break;
        }

        mensagem.MarkNotified(_clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        return HandlerResult.Success;
    }
}
