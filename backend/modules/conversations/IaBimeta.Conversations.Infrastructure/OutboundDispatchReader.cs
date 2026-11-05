using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Queries;
using Microsoft.EntityFrameworkCore;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>
/// A leitura de despacho: a mensagem de saída, o contato dela (o destinatário no provedor) e a instância de canal da conversa. Toda consulta passa pelo filtro global por tenant do
/// <see cref="ConversationsDbContext"/>, e o contato é buscado pelo mesmo tenant e pela mesma instância de canal da mensagem. Sem rastreamento.
/// </summary>
internal sealed class OutboundDispatchReader : IOutboundDispatchReader
{
    private readonly ConversationsDbContext _db;

    public OutboundDispatchReader(ConversationsDbContext db)
    {
        _db = db;
    }

    public async Task<OutboundDispatchView?> FindAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var linha = await (
            from mensagem in _db.Messages.AsNoTracking()
            where mensagem.Id == messageId && mensagem.Direction == MessageDirection.Outbound
            join sessao in _db.Sessions.AsNoTracking() on mensagem.SessionId equals sessao.Id
            join contato in _db.Contacts.AsNoTracking() on sessao.ContactId equals contato.Id
            select new { mensagem, contato.ExternalRef })
            .FirstOrDefaultAsync(cancellationToken);

        return linha is null
            ? null
            : new OutboundDispatchView(
                linha.mensagem.Id,
                linha.mensagem.SessionId,
                linha.mensagem.ChannelInstanceId,
                linha.ExternalRef,
                linha.mensagem.Author,
                linha.mensagem.Text,
                linha.mensagem.DeliveryState);
    }
}
