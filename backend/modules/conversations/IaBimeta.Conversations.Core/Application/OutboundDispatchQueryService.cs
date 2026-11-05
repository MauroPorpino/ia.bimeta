using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>O armazenamento da leitura de despacho (a Infrastructure o implementa, filtrando por tenant).</summary>
public interface IOutboundDispatchReader
{
    /// <summary>A mensagem de saída com o destinatário do contato da sessão, ou nulo se não existe no tenant do contexto ou não é de saída.</summary>
    Task<OutboundDispatchView?> FindAsync(Guid messageId, CancellationToken cancellationToken);
}

/// <summary>
/// A leitura que o Sender do Channels faz para despachar uma mensagem de saída (ver <see cref="IOutboundDispatchQueries"/>). Só o contexto de SISTEMA a usa: a mensagem é buscada por <c>(tenant, id)</c>
/// e nunca há permissão de usuário aqui. Mensagem do cliente (entrada) responde como inexistente.
/// </summary>
public sealed class OutboundDispatchQueryService : IOutboundDispatchQueries
{
    private readonly IOutboundDispatchReader _reader;
    private readonly IRequestContextAccessor _context;

    public OutboundDispatchQueryService(IOutboundDispatchReader reader, IRequestContextAccessor context)
    {
        _reader = reader;
        _context = context;
    }

    public async Task<OutboundDispatchView?> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.SystemOperation);
        return await _reader.FindAsync(messageId, cancellationToken);
    }
}
