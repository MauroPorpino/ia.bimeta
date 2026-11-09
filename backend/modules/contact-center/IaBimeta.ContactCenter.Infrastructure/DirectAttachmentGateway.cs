using IaBimeta.ContactCenter.Application;
using IaBimeta.Conversations.Contracts.Queries;

namespace IaBimeta.ContactCenter.Infrastructure;

/// <summary>A porta padrão para abrir arquivos: chama o Conversations com o contexto de QUEM CHAMOU (ver <see cref="IAttachmentGateway"/>).</summary>
internal sealed class DirectAttachmentGateway(IAttachmentQueries queries) : IAttachmentGateway
{
    public Task<AttachmentContent?> OpenAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken) =>
        queries.OpenAsync(sessionId, messageId, cancellationToken);
}
