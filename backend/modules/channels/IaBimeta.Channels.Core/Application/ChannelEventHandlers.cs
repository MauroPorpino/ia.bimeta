using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>
/// O processamento do Inbox, mensagem do cliente (ADR 0011, seção D): o evento já autenticado e atribuído a uma instância vira <c>IngestInboundMessage</c> no Conversations, com o contexto de SISTEMA do tenant do
/// envelope (o relay do Worker o cria, TEN-1). A entrega é pelo menos uma vez, em ordem por lane (instância e contato); a ingestão é idempotente pelo identificador da mensagem no provedor. A unidade e o setor são os
/// PADRÃO do número (sempre entre as autorizadas); quem decide o modo é a configuração do número (no MVP, a IA atende primeiro). Mídia chega só como metadados e conteúdo pendente (DP-05): o arquivo nunca é baixado.
/// </summary>
public sealed class InboundProviderMessageHandler : EventHandlerBase<ProviderMessageReceivedV1>
{
    private readonly IConversationIngestion _ingestion;
    private readonly IChannelRouting _routing;

    public InboundProviderMessageHandler(IConversationIngestion ingestion, IChannelRouting routing)
    {
        _ingestion = ingestion;
        _routing = routing;
    }

    public override string EventType => ChannelEventTypes.ProviderMessageReceived;

    public override int EventVersion => 1;

    protected override async Task<HandlerResult> HandleAsync(EventEnvelope envelope, ProviderMessageReceivedV1 payload, CancellationToken cancellationToken)
    {
        var instancia = new ChannelInstanceId(payload.ChannelInstanceId);
        var rota = await _routing.FindAsync(instancia, cancellationToken);
        if (rota is null)
        {
            return HandlerResult.Dead("channel-not-found");
        }

        // Canal suspenso ou ainda inativo: o evento espera (o relay repete com espera crescente e, esgotadas as tentativas, vai para os mortos, onde a administração o reenfileira).
        if (rota.Status != ChannelStatus.Active)
        {
            return HandlerResult.Retry(TimeSpan.FromMinutes(1), "channel-inactive");
        }

        var tipo = payload.Kind switch
        {
            ProviderMessageKind.Text => MessageKind.Text,
            ProviderMessageKind.Audio => MessageKind.Audio,
            ProviderMessageKind.Image => MessageKind.Image,
            ProviderMessageKind.Document => MessageKind.Document,
            ProviderMessageKind.Video => MessageKind.Video,
            _ => MessageKind.Other,
        };

        // Texto só no tipo texto (o conteúdo das mídias nasce pendente); metadados da mídia vão no bloco opaco do canal.
        var texto = tipo == MessageKind.Text ? payload.Text : null;
        var dados = payload.ChannelData is { Count: > 0 } valores ? new ChannelData(1, valores) : null;

        await _ingestion.IngestInboundMessageAsync(
            new IngestInboundMessageCommand(
                instancia,
                payload.ContactExternalRef,
                payload.ContactDisplayName,
                rota.OrganizationId,
                rota.DefaultBusinessUnitId,
                payload.ProviderMessageId,
                payload.ProviderTimestamp,
                tipo,
                texto,
                DepartmentId: rota.DefaultDepartmentId,
                ChannelDefaultLocale: rota.DefaultLocale,
                InitialHandlingMode: HandlingMode.AiAutonomous,
                ChannelData: dados),
            cancellationToken);
        return HandlerResult.Success;
    }
}

/// <summary>
/// O processamento do Inbox, status de entrega: o status do provedor vira <c>RecordDeliveryStatus</c> no Conversations (monotônico: o que não avança é ignorado). A mensagem é achada pelo dado opaco que o Sender
/// mandou no envio (o identificador da nossa mensagem) ou, na falta dele, pelo identificador do provedor guardado no despacho. Status que chega antes de a mensagem ser conhecida espera (o relay repete).
/// </summary>
public sealed class InboundProviderStatusHandler : EventHandlerBase<ProviderStatusReceivedV1>
{
    private readonly IConversationIngestion _ingestion;
    private readonly IOutboundMessageStore _outbound;

    public InboundProviderStatusHandler(IConversationIngestion ingestion, IOutboundMessageStore outbound)
    {
        _ingestion = ingestion;
        _outbound = outbound;
    }

    public override string EventType => ChannelEventTypes.ProviderStatusReceived;

    public override int EventVersion => 1;

    protected override async Task<HandlerResult> HandleAsync(EventEnvelope envelope, ProviderStatusReceivedV1 payload, CancellationToken cancellationToken)
    {
        Guid? mensagem = Guid.TryParseExact(payload.OpaqueCallbackData, "N", out var correlacionada) ? correlacionada : null;
        if (mensagem is null)
        {
            mensagem = (await _outbound.FindByProviderMessageIdAsync(payload.ProviderMessageId, cancellationToken))?.Id;
        }

        if (mensagem is null)
        {
            return HandlerResult.Retry(null, "message-unknown");
        }

        var estado = payload.Status switch
        {
            ProviderDeliveryStatus.Sent => DeliveryState.Sent,
            ProviderDeliveryStatus.Delivered => DeliveryState.Delivered,
            ProviderDeliveryStatus.Read => DeliveryState.Read,
            _ => DeliveryState.Failed,
        };
        var motivo = estado == DeliveryState.Failed ? $"provider-failed.{payload.ErrorCode ?? "unknown"}" : null;

        var resultado = await _ingestion.RecordDeliveryStatusAsync(new RecordDeliveryStatusCommand(mensagem.Value, estado, motivo, EvidenceSource.ProviderStatus), cancellationToken);
        return resultado.Outcome is DeliveryStatusOutcome.MessageNotFound or DeliveryStatusOutcome.NotAnOutboundMessage
            ? HandlerResult.Dead("message-not-found")
            : HandlerResult.Success;
    }
}
