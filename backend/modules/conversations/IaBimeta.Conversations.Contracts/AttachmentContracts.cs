namespace IaBimeta.Conversations.Contracts;

/// <summary>
/// Em que pé está o ARQUIVO de uma mensagem de mídia do cliente (ADR 0028). O estado do arquivo é independente do <see cref="ContentState"/> da mensagem (que é a interpretação do conteúdo pelo texto:
/// o MVP não transcreve nem reconhece imagem, então a mídia segue "pendente" para a IA mesmo com o arquivo disponível ao atendente).
/// </summary>
public enum AttachmentStatus
{
    /// <summary>A mensagem foi gravada e o arquivo ainda está sendo buscado no provedor.</summary>
    Pending = 1,

    /// <summary>O arquivo foi baixado, validado e guardado em armazenamento privado: o atendente autorizado pode abri-lo.</summary>
    Available = 2,

    /// <summary>O provedor não tem mais o arquivo (expirou, foi apagado) ou ele não pôde ser baixado depois das tentativas. Nada a abrir.</summary>
    Unavailable = 3,

    /// <summary>O arquivo foi recusado pela política de segurança (tipo não permitido, grande demais, conteúdo que não é o que diz ser, integridade). Nunca é guardado nem aberto.</summary>
    Rejected = 4,
}

/// <summary>
/// O que o canal sabe da mídia no momento em que a mensagem chega: o identificador dela no provedor (para buscar o arquivo depois), o tipo e o nome que o cliente declarou e a legenda. Não há arquivo, URL
/// nem credencial aqui. Só vale para áudio, imagem e documento.
/// </summary>
/// <param name="ProviderMediaId">O identificador da mídia no provedor (no WhatsApp, o <c>id</c> da mídia da mensagem).</param>
/// <param name="DeclaredMimeType">O tipo que o provedor declarou na mensagem (ainda NÃO verificado contra o conteúdo).</param>
/// <param name="FileName">O nome do arquivo, quando o provedor informa (documentos). Texto do cliente: é saneado antes de ir a uma tela ou a um cabeçalho.</param>
/// <param name="Caption">A legenda que o cliente escreveu junto, se houver. Texto do cliente.</param>
public sealed record InboundAttachment(string ProviderMediaId, string? DeclaredMimeType, string? FileName, string? Caption);

/// <summary>
/// O arquivo de uma mensagem como o histórico o mostra. Nunca traz o caminho de armazenamento, o identificador no provedor nem uma URL: o conteúdo só sai por uma operação autorizada (ADR 0028).
/// </summary>
/// <param name="MimeType">O tipo VERIFICADO contra o conteúdo (só com o arquivo disponível).</param>
/// <param name="FileName">O nome saneado, seguro para exibir e para o cabeçalho de download.</param>
/// <param name="FailureCode">O motivo quando o arquivo não está disponível (<c>expired</c>, <c>download-failed</c>, <c>too-large</c>, <c>type-not-allowed</c>, <c>content-mismatch</c>, <c>hash-mismatch</c>, <c>empty</c>...).</param>
public sealed record AttachmentView(Guid Id, AttachmentStatus Status, string? MimeType, string? FileName, long? SizeBytes, string? FailureCode, string? Caption);

/// <summary>
/// O arquivo que o atendente envia ao cliente (ADR 0031): imagem, áudio ou documento, com legenda quando o tipo a aceita. O Conversations decide TUDO sobre ele: o tipo é o que o CONTEÚDO é (o tipo e a extensão
/// informados só desempatam e nunca prevalecem), o tipo de mídia (imagem, áudio ou documento) sai do tipo detectado, o tamanho respeita o máximo daquele tipo e o nome e a legenda são saneados. Os bytes só existem
/// em memória até o Conversations validá-los e guardá-los em armazenamento privado; nada disso é registrado em log.
/// </summary>
/// <param name="FileName">O nome que o atendente escolheu (o do arquivo no computador dele). Texto não confiável: é saneado.</param>
/// <param name="DeclaredMimeType">O tipo que o navegador informou. NUNCA é confiado: só serve para escolher entre tipos que o conteúdo também permite (por exemplo, Word, Excel e PowerPoint novos são o mesmo pacote) e, se contradiz o conteúdo, recusa o arquivo.</param>
/// <param name="Content">O conteúdo inteiro do arquivo.</param>
/// <param name="Caption">A legenda, se houver. Áudio não tem legenda (a recusa é <c>caption-not-supported</c>).</param>
public sealed record OutboundAttachment(string FileName, string? DeclaredMimeType, byte[] Content, string? Caption = null);

/// <summary>O que o Sender precisa saber do arquivo de uma mensagem de saída para enviá-lo (nunca a chave de armazenamento nem URL): o conteúdo vem à parte, por <c>IOutboundAttachmentReader</c>.</summary>
/// <param name="MimeType">O tipo VERIFICADO contra o conteúdo.</param>
/// <param name="FileName">O nome saneado (no WhatsApp, só o documento o mostra).</param>
public sealed record OutboundAttachmentInfo(MessageKind Kind, string MimeType, string FileName, long SizeBytes, string? Caption);
