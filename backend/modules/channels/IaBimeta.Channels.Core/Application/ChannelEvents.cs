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
}
