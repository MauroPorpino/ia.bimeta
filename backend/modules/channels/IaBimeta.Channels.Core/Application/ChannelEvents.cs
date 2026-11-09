using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Channels.Application;

/// <summary>Os tipos dos eventos do Inbox do Channels. Internos ao módulo: só o Receiver os grava e só os handlers do próprio Channels os consomem.</summary>
public static class ChannelEventTypes
{
    public const string ProviderMessageReceived = "channels.provider-message-received";
    public const string ProviderStatusReceived = "channels.provider-status-received";

    /// <summary>O pedido interno de baixar o arquivo de uma mídia recebida (ADR 0028): gravado depois de a mensagem ser aceita, entregue pelo mesmo relay (com espera, tentativas e mortos).</summary>
    public const string MediaFetchRequested = "channels.media-fetch-requested";
}

/// <summary>Uma mensagem do cliente, já autenticada e atribuída a uma instância do tenant (o Receiver conferiu conta, número e situação). Carrega o texto do cliente: vive no Inbox só pelo tempo da retenção.</summary>
public sealed record ProviderMessageReceivedV1(
    Guid ChannelInstanceId,
    string ProviderMessageId,
    string ContactExternalRef,
    string? ContactDisplayName,
    DateTimeOffset ProviderTimestamp,
    ProviderMessageKind Kind,
    string? Text,
    IReadOnlyDictionary<string, string>? ChannelData);

/// <summary>
/// Pede o download do arquivo da mensagem de mídia já registrada no Conversations (ADR 0028). Só identificadores: o identificador da mídia no provedor, o tipo e os limites vêm do plano do Conversations na hora de
/// baixar. O identificador do evento é determinístico por mensagem, então repetir o pedido é uma linha só.
/// </summary>
public sealed record ChannelMediaFetchRequestedV1(Guid ChannelInstanceId, Guid MessageId);

/// <summary>Um status de entrega, já autenticado e atribuído a uma instância do tenant.</summary>
public sealed record ProviderStatusReceivedV1(
    Guid ChannelInstanceId,
    string ProviderMessageId,
    ProviderDeliveryStatus Status,
    DateTimeOffset ProviderTimestamp,
    string? OpaqueCallbackData,
    string? ErrorCode);

/// <summary>Um GUID DETERMINÍSTICO (SHA-256 truncado) a partir de um texto: a chave de deduplicação do Inbox é função da conta e do identificador do provedor, nunca sorteada.</summary>
public static class DeterministicGuid
{
    public static Guid From(string texto)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(texto));
        var bytes = hash.AsSpan(0, 16).ToArray();

        // Versão 8 (definida pela aplicação) e variante RFC 4122: um GUID bem formado, nunca confundido com os aleatórios.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

/// <summary>As métricas do Channels (<c>System.Diagnostics.Metrics</c>, sem pacote novo; ADR 0011, seção C). Sem tenant nem conteúdo como rótulo: cardinalidade fixa.</summary>
public static class ChannelsMetrics
{
    public const string MeterName = "IaBimeta.Channels";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> WebhookStored = Meter.CreateCounter<long>("channels.webhook.stored", description: "Eventos de webhook gravados no Inbox.");

    public static readonly Counter<long> WebhookDuplicate = Meter.CreateCounter<long>("channels.webhook.duplicate", description: "Eventos repetidos (deduplicados).");

    public static readonly Counter<long> WebhookDiscarded = Meter.CreateCounter<long>("channels.webhook.discarded", description: "Eventos descartados (número desconhecido, de outra conta ou canal inativo).");

    public static readonly Counter<long> WebhookRejected = Meter.CreateCounter<long>("channels.webhook.rejected", description: "Webhooks recusados (assinatura inválida ou corpo inválido).");

    public static readonly Counter<long> OutboundSent = Meter.CreateCounter<long>("channels.outbound.sent", description: "Mensagens aceitas pelo provedor.");

    public static readonly Counter<long> OutboundFailed = Meter.CreateCounter<long>("channels.outbound.failed", description: "Mensagens com falha definitiva.");

    public static readonly Counter<long> OutboundUnknown = Meter.CreateCounter<long>("channels.outbound.send_unknown", description: "Envios de resultado incerto (nunca reenviados).");

    public static readonly Counter<long> OutboundStateConflict = Meter.CreateCounter<long>("channels.outbound.state_conflict", description: "Gravações de despacho descartadas porque outro processo já gravou o estado (lease vencido ou tratamento de morto concorrente).");

    public static readonly Counter<long> OutboundThrottled = Meter.CreateCounter<long>("channels.outbound.throttled", description: "Entregas devolvidas ao relay porque o balde da conta (ou a espera do provedor) não liberou o envio a tempo (SND-2).");

    public static readonly Counter<long> OutboundRateLimited = Meter.CreateCounter<long>("channels.outbound.rate_limited", description: "Recusas de limite de taxa do provedor (a conta passa a esperar, SND-2).");

    public static readonly Counter<long> AccountPaused = Meter.CreateCounter<long>("channels.account.paused", description: "Contas pausadas por erro de conta do provedor ou por ação do administrador (SND-6).");

    public static readonly Counter<long> MediaDownloaded = Meter.CreateCounter<long>("channels.media.downloaded", description: "Arquivos de mídia baixados e entregues ao Conversations.");

    public static readonly Counter<long> MediaFailed = Meter.CreateCounter<long>("channels.media.failed", description: "Arquivos de mídia que não ficaram disponíveis (expirados, grandes demais, recusados ou sem sucesso depois das tentativas).");

    public static readonly Counter<long> OutboundBlockedByPause = Meter.CreateCounter<long>("channels.outbound.blocked_by_pause", description: "Mensagens recusadas porque a conta está pausada (SND-6).");
}
