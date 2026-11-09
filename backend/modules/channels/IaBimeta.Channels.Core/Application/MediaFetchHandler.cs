using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>
/// O job de baixar o arquivo de uma mensagem de mídia (ADR 0028), entregue pelo relay do Channels com o contexto de SISTEMA do tenant do envelope. O estado e as regras do arquivo são do Conversations: este handler
/// pede o PLANO (identificador da mídia no provedor, tipo e limites), baixa pelo adaptador, com o token da conta só nos cabeçalhos das chamadas ao provedor, e entrega os bytes (que o Conversations valida e guarda) ou
/// registra por que não deu. É idempotente: um arquivo já resolvido não é baixado de novo, e uma entrega repetida (relay, reinício do Worker) termina no mesmo estado. Falha passageira e credencial recusada
/// repetem com espera, e esgotadas as tentativas o evento morre e o arquivo fica "indisponível" (<see cref="HandleDeadAsync"/>): nunca fica "processando" para sempre.
/// </summary>
public sealed class MediaFetchRequestedHandler : EventHandlerBase<ChannelMediaFetchRequestedV1>
{
    private readonly IAttachmentCommands _attachments;
    private readonly IChannelRepository _repository;
    private readonly ISecretProtector _protector;
    private readonly ChannelProviderAdapters _adapters;

    public MediaFetchRequestedHandler(IAttachmentCommands attachments, IChannelRepository repository, ISecretProtector protector, ChannelProviderAdapters adapters)
    {
        _attachments = attachments;
        _repository = repository;
        _protector = protector;
        _adapters = adapters;
    }

    public override string EventType => ChannelEventTypes.MediaFetchRequested;

    public override int EventVersion => 1;

    public override bool HandlesDeadLetters => true;

    protected override async Task<HandlerResult> HandleAsync(EventEnvelope envelope, ChannelMediaFetchRequestedV1 payload, CancellationToken cancellationToken)
    {
        var plano = await _attachments.GetFetchPlanAsync(payload.MessageId, cancellationToken);
        if (plano is null)
        {
            return HandlerResult.Dead("attachment-not-found");
        }

        // Já resolvido (baixado antes, recusado, expirado): nada a fazer. É o que torna a entrega repetida inofensiva.
        if (plano.Status != AttachmentStatus.Pending)
        {
            return HandlerResult.Success;
        }

        var instancia = await _repository.FindInstanceReadOnlyAsync(new ChannelInstanceId(payload.ChannelInstanceId), cancellationToken);
        if (instancia is null)
        {
            return await FailAsync(payload.MessageId, AttachmentStatus.Unavailable, "channel-not-found", cancellationToken);
        }

        var adaptador = _adapters.Find(instancia.Provider);
        if (adaptador is null)
        {
            return HandlerResult.Retry(TimeSpan.FromMinutes(1), "provider-unavailable");
        }

        var token = await ChannelSecretReader.ReadAsync(_repository, _protector, envelope.TenantId, instancia.AccountId, ChannelSecretKind.AccessToken, cancellationToken);
        if (token is null)
        {
            return HandlerResult.Retry(TimeSpan.FromMinutes(5), "account-credentials-missing");
        }

        var resultado = await adaptador.DownloadMediaAsync(new ProviderMediaRequest(token, plano.ProviderMediaId, plano.MaxBytes), cancellationToken);
        switch (resultado.Outcome)
        {
            case ProviderMediaOutcome.Downloaded:
                var entrega = await _attachments.CompleteAsync(new CompleteAttachmentCommand(payload.MessageId, resultado.Content!, resultado.MimeType, resultado.Sha256), cancellationToken);
                if (entrega.Outcome is AttachmentChangeOutcome.Changed)
                {
                    ChannelsMetrics.MediaDownloaded.Add(1);
                }
                else if (entrega.Outcome is AttachmentChangeOutcome.Rejected)
                {
                    ChannelsMetrics.MediaFailed.Add(1);
                }

                return HandlerResult.Success;

            case ProviderMediaOutcome.NotFound:
                return await FailAsync(payload.MessageId, AttachmentStatus.Unavailable, "expired", cancellationToken);

            case ProviderMediaOutcome.TooLarge:
                return await FailAsync(payload.MessageId, AttachmentStatus.Rejected, "too-large", cancellationToken);

            case ProviderMediaOutcome.Rejected:
                return await FailAsync(payload.MessageId, AttachmentStatus.Unavailable, resultado.ErrorCode ?? "provider-rejected", cancellationToken);

            case ProviderMediaOutcome.AccountError:
                // A credencial foi recusada: o administrador pode corrigi-la a tempo (o identificador da mídia vale 7 dias). Repete com espera; se esgotar, o arquivo fica indisponível.
                return HandlerResult.Retry(TimeSpan.FromMinutes(2), "credential-rejected");

            default:
                return HandlerResult.Retry(null, "provider-transient");
        }
    }

    /// <summary>O evento morreu (tentativas esgotadas): o arquivo não pode ficar "processando" para sempre, então vira "indisponível". Idempotente (só sai de Pending).</summary>
    protected override async Task<HandlerResult> HandleDeadAsync(EventEnvelope envelope, ChannelMediaFetchRequestedV1 payload, string deadReason, CancellationToken cancellationToken)
    {
        var resultado = await _attachments.FailAsync(new FailAttachmentCommand(payload.MessageId, AttachmentStatus.Unavailable, "download-failed"), cancellationToken);
        if (resultado.Outcome == AttachmentChangeOutcome.Changed)
        {
            ChannelsMetrics.MediaFailed.Add(1);
        }

        return HandlerResult.Success;
    }

    private async Task<HandlerResult> FailAsync(Guid messageId, AttachmentStatus status, string code, CancellationToken cancellationToken)
    {
        var resultado = await _attachments.FailAsync(new FailAttachmentCommand(messageId, status, code), cancellationToken);
        if (resultado.Outcome == AttachmentChangeOutcome.Changed)
        {
            ChannelsMetrics.MediaFailed.Add(1);
        }

        return HandlerResult.Success;
    }
}
