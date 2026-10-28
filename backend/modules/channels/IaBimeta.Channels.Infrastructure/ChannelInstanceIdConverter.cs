using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.Channels.Infrastructure;

/// <summary>Guarda a instância de canal como <c>uuid</c>. Sem chave estrangeira: o cadastro é deste módulo, mas a coluna é só uma chave.</summary>
internal sealed class ChannelInstanceIdConverter : ValueConverter<ChannelInstanceId, Guid>
{
    public ChannelInstanceIdConverter()
        : base(canal => canal.Value, valor => new ChannelInstanceId(valor))
    {
    }
}
