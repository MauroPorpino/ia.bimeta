using IaBimeta.Conversations.Application;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

public sealed class ChannelUnitAuthorizationTests
{
    [Fact]
    public async Task A_implementacao_padrao_nega_toda_unidade_em_todo_canal_falha_fechada()
    {
        var padrao = new DenyAllChannelUnitAuthorization();

        Assert.False(await padrao.IsBusinessUnitAuthorizedAsync(ChannelInstanceId.New(), Guid.NewGuid()));
        Assert.False(await padrao.IsBusinessUnitAuthorizedAsync(default, Guid.Empty));
    }
}
