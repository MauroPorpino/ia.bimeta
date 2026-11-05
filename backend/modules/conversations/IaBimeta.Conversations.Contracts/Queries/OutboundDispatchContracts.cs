using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Contracts.Queries;

/// <summary>
/// A leitura que o Sender do Channels precisa para despachar uma mensagem de saída: o que enviar e para quem. Só o contexto de SISTEMA (o worker do Channels, depois de receber
/// <c>OutboundMessageRequestedV1</c>) a chama. O tenant é o do contexto confiável: uma mensagem de outro tenant responde como inexistente. Não aplica permissões de usuário e nunca
/// devolve mensagem de entrada (do cliente): o Sender só despacha o que o Conversations liberou para envio.
/// </summary>
public interface IOutboundDispatchQueries
{
    /// <summary>O despacho da mensagem de saída, ou <c>null</c> se ela não existe no tenant do contexto ou não é de saída.</summary>
    /// <exception cref="AccessDeniedException">O contexto não é de sistema.</exception>
    Task<OutboundDispatchView?> GetAsync(Guid messageId, CancellationToken cancellationToken = default);
}

/// <param name="ChannelInstanceId">De QUAL número a mensagem sai (a instância da conversa).</param>
/// <param name="ContactExternalRef">O destinatário no provedor (no WhatsApp, o wa_id), do contato da sessão.</param>
/// <param name="Text">O texto a enviar, exatamente como foi aceito.</param>
/// <param name="DeliveryState">O estado de entrega atual da mensagem (o Sender não envia o que já saiu do estado de pedido).</param>
public sealed record OutboundDispatchView(
    Guid MessageId,
    Guid SessionId,
    ChannelInstanceId ChannelInstanceId,
    string ContactExternalRef,
    MessageAuthor Author,
    string? Text,
    DeliveryState? DeliveryState);
