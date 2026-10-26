using IaBimeta.Domain.Tenancy;

namespace IaBimeta.Domain.Tests.Tenancy;

public class TenantIdTests
{
    [Fact]
    public void Rejeita_guid_vazio()
    {
        Assert.Throws<ArgumentException>(() => new TenantId(Guid.Empty));
    }

    [Fact]
    public void Ids_com_o_mesmo_valor_sao_iguais()
    {
        var valor = Guid.NewGuid();

        Assert.Equal(new TenantId(valor), new TenantId(valor));
    }

    [Fact]
    public void Ids_com_valores_diferentes_nao_sao_iguais()
    {
        Assert.NotEqual(new TenantId(Guid.NewGuid()), new TenantId(Guid.NewGuid()));
    }
}
