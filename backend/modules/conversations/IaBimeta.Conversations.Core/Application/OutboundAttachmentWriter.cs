using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// O preparo do arquivo que o atendente envia (ADR 0031), usado pela resposta humana: avalia o arquivo pela <see cref="OutboundAttachmentPolicy"/> e pela varredura (<see cref="IAttachmentScanner"/>) ANTES de qualquer
/// gravação, guarda o conteúdo no armazenamento PRIVADO (<see cref="IAttachmentContentStore"/>, o mesmo do arquivo do cliente) e o descarta se a mensagem não chegou a ser gravada. A gravação do objeto só acontece
/// depois de todas as condições da resposta valerem, com a linha da sessão travada (então uma repetição do mesmo pedido já foi respondida antes de qualquer escrita), e o objeto tem chave por arquivo.
/// </summary>
public sealed class OutboundAttachmentWriter
{
    private readonly IAttachmentContentStore _content;
    private readonly IAttachmentScanner _scanner;
    private readonly AttachmentOptions _options;

    public OutboundAttachmentWriter(IAttachmentContentStore content, IAttachmentScanner scanner, AttachmentOptions options)
    {
        _content = content;
        _scanner = scanner;
        _options = options;
    }

    /// <summary>A política (tipo real, tamanho, nome e legenda) e a varredura. Nada é guardado aqui.</summary>
    public async Task<OutboundAttachmentVerdict> ScreenAsync(OutboundAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var veredito = OutboundAttachmentPolicy.Evaluate(_options, attachment.Content, attachment.DeclaredMimeType, attachment.FileName, attachment.Caption);
        if (!veredito.Accepted)
        {
            return veredito;
        }

        return await _scanner.ScanAsync(veredito.Kind, veredito.MimeType!, attachment.Content, cancellationToken) == ScanVerdict.Infected
            ? OutboundAttachmentVerdict.Reject("malware")
            : veredito;
    }

    /// <summary>Guarda o conteúdo e devolve a chave opaca.</summary>
    public Task<string> StoreAsync(TenantId tenant, Guid attachmentId, byte[] content, CancellationToken cancellationToken) =>
        _content.WriteAsync(tenant, attachmentId, content, cancellationToken);

    /// <summary>Apaga o objeto de uma gravação que não se completou. Melhor esforço: se falhar, a limpeza de objetos órfãos o recolhe depois (nunca esconde o erro que levou até aqui).</summary>
    public async Task DiscardAsync(TenantId tenant, string key)
    {
        try
        {
            await _content.DeleteAsync(tenant, key, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O objeto sem linha é recolhido pela limpeza de órfãos.
        }
    }
}
