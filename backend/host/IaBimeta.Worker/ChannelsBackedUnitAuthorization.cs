using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Conversations.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Worker;

/// <summary>
/// A integração mediada pelo host entre o Conversations e o Channels (ADR 0008), a mesma do Api: o Conversations pergunta, por uma
/// porta dele, se um número pode atender uma unidade, e o Channels (a fonte de verdade) responde pelo <c>Contracts</c> dele, a cada
/// pergunta, sem cópia local. Só um "sim" inequívoco autoriza; uma falha da consulta sobe, a operação não acontece e pode ser
/// repetida. Vive em cada host porque a matriz de dependências não liga os módulos e só o host conhece os dois.
/// </summary>
internal sealed class ChannelsBackedUnitAuthorization : IChannelUnitAuthorization
{
    private readonly IChannelRouting _routing;

    public ChannelsBackedUnitAuthorization(IChannelRouting routing)
    {
        _routing = routing;
    }

    public Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default) =>
        _routing.IsBusinessUnitAuthorizedAsync(channelInstanceId, businessUnitId, cancellationToken);
}
