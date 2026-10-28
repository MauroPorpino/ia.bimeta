using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Conversations.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Channels;

/// <summary>
/// A integração mediada pelo host entre o Conversations e o Channels. O Conversations pergunta, por uma porta dele, se um número
/// pode atender uma unidade; quem responde é o Channels, a fonte de verdade das unidades autorizadas, por meio do seu
/// <c>Contracts</c>. A matriz de dependências não liga Conversations (nem ContactCenter) ao Channels, e só o host conhece os
/// dois, então a ligação mora aqui: nenhuma aresta nova entre módulos, nenhuma cópia da lista (cada pergunta consulta o
/// Channels na hora) e nenhum acesso às tabelas de outro módulo.
/// <para>
/// Só um "sim" inequívoco autoriza. Instância inexistente ou de outro tenant (o Channels responde como inexistente), unidade fora
/// da lista e lista vazia viram "não". Uma falha da consulta (por exemplo, banco indisponível) NÃO vira "sim" nem "não":
/// a exceção sobe, a operação não acontece e quem chamou pode tentar de novo.
/// </para>
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
