namespace IaBimeta.Channels.Contracts.Providers;

// A porta do ADAPTADOR de provedor do Channels (ADR 0011, seção B.2). O adaptador (um projeto por provedor, em backend/adapters) só sabe falar com o provedor: verifica a autenticidade
// do webhook, normaliza o que chegou, envia e declara a estratégia de efeito. NÃO conhece tenant, sessão, fila nem banco: o Channels (Receiver, processador do Inbox e Sender) é quem resolve
// o tenant, grava, deduplica, ordena e chama o Conversations. Segredos (AppSecret, token de acesso) chegam por chamada, decifrados na hora, e nunca são guardados nem registrados.

/// <summary>O tipo normalizado de uma mensagem recebida. O Channels o traduz para o tipo do Conversations.</summary>
public enum ProviderMessageKind
{
    Text = 1,
    Audio = 2,
    Image = 3,
    Document = 4,
    Video = 5,

    /// <summary>Tipo sem tratamento no MVP (localização, contato, sticker, reação, interativo...). Vira conteúdo indisponível para o atendente.</summary>
    Other = 6,
}

/// <summary>Uma mensagem recebida do cliente, normalizada.</summary>
/// <param name="ExternalAccountId">O identificador do número no provedor (no WhatsApp, o Phone Number ID): é o que a rota do Channels resolve.</param>
/// <param name="ProviderMessageId">O identificador da mensagem no provedor: a chave de deduplicação.</param>
/// <param name="ContactExternalRef">O contato no provedor (no WhatsApp, o wa_id).</param>
/// <param name="ContactDisplayName">O nome que o provedor informa para o contato, se houver.</param>
/// <param name="ProviderTimestamp">O instante da mensagem no provedor (o relógio do servidor é outro).</param>
/// <param name="Text">O texto exatamente como veio; nulo nas mídias (o texto de legenda, quando houver, vem aqui).</param>
/// <param name="ChannelData">Metadados opacos da mídia ou do tipo sem tratamento (por exemplo, <c>media_id</c> e <c>mime_type</c>), só texto; nunca o conteúdo da mídia.</param>
public sealed record ProviderInboundMessage(
    string ExternalAccountId,
    string ProviderMessageId,
    string ContactExternalRef,
    string? ContactDisplayName,
    DateTimeOffset ProviderTimestamp,
    ProviderMessageKind Kind,
    string? Text,
    IReadOnlyDictionary<string, string>? ChannelData = null);

/// <summary>O status de uma mensagem de saída informado pelo provedor.</summary>
public enum ProviderDeliveryStatus
{
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4,
}

/// <summary>Um status de entrega, normalizado.</summary>
/// <param name="ProviderMessageId">O identificador que o provedor deu à mensagem de saída.</param>
/// <param name="OpaqueCallbackData">O dado opaco que o Channels mandou no envio e o provedor devolve (a correlação com a nossa mensagem); nulo se o provedor não o devolveu.</param>
/// <param name="ErrorCode">O código de erro do provedor numa falha (texto, só o código, nunca a mensagem livre).</param>
public sealed record ProviderStatusUpdate(
    string ExternalAccountId,
    string ProviderMessageId,
    ProviderDeliveryStatus Status,
    DateTimeOffset ProviderTimestamp,
    string? OpaqueCallbackData,
    string? ErrorCode);

/// <summary>O que um webhook autenticado trouxe, já normalizado. <paramref name="IgnoredEntries"/> conta o que o adaptador reconhece e descarta (por exemplo, eventos de outro objeto), para a métrica.</summary>
public sealed record ParsedWebhook(
    IReadOnlyList<ProviderInboundMessage> Messages,
    IReadOnlyList<ProviderStatusUpdate> Statuses,
    int IgnoredEntries);

/// <summary>Como o provedor trata o efeito externo do envio (ADR 0011, E.4 e EFX-5).</summary>
public enum ProviderEffectStrategy
{
    /// <summary>(a) Idempotência comprovada por documentação e teste: repetir com a mesma chave não duplica.</summary>
    Idempotent = 1,

    /// <summary>(b) Reconciliação por identificador próprio: consultar o provedor resolve a dúvida.</summary>
    Reconcilable = 2,

    /// <summary>(c) Nenhuma: resultado ambíguo vira <c>SendUnknown</c> e nunca é reenviado automaticamente. É a estratégia do WhatsApp.</summary>
    None = 3,
}

/// <summary>O desfecho de uma tentativa de envio.</summary>
public enum ProviderSendOutcome
{
    /// <summary>O provedor aceitou a mensagem (resposta de sucesso com o identificador dela).</summary>
    Accepted = 1,

    /// <summary>Recusa definitiva desta mensagem (destinatário inválido, conteúdo recusado...): falha, sem nova tentativa.</summary>
    RejectedPermanent = 2,

    /// <summary>Recusa transitória com prova de que NADA foi enviado (limite de taxa, indisponibilidade declarada): pode tentar de novo, com espera.</summary>
    RejectedTransient = 3,

    /// <summary>A janela de atendimento do provedor está fechada para este destinatário (mensagem livre fora da janela): falha definitiva.</summary>
    WindowClosed = 4,

    /// <summary>Erro da conta (credencial inválida ou expirada, conta bloqueada): pausa a conta e alerta; não é falha da mensagem.</summary>
    AccountError = 5,

    /// <summary>Resultado ambíguo (tempo esgotado, rede caída, 5xx, resposta ilegível): a mensagem PODE ter sido enviada. Nunca é reenviada automaticamente.</summary>
    Unknown = 6,
}

