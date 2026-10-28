using System.Text.Json;
using IaBimeta.SharedKernel;

namespace IaBimeta.SharedKernel.Tests;

public class ChannelInstanceIdTests
{
    [Fact]
    public void O_identificador_guarda_o_guid_e_o_valor_padrao_e_vazio()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(guid, new ChannelInstanceId(guid).Value);
        Assert.False(new ChannelInstanceId(guid).IsEmpty);
        Assert.True(default(ChannelInstanceId).IsEmpty);
        Assert.Equal(guid.ToString(), new ChannelInstanceId(guid).ToString());
    }

    [Fact]
    public void Guid_vazio_e_recusado_na_construcao()
    {
        Assert.Throws<ArgumentException>(() => new ChannelInstanceId(Guid.Empty));
    }

    [Fact]
    public void A_igualdade_e_pelo_valor_e_dois_novos_sao_diferentes()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(new ChannelInstanceId(guid), new ChannelInstanceId(guid));
        Assert.True(new ChannelInstanceId(guid) == new ChannelInstanceId(guid));
        Assert.NotEqual(ChannelInstanceId.New(), ChannelInstanceId.New());
        Assert.Equal(new ChannelInstanceId(guid).GetHashCode(), new ChannelInstanceId(guid).GetHashCode());
    }

    private sealed record Exemplo(ChannelInstanceId Canal);

    [Fact]
    public void O_JSON_usa_texto_e_recusa_vazio_mal_formado_e_numero()
    {
        var guid = Guid.NewGuid();

        Assert.Equal($$"""{"Canal":"{{guid}}"}""", JsonSerializer.Serialize(new Exemplo(new ChannelInstanceId(guid))));
        Assert.Equal(new ChannelInstanceId(guid), JsonSerializer.Deserialize<Exemplo>($$"""{"Canal":"{{guid}}"}""")!.Canal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Exemplo>($$"""{"Canal":"{{Guid.Empty}}"}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Exemplo>("""{"Canal":"nao-e-guid"}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Exemplo>("""{"Canal":123}"""));
    }
}
