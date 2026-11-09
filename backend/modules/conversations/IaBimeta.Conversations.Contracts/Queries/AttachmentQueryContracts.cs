namespace IaBimeta.Conversations.Contracts.Queries;

/// <summary>
/// A abertura do arquivo de uma mensagem (ADR 0028). Respeita as permissões do contexto sobre a organização da sessão (<c>ConversationRead</c>); a mensagem precisa ser DA sessão informada (o identificador de uma
/// mensagem de outra sessão ou de outro tenant é inexistente) e o arquivo precisa estar <see cref="AttachmentStatus.Available"/>. Quem chama (o ContactCenter) já decidiu se o atendente enxerga a mensagem.
/// </summary>
public interface IAttachmentQueries
{
    /// <summary>O arquivo aberto para leitura, ou nulo se a sessão, a mensagem ou o arquivo disponível não existem para o contexto. Quem recebe DEVE descartar o resultado.</summary>
    Task<AttachmentContent?> OpenAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default);
}

/// <summary>O arquivo aberto: os metadados e o fluxo de leitura (a posse do fluxo é de quem recebe).</summary>
public sealed class AttachmentContent : IAsyncDisposable
{
    public AttachmentContent(AttachmentView attachment, Stream content, string mimeType, string fileName)
    {
        Attachment = attachment;
        Content = content;
        MimeType = mimeType;
        FileName = fileName;
    }

    public AttachmentView Attachment { get; }

    public Stream Content { get; }

    /// <summary>O tipo verificado contra o conteúdo.</summary>
    public string MimeType { get; }

    /// <summary>O nome saneado.</summary>
    public string FileName { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
