using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.Conversations.Infrastructure;

/// <summary>Guarda a instância de canal como <c>uuid</c>. Sem chave estrangeira: o cadastro é do módulo Channels (DAD-2).</summary>
internal sealed class ChannelInstanceIdConverter : ValueConverter<ChannelInstanceId, Guid>
{
    public ChannelInstanceIdConverter()
        : base(canal => canal.Value, valor => new ChannelInstanceId(valor))
    {
    }
}
