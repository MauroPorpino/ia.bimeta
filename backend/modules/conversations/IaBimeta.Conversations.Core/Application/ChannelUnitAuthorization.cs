using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// Porta do Conversations para a pergunta "este número (instância de canal) pode atender esta unidade?". A fonte de verdade é o
/// Channels, mas a matriz de dependências não deixa o Conversations referenciar o Channels (nem o ContactCenter), então a
/// porta é DESTE módulo e o host a implementa chamando o <c>Channels.Contracts</c> (BOUNDARIES.md: só o host conhece todos os
/// módulos). Cada chamada consulta o Channels no momento, sem cópia que fique velha.
/// <para>
/// Falha fechada: sem implementação registrada vale <see cref="DenyAllChannelUnitAuthorization"/> (nenhuma unidade é aceita), e
/// qualquer resposta que não seja um "sim" inequívoco é tratada como não autorizada. O tenant da pergunta é o do
/// <see cref="RequestContext"/> da operação.
/// </para>
/// </summary>
public interface IChannelUnitAuthorization
{
    /// <summary>Verdadeiro só se a instância existe neste tenant e a unidade está entre as autorizadas dela.</summary>
    Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default);
}

/// <summary>A implementação padrão: nega tudo. Existe para que um host sem o Channels ligado nunca aceite uma unidade por omissão.</summary>
public sealed class DenyAllChannelUnitAuthorization : IChannelUnitAuthorization
{
    public Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
