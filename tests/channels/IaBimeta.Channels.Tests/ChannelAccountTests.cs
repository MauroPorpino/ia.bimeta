using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Tests;

public sealed class ChannelAccountTests
{
    [Fact]
    public void Cria_a_conta_na_organizacao_com_o_identificador_normalizado()
    {
        var tenant = Fabrica.NovoTenant();
        var organizacao = Guid.NewGuid();

        var conta = ChannelAccount.Create(tenant, organizacao, ChannelProvider.WhatsAppCloud, " 100200 ", "  Conta Matriz  ", Fabrica.Agora);

        Assert.NotEqual(Guid.Empty, conta.Id);
        Assert.Equal(tenant, conta.TenantId);
        Assert.Equal(organizacao, conta.OrganizationId);
        Assert.Equal("100200", conta.ProviderAccountId);
        Assert.Equal("Conta Matriz", conta.Name);
    }

    [Fact]
    public void Recusa_organizacao_vazia()
    {
        Assert.Throws<ChannelsValidationException>(() =>
            ChannelAccount.Create(Fabrica.NovoTenant(), Guid.Empty, ChannelProvider.WhatsAppCloud, "1", "Conta", Fabrica.Agora));
    }

    [Fact]
    public void Recusa_provedor_inexistente()
    {
        Assert.Throws<ChannelsValidationException>(() =>
            ChannelAccount.Create(Fabrica.NovoTenant(), Guid.NewGuid(), (ChannelProvider)99, "1", "Conta", Fabrica.Agora));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Recusa_nome_vazio(string nome)
    {
        Assert.Throws<ChannelsValidationException>(() =>
            ChannelAccount.Create(Fabrica.NovoTenant(), Guid.NewGuid(), ChannelProvider.WhatsAppCloud, "1", nome, Fabrica.Agora));
    }

    [Fact]
    public void Recusa_nome_acima_do_limite_e_identificador_invalido()
    {
        Assert.Throws<ChannelsValidationException>(() =>
            ChannelAccount.Create(Fabrica.NovoTenant(), Guid.NewGuid(), ChannelProvider.WhatsAppCloud, "1", new string('x', ChannelAccount.NameMaxLength + 1), Fabrica.Agora));
        Assert.Throws<ChannelsValidationException>(() =>
            ChannelAccount.Create(Fabrica.NovoTenant(), Guid.NewGuid(), ChannelProvider.WhatsAppCloud, "id com espaco", "Conta", Fabrica.Agora));
    }
}