/// <summary>O pedido de envio de uma mensagem de texto livre.</summary>
/// <param name="AccessToken">A credencial da conta, decifrada para ESTA chamada: vai só no cabeçalho deste pedido e nunca é guardada nem registrada.</param>
/// <param name="ExternalAccountId">O número de origem no provedor (Phone Number ID).</param>
/// <param name="To">O destinatário no provedor (wa_id).</param>
/// <param name="OpaqueCallbackData">O dado opaco que o provedor devolve nos status (a correlação com a nossa mensagem); até 512 caracteres.</param>
public sealed record ProviderSendRequest(string AccessToken, string ExternalAccountId, string To, string Text, string? OpaqueCallbackData);

/// <summary>A quem o limite de taxa do provedor se aplica (só vale em <see cref="ProviderSendOutcome.RejectedTransient"/> de limite de taxa).</summary>
public enum ProviderRateLimitScope
{
    /// <summary>A conta (ou o número, ou o aplicativo) inteira: nenhum envio dela deve sair antes do fim da espera (SND-2).</summary>
    Account = 1,

    /// <summary>Só o par remetente-destinatário: os envios para outros destinatários não precisam esperar.</summary>
    Recipient = 2,
}

/// <summary>
/// O resultado do envio. Só o código do erro do provedor entra aqui (nunca a mensagem livre, que pode ecoar dados).
/// <paramref name="RateLimitScope"/> é preenchido quando a recusa transitória é um limite de taxa; nulo nas demais (por exemplo, falha genérica transitória).
/// </summary>
public sealed record ProviderSendResult(
    ProviderSendOutcome Outcome,
    string? ProviderMessageId,
    string? ErrorCode,
    TimeSpan? RetryAfter = null,
    ProviderRateLimitScope? RateLimitScope = null);

/// <summary>O pedido de sonda: confere a credencial e o número no provedor SEM enviar mensagem alguma (só leitura).</summary>
/// <param name="AccessToken">A credencial da conta, decifrada para ESTA chamada (nunca guardada nem registrada).</param>
/// <param name="ExternalAccountId">O número no provedor (Phone Number ID).</param>
public sealed record ProviderProbeRequest(string AccessToken, string ExternalAccountId);

/// <summary>O desfecho da sonda.</summary>
public enum ProviderProbeOutcome
{
    /// <summary>A credencial é aceita e o número existe e é acessível por ela.</summary>
    Ok = 1,

    /// <summary>O provedor recusou a credencial (expirada, inválida, sem permissão) ou a conta está restrita.</summary>
    CredentialRejected = 2,

    /// <summary>O número não existe no provedor ou a credencial não enxerga esse número.</summary>
    NotFound = 3,

    /// <summary>Não foi possível concluir (rede, tempo esgotado, erro do provedor, resposta ilegível): nada se afirma sobre a configuração.</summary>
    Unavailable = 4,
}

/// <summary>O que a sonda leu do número (só dados que o provedor mostra ao dono da conta; nunca credencial).</summary>
/// <param name="VerifiedName">O nome verificado da empresa no provedor.</param>
/// <param name="DisplayPhoneNumber">O número como o provedor o exibe.</param>
/// <param name="QualityRating">A classificação de qualidade do número no provedor (por exemplo, GREEN, YELLOW, RED).</param>
/// <param name="ErrorCode">O código de erro do provedor, só o código (nunca a mensagem livre).</param>
public sealed record ProviderProbeResult(ProviderProbeOutcome Outcome, string? VerifiedName, string? DisplayPhoneNumber, string? QualityRating, string? ErrorCode);

/// <summary>O adaptador de um provedor de canal. Implementado em <c>backend/adapters/IaBimeta.Adapters.*</c>, um por provedor.</summary>
public interface IChannelProviderAdapter
{
    /// <summary>O provedor que este adaptador atende.</summary>
    ChannelProvider Provider { get; }

    /// <summary>A estratégia de efeito DECLARADA (ADR 0011, E.4): declarar (a) ou (b) sem prova é defeito grave.</summary>
    ProviderEffectStrategy EffectStrategy { get; }

    /// <summary>
    /// Verifica a assinatura do webhook sobre o corpo BRUTO (bytes exatamente como chegaram), com o segredo do aplicativo, em tempo constante. Cabeçalho ausente, mal formado ou
    /// diferente: falso. Nunca lança por entrada inválida.
    /// </summary>
    bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader, string appSecret);

    /// <summary>
    /// Normaliza o corpo (já autenticado). Devolve nulo se o corpo não é um webhook deste provedor (JSON inválido ou forma desconhecida). Nunca lança por entrada inválida. Eventos que o
    /// adaptador reconhece mas não trata são contados em <see cref="ParsedWebhook.IgnoredEntries"/>.
    /// </summary>
    ParsedWebhook? Parse(ReadOnlySpan<byte> body);

    /// <summary>
    /// Responde ao pedido de verificação do webhook (o GET com desafio). Verdadeiro, com o texto a devolver, só se o modo e o token conferem (comparação em tempo constante); falso
    /// caso contrário.
    /// </summary>
    bool TryAnswerVerification(string? mode, string? verifyToken, string? challenge, string expectedVerifyToken, out string? response);

    /// <summary>
    /// Envia uma mensagem de texto livre. Não repete a chamada por conta própria (o Channels decide, pela estratégia de efeito) e não lança por falha do provedor: o desfecho vai em
    /// <see cref="ProviderSendResult"/>. Cancelamento do chamador propaga como <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProviderSendResult> SendTextAsync(ProviderSendRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Confere, SEM enviar mensagem, que a credencial é aceita e que o número existe e é acessível por ela (uma leitura no provedor). Não lança por falha do provedor: o desfecho vai em
    /// <see cref="ProviderProbeResult"/>; só o cancelamento do chamador propaga como <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProviderProbeResult> ProbeAsync(ProviderProbeRequest request, CancellationToken cancellationToken);
}
