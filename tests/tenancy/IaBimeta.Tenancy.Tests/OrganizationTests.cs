using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

public class OrganizationTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Criacao_valida_guarda_tenant_nome_e_gera_id()
    {
        var tenant = new TenantId(Guid.NewGuid());

        var organizacao = Organization.Create(tenant, "  Matriz Ltda  ", Agora);

        Assert.NotEqual(Guid.Empty, organizacao.Id);
        Assert.Equal(tenant, organizacao.TenantId);
        Assert.Equal("Matriz Ltda", organizacao.Name);
        Assert.Equal(Agora, organizacao.CreatedAt);
    }

    [Fact]
    public void Organizacao_e_propriedade_de_tenant()
    {
        var organizacao = Organization.Create(new TenantId(Guid.NewGuid()), "Matriz", Agora);

        Assert.IsAssignableFrom<ITenantOwned>(organizacao);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nome_vazio_e_rejeitado(string nome)
    {
        Assert.Throws<TenancyValidationException>(() => Organization.Create(new TenantId(Guid.NewGuid()), nome, Agora));
    }

    [Fact]
    public void Nome_acima_do_limite_e_rejeitado()
    {
        var nome = new string('n', Organization.NameMaxLength + 1);

        Assert.Throws<TenancyValidationException>(() => Organization.Create(new TenantId(Guid.NewGuid()), nome, Agora));
    }

    [Fact]
    public void Tenant_nulo_e_rejeitado()
    {
        Assert.Throws<ArgumentNullException>(() => Organization.Create(null!, "Matriz", Agora));
    }
}
