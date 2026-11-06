using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>O armazenamento da leitura de despacho (a Infrastructure o implementa, filtrando por tenant).</summary>
public interface IOutboundDispatchReader
{
    /// <summary>A mensagem de saída com o destinatário do contato da sessão, ou nulo se não existe no tenant do contexto ou não é de saída.</summary>
    Task<OutboundDispatchView?> FindAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>As mensagens de saída ainda sem desfecho (pedidas, enfileiradas ou em envio) criadas antes do instante, as mais antigas primeiro, só das organizações dadas (nulo = todas do tenant).</summary>
    Task<IReadOnlyList<UndeliveredOutboundView>> ListUndeliveredAsync(IReadOnlyCollection<Guid>? organizationIds, DateTimeOffset createdBefore, int take, CancellationToken cancellationToken);
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

/// <summary>A leitura de diagnóstico das mensagens de saída sem desfecho (ver <see cref="IOutboundDeliveryQueries"/>), com as permissões do usuário sobre as organizações das sessões.</summary>
public sealed class OutboundDeliveryQueryService : IOutboundDeliveryQueries
{
    private const int MaxTake = 200;

    private readonly IOutboundDispatchReader _reader;
    private readonly IRequestContextAccessor _context;

    public OutboundDeliveryQueryService(IOutboundDispatchReader reader, IRequestContextAccessor context)
    {
        _reader = reader;
        _context = context;
    }

    public async Task<IReadOnlyList<UndeliveredOutboundView>> ListUndeliveredAsync(DateTimeOffset createdBefore, int take, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var visiveis = contexto.OrganizationsWithPermission(Permission.ConversationRead);
        if (visiveis is { Count: 0 })
        {
            throw new AccessDeniedException(Permission.ConversationRead);
        }

        return await _reader.ListUndeliveredAsync(visiveis, createdBefore, Math.Clamp(take, 1, MaxTake), cancellationToken);
    }
}
