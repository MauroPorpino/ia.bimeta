using IaBimeta.Channels.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>O estado do despacho de uma mensagem de saída (ADR 0011, E.3 e E.4).</summary>
public enum OutboundState
{
    /// <summary>A intenção de enviar foi gravada ANTES de chamar o provedor (EFX-2): se o processo cair aqui, o desfecho é desconhecido e a mensagem nunca é reenviada.</summary>
    Dispatching = 1,

    /// <summary>O provedor aceitou a mensagem.</summary>
    Sent = 2,

    /// <summary>Falha definitiva desta mensagem (recusa do provedor, janela fechada, erro da conta, canal inativo).</summary>
    Failed = 3,

    /// <summary>Resultado ambíguo (tempo esgotado, rede, 5xx): a mensagem PODE ter sido enviada. NUNCA é reenviada automaticamente (EFX-3).</summary>
    SendUnknown = 4,

    /// <summary>O provedor recusou de forma transitória, com prova de que nada foi enviado: pode tentar de novo.</summary>
    Retry = 5,
}

/// <summary>
/// O despacho de UMA mensagem de saída pelo Sender (<c>channels.outbound_messages</c>). A chave é a mensagem do Conversations; o <see cref="OperationId"/> é a chave de idempotência do efeito externo
/// (não muda entre tentativas) e é único por tenant. Não guarda o texto (ele vive no Conversations e é lido na hora de enviar). <see cref="Notified"/> diz se o desfecho já foi informado ao Conversations:
/// um desfecho gravado e não informado é refeito na próxima entrega, sem reenviar nada ao provedor.
/// </summary>
public sealed class OutboundMessage : ITenantOwned
{
    private OutboundMessage(Guid id, TenantId tenantId, Guid operationId, ChannelInstanceId channelInstanceId, OutboundState state, int attempts, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        OperationId = operationId;
        ChannelInstanceId = channelInstanceId;
        State = state;
        Attempts = attempts;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>A mensagem de saída no Conversations.</summary>
    public Guid Id { get; }

    public TenantId TenantId { get; }

    public Guid OperationId { get; }

    public ChannelInstanceId ChannelInstanceId { get; }

    public OutboundState State { get; private set; }

    /// <summary>O identificador que o provedor deu à mensagem, quando aceitou.</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>As chamadas ao provedor que chegaram a ser feitas.</summary>
    public int Attempts { get; private set; }

    /// <summary>O código curto do erro (nunca mensagem livre do provedor, nunca conteúdo).</summary>
    public string? ErrorCode { get; private set; }

    public bool Notified { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsResolved => State is OutboundState.Sent or OutboundState.Failed or OutboundState.SendUnknown;

    /// <summary>
    /// A chamada ao provedor PODE estar em curso ou ter saído sem desfecho gravado: o despacho está em <see cref="OutboundState.Dispatching"/> e a tentativa já começou (<see cref="Attempts"/> acima de zero, gravado
    /// ANTES da chamada). A intenção sozinha (<see cref="Intend"/>, zero tentativas) NÃO é isso: nenhuma chamada foi feita, e a entrega pode seguir (uma espera por vazão, por exemplo) ou falhar com certeza.
    /// Só neste caso o desfecho é <see cref="OutboundState.SendUnknown"/>, nunca reenvio nem falha afirmada (EFX-3).
    /// </summary>
    public bool MayHaveReachedProvider => State == OutboundState.Dispatching && Attempts > 0;

    /// <summary>Grava a intenção de enviar (estado <see cref="OutboundState.Dispatching"/>), ANTES da chamada ao provedor.</summary>
    public static OutboundMessage Intend(Guid messageId, TenantId tenantId, Guid operationId, ChannelInstanceId channelInstanceId, DateTimeOffset now) =>
        new(messageId, tenantId, operationId, channelInstanceId, OutboundState.Dispatching, 0, now);

    /// <summary>Uma nova chamada ao provedor vai começar (a primeira, ou depois de uma recusa transitória provada).</summary>
    public void BeginAttempt(DateTimeOffset now)
    {
        State = OutboundState.Dispatching;
        Attempts++;
        UpdatedAt = now;
    }

    /// <summary>
    /// A chamada que ia começar foi RECUSADA antes de sair (a verificação final de admissão, P1-04 da 2.ª revisão): desfaz a contagem da tentativa, que não aconteceu. Sem tentativas anteriores volta à intenção (zero tentativas,
    /// que não "pode ter chegado ao provedor"); com tentativas anteriores provadamente recusadas volta a <see cref="OutboundState.Retry"/>. Só vale para um despacho em <see cref="OutboundState.Dispatching"/> com tentativa aberta.
    /// </summary>
    public void RevertAttempt(DateTimeOffset now)
    {
        if (State != OutboundState.Dispatching || Attempts == 0)
        {
            return;
        }

        Attempts--;
        State = Attempts == 0 ? OutboundState.Dispatching : OutboundState.Retry;
        UpdatedAt = now;
    }

    public void MarkSent(string providerMessageId, DateTimeOffset now)
    {
        State = OutboundState.Sent;
        ProviderMessageId = providerMessageId;
        ErrorCode = null;
        UpdatedAt = now;
    }

    public void MarkFailed(string errorCode, DateTimeOffset now)
    {
        State = OutboundState.Failed;
        ErrorCode = errorCode;
        UpdatedAt = now;
    }

    public void MarkSendUnknown(string? errorCode, DateTimeOffset now)
    {
        State = OutboundState.SendUnknown;
        ErrorCode = errorCode;
        UpdatedAt = now;
    }

    public void MarkRetry(string? errorCode, DateTimeOffset now)
    {
        State = OutboundState.Retry;
        ErrorCode = errorCode;
        UpdatedAt = now;
    }

    public void MarkNotified(DateTimeOffset now)
    {
        Notified = true;
        UpdatedAt = now;
    }
}
