using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Tests;

public sealed class ChannelSecretTests
{
    [Theory]
    [InlineData("x")]
    [InlineData("EAAG-token.sintetico_0123456789")]
    [InlineData("com espaco no meio")]
    public void Aceita_valor_sem_espaco_nas_pontas_e_sem_caractere_de_controle(string valor)
    {
        Assert.Equal(valor, ChannelSecret.ValidateValue(valor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" token")]
    [InlineData("token ")]
    [InlineData("to\nken")]
    [InlineData("to\u0000ken")]
    public void Recusa_vazio_espaco_nas_pontas_e_caractere_de_controle(string? valor)
    {
        Assert.Throws<ChannelsValidationException>(() => ChannelSecret.ValidateValue(valor));
    }

    [Fact]
    public void Aceita_o_limite_e_recusa_acima_dele()
    {
        Assert.NotNull(ChannelSecret.ValidateValue(new string('a', ChannelSecret.MaxValueLength)));
        Assert.Throws<ChannelsValidationException>(() => ChannelSecret.ValidateValue(new string('a', ChannelSecret.MaxValueLength + 1)));
    }

    [Fact]
    public void A_mensagem_de_erro_nunca_reproduz_o_valor()
    {
        const string valor = "SEGREDO-SINTETICO-COM-ESPACO ";

        var erro = Assert.Throws<ChannelsValidationException>(() => ChannelSecret.ValidateValue(valor));

        Assert.DoesNotContain("SEGREDO", erro.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Girar_a_credencial_mantem_a_referencia_e_sobe_a_versao()
    {
        var tenant = Fabrica.NovoTenant();
        var id = Guid.NewGuid();
        var segredo = ChannelSecret.Create(id, tenant, Guid.NewGuid(), ChannelSecretKind.AccessToken, "k1", "v1:AAAA", null, Fabrica.Agora);
        Assert.Equal(1, segredo.Version);

        var autor = Guid.NewGuid();
        segredo.Replace("k2", "v1:BBBB", autor, Fabrica.Agora.AddHours(1));

        Assert.Equal(id, segredo.Id);
        Assert.Equal(2, segredo.Version);
        Assert.Equal("k2", segredo.KeyId);
        Assert.Equal("v1:BBBB", segredo.Ciphertext);
        Assert.Equal(autor, segredo.UpdatedBy);
        Assert.Equal(Fabrica.Agora.AddHours(1), segredo.UpdatedAt);
    }

    [Fact]
    public void Recusa_referencia_conta_ou_papel_invalidos()
    {
        var tenant = Fabrica.NovoTenant();

        Assert.Throws<ChannelsValidationException>(() => ChannelSecret.Create(Guid.Empty, tenant, Guid.NewGuid(), ChannelSecretKind.AppSecret, "k", "c", null, Fabrica.Agora));
        Assert.Throws<ChannelsValidationException>(() => ChannelSecret.Create(Guid.NewGuid(), tenant, Guid.Empty, ChannelSecretKind.AppSecret, "k", "c", null, Fabrica.Agora));
        Assert.Throws<ChannelsValidationException>(() => ChannelSecret.Create(Guid.NewGuid(), tenant, Guid.NewGuid(), (ChannelSecretKind)99, "k", "c", null, Fabrica.Agora));
    }
}
