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
/// <param name="ChannelData">Os metadados de envio gravados com a mensagem (por exemplo, o modelo de mensagem e os parâmetros que o Sender monta); nulo na mensagem de texto livre.</param>
/// <param name="Attachment">O arquivo que o atendente enviou (imagem, áudio ou documento; ADR 0031), se a mensagem é de mídia e o arquivo ainda está guardado; nulo na mensagem de texto. Uma mensagem de mídia SEM este dado
/// (<see cref="Kind"/> de mídia) é a de um arquivo que já não existe: o Sender não a envia.</param>
/// <param name="Kind">O tipo da mensagem (texto, imagem, áudio ou documento).</param>
public sealed record OutboundDispatchView(
    Guid MessageId,
    Guid SessionId,
    ChannelInstanceId ChannelInstanceId,
    string ContactExternalRef,
    MessageAuthor Author,
    string? Text,
    DeliveryState? DeliveryState,
    ChannelData? ChannelData = null,
    OutboundAttachmentInfo? Attachment = null,
    MessageKind Kind = MessageKind.Text);

/// <summary>
/// A leitura do CONTEÚDO do arquivo de uma mensagem de saída, para o Sender subir ao provedor (ADR 0031). Só o contexto de SISTEMA a chama; o tenant é o do contexto confiável; só devolve o arquivo de uma mensagem
/// de SAÍDA do tenant com o arquivo ainda disponível (nunca o de uma mensagem do cliente). Não há URL: o conteúdo só sai por este fluxo.
/// </summary>
public interface IOutboundAttachmentReader
{
    /// <summary>O conteúdo do arquivo (a posse do fluxo é de quem recebe), ou <c>null</c> se a mensagem não existe no tenant, não é de saída ou o arquivo já não está disponível (por exemplo, passou do prazo de guarda).</summary>
    /// <exception cref="AccessDeniedException">O contexto não é de sistema.</exception>
    Task<Stream?> OpenAsync(Guid messageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A leitura de DIAGNÓSTICO das mensagens de saída que ainda não tiveram desfecho: pedidas ao provedor (<c>Requested</c>, <c>Queued</c> ou <c>Sending</c>) e paradas há mais que o tempo dado. É o sinal de que
/// uma entrega pode estar presa (no relay, no Sender ou numa conta limitada), independente do mecanismo que a leva. Respeita as permissões do usuário: só as organizações em que ele lê conversas
/// (<c>ConversationRead</c>); sem leitura em nenhuma, a operação é negada. O tenant é o do contexto confiável.
/// </summary>
public interface IOutboundDeliveryQueries
{
    /// <summary>As mais antigas primeiro, até <paramref name="take"/> (1 a 200).</summary>
    /// <exception cref="AccessDeniedException">O usuário não lê conversas em organização alguma.</exception>
    Task<IReadOnlyList<UndeliveredOutboundView>> ListUndeliveredAsync(DateTimeOffset createdBefore, int take, CancellationToken cancellationToken = default);
}

/// <param name="CreatedAt">Quando a mensagem de saída foi aceita e liberada para envio.</param>
public sealed record UndeliveredOutboundView(
    Guid MessageId,
    Guid SessionId,
    Guid OrganizationId,
    ChannelInstanceId ChannelInstanceId,
    DeliveryState DeliveryState,
    DateTimeOffset CreatedAt);
