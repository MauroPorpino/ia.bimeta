using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Tests;

public sealed class ProviderIdentifierTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("123456789012345")]
    [InlineData("abc_DEF.1:2-3")]
    public void Aceita_identificadores_curtos_com_o_conjunto_de_caracteres_seguro(string valor)
    {
        Assert.True(ProviderIdentifier.IsValid(valor));
        Assert.Equal(valor, ProviderIdentifier.Normalize(valor, "do número"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a;b")]
    [InlineData("a'b")]
    [InlineData("a\nb")]
    [InlineData("ação")]
    [InlineData("<script>")]
    public void Recusa_vazio_espaco_no_meio_e_caracteres_fora_do_conjunto(string? valor)
    {
        Assert.False(ProviderIdentifier.IsValid(valor));
        Assert.Throws<ChannelsValidationException>(() => ProviderIdentifier.Normalize(valor, "do número"));
    }

    [Fact]
    public void Aceita_ate_64_caracteres_e_recusa_65()
    {
        Assert.True(ProviderIdentifier.IsValid(new string('1', ProviderIdentifier.MaxLength)));
        Assert.False(ProviderIdentifier.IsValid(new string('1', ProviderIdentifier.MaxLength + 1)));
    }

    [Fact]
    public void Normalize_tira_os_espacos_das_pontas_mas_IsValid_nao_normaliza()
    {
        Assert.Equal("123", ProviderIdentifier.Normalize("  123 ", "do número"));
        Assert.False(ProviderIdentifier.IsValid(" 123"));
    }

    [Fact]
    public void A_mensagem_de_erro_nao_reproduz_o_valor_recebido()
    {
        var erro = Assert.Throws<ChannelsValidationException>(() => ProviderIdentifier.Normalize("segredo com espaco", "do número"));

        Assert.DoesNotContain("segredo", erro.Message, StringComparison.Ordinal);
    }
}
