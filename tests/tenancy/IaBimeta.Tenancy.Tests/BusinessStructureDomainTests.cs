using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Tests;

public class BusinessStructureDomainTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unidade_guarda_tenant_organizacao_nome_aparado_e_tipo()
    {
        var organizacao = Guid.NewGuid();

        var unidade = BusinessUnit.Create(Tenant, organizacao, "  Filial Centro  ", BusinessUnitKind.Branch, Agora);

        Assert.Equal(Tenant, unidade.TenantId);
        Assert.Equal(organizacao, unidade.OrganizationId);
        Assert.Equal("Filial Centro", unidade.Name);
        Assert.Equal(BusinessUnitKind.Branch, unidade.Kind);
        Assert.NotEqual(Guid.Empty, unidade.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Unidade_recusa_nome_vazio(string nome)
    {
        Assert.Throws<TenancyValidationException>(() => BusinessUnit.Create(Tenant, Guid.NewGuid(), nome, BusinessUnitKind.Branch, Agora));
    }

    [Fact]
    public void Unidade_recusa_nome_acima_do_limite_e_aceita_o_limite()
    {
        Assert.Throws<TenancyValidationException>(() =>
            BusinessUnit.Create(Tenant, Guid.NewGuid(), new string('x', BusinessUnit.NameMaxLength + 1), BusinessUnitKind.Branch, Agora));

        var noLimite = BusinessUnit.Create(Tenant, Guid.NewGuid(), new string('x', BusinessUnit.NameMaxLength), BusinessUnitKind.Branch, Agora);
        Assert.Equal(BusinessUnit.NameMaxLength, noLimite.Name.Length);
    }

    [Fact]
    public void Unidade_recusa_organizacao_vazia_e_tipo_inexistente()
    {
        Assert.Throws<TenancyValidationException>(() => BusinessUnit.Create(Tenant, Guid.Empty, "Matriz", BusinessUnitKind.Headquarters, Agora));
        Assert.Throws<TenancyValidationException>(() => BusinessUnit.Create(Tenant, Guid.NewGuid(), "Matriz", (BusinessUnitKind)99, Agora));
    }

    [Fact]
    public void Setor_guarda_tenant_unidade_e_nome_aparado()
    {
        var unidade = Guid.NewGuid();

        var setor = Department.Create(Tenant, unidade, "  Coleta  ", Agora);

        Assert.Equal(Tenant, setor.TenantId);
        Assert.Equal(unidade, setor.BusinessUnitId);
        Assert.Equal("Coleta", setor.Name);
    }

    [Fact]
    public void Setor_recusa_nome_invalido_e_unidade_vazia()
    {
        Assert.Throws<TenancyValidationException>(() => Department.Create(Tenant, Guid.NewGuid(), " ", Agora));
        Assert.Throws<TenancyValidationException>(() => Department.Create(Tenant, Guid.NewGuid(), new string('x', Department.NameMaxLength + 1), Agora));
        Assert.Throws<TenancyValidationException>(() => Department.Create(Tenant, Guid.Empty, "Coleta", Agora));
    }
}
