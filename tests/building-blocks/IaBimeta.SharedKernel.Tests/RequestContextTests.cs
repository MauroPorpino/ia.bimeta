namespace IaBimeta.SharedKernel.Tests;

public class RequestContextTests
{
    [Fact]
    public void Contexto_guarda_o_tenant_e_a_origem()
    {
        var tenant = new TenantId(Guid.NewGuid());

        var contexto = new RequestContext(tenant, "teste");

        Assert.Equal(tenant, contexto.TenantId);
        Assert.Equal("teste", contexto.Origin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Origem_vazia_e_rejeitada(string origem)
    {
        Assert.Throws<ArgumentException>(() => new RequestContext(new TenantId(Guid.NewGuid()), origem));
    }

    [Fact]
    public void Tenant_nulo_e_rejeitado()
    {
        Assert.Throws<ArgumentNullException>(() => new RequestContext(null!, "teste"));
    }

    [Fact]
    public void Sem_contexto_a_leitura_obrigatoria_falha_fechada()
    {
        var holder = new RequestContextHolder();

        Assert.Null(holder.Current);
        Assert.Throws<TenantContextRequiredException>(() => holder.GetRequired());
    }

    [Fact]
    public void Com_contexto_a_leitura_obrigatoria_devolve_o_contexto()
    {
        var holder = new RequestContextHolder();
        var contexto = new RequestContext(new TenantId(Guid.NewGuid()), "teste");

        holder.Set(contexto);

        Assert.Same(contexto, holder.GetRequired());
    }

    [Fact]
    public void Contexto_pode_ser_removido_e_a_leitura_volta_a_falhar()
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(new TenantId(Guid.NewGuid()), "teste"));

        holder.Set(null);

        Assert.Throws<TenantContextRequiredException>(() => holder.GetRequired());
    }
}
