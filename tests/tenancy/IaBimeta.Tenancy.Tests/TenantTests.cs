using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

public class TenantTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registro_valido_cria_tenant_ativo()
    {
        var id = new TenantId(Guid.NewGuid());

        var tenant = Tenant.Register(id, "lab-central", "  Laboratório Central  ", Agora);

        Assert.Equal(id, tenant.Id);
        Assert.Equal("lab-central", tenant.Slug);
        Assert.Equal("Laboratório Central", tenant.Name);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(Agora, tenant.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Lab-Central")]
    [InlineData("lab central")]
    [InlineData("-lab")]
    [InlineData("lab-")]
    [InlineData("lab--central")]
    [InlineData("lab_central")]
    [InlineData("laboratório")]
    public void Slug_invalido_e_rejeitado(string slug)
    {
        Assert.Throws<TenancyValidationException>(() => Tenant.Register(new TenantId(Guid.NewGuid()), slug, "Nome", Agora));
    }

    [Fact]
    public void Slug_acima_do_limite_e_rejeitado()
    {
        var slug = new string('a', Tenant.SlugMaxLength + 1);

        Assert.Throws<TenancyValidationException>(() => Tenant.Register(new TenantId(Guid.NewGuid()), slug, "Nome", Agora));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nome_vazio_e_rejeitado(string nome)
    {
        Assert.Throws<TenancyValidationException>(() => Tenant.Register(new TenantId(Guid.NewGuid()), "lab-central", nome, Agora));
    }

    [Fact]
    public void Nome_acima_do_limite_e_rejeitado()
    {
        var nome = new string('n', Tenant.NameMaxLength + 1);

        Assert.Throws<TenancyValidationException>(() => Tenant.Register(new TenantId(Guid.NewGuid()), "lab-central", nome, Agora));
    }

    [Fact]
    public void Suspender_e_reativar_alteram_a_situacao()
    {
        var tenant = Tenant.Register(new TenantId(Guid.NewGuid()), "lab-central", "Lab", Agora);

        tenant.Suspend();
        Assert.Equal(TenantStatus.Suspended, tenant.Status);

        tenant.Reactivate();
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }
}
