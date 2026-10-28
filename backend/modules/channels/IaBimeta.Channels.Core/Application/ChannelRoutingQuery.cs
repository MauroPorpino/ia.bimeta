using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>
/// Implementação da consulta de roteamento entre módulos. Só enxerga o tenant do contexto (instância de outro tenant responde
/// como inexistente) e não aplica permissão de usuário, como o contrato documenta. Falha fechada: sem contexto lança, e uma
/// instância sem a unidade autorizada responde falso.
/// </summary>
public sealed class ChannelRoutingQuery : IChannelRouting
{
    private readonly IChannelRepository _repository;
    private readonly IRequestContextAccessor _context;

    public ChannelRoutingQuery(IChannelRepository repository, IRequestContextAccessor context)
    {
        _repository = repository;
        _context = context;
    }

    public async Task<ChannelRoutingView?> FindAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();
        if (channelInstanceId.IsEmpty)
        {
            return null;
        }

        var instancia = await _repository.FindInstanceReadOnlyAsync(channelInstanceId, cancellationToken);
        return instancia is null
            ? null
            : new ChannelRoutingView(
                instancia.Id,
                instancia.OrganizationId,
                instancia.Status,
                instancia.DefaultLocale,
                instancia.DefaultBusinessUnitId,
                instancia.DefaultDepartmentId,
                instancia.AuthorizedUnits.Select(u => u.BusinessUnitId).Order().ToList());
    }

    public async Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default)
    {
        var roteamento = await FindAsync(channelInstanceId, cancellationToken);
        return roteamento is not null && businessUnitId != Guid.Empty && roteamento.AuthorizedBusinessUnitIds.Contains(businessUnitId);
    }
}
