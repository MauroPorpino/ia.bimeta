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
