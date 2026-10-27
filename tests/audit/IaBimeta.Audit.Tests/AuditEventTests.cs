using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Audit.Tests;

public class AuditEventTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Agora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static AuditEvent Criar(AuditEntry entrada, Guid? ator = null, string origem = "session") =>
        AuditEvent.Create(Tenant, ator, origem, entrada, Agora);

    [Fact]
    public void Evento_guarda_tenant_ator_operacao_recurso_resultado_e_data()
    {
        var ator = Guid.NewGuid();

        var evento = Criar(new AuditEntry("tenancy.organization.create", "organization", "0192f3a1-aaaa", AuditOutcome.Success), ator);

        Assert.Equal(Tenant, evento.TenantId);
        Assert.Equal(ator, evento.ActorUserId);
        Assert.Equal("session", evento.ActorOrigin);
        Assert.Equal("tenancy.organization.create", evento.Operation);
        Assert.Equal("organization", evento.ResourceType);
        Assert.Equal("0192f3a1-aaaa", evento.ResourceId);
        Assert.Equal(AuditOutcome.Success, evento.Outcome);
        Assert.Equal(Agora, evento.OccurredAt);
        Assert.NotEqual(Guid.Empty, evento.Id);
    }

    [Fact]
    public void Recurso_e_ator_sao_opcionais()
    {
        var evento = Criar(new AuditEntry("identity.login", "user", null, AuditOutcome.Denied));

        Assert.Null(evento.ResourceId);
        Assert.Null(evento.ActorUserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Login")]
    [InlineData("identity login")]
    [InlineData("identity.login: senha=abc")]
    [InlineData("1identity")]
    public void Operacao_fora_do_formato_e_recusada(string operacao)
    {
        Assert.Throws<AuditValidationException>(() => Criar(new AuditEntry(operacao, "user", null, AuditOutcome.Success)));
    }

    [Fact]
    public void Operacao_e_tipo_acima_do_limite_sao_recusados()
    {
        Assert.Throws<AuditValidationException>(() =>
            Criar(new AuditEntry("a" + new string('b', AuditEvent.OperationMaxLength), "user", null, AuditOutcome.Success)));
        Assert.Throws<AuditValidationException>(() =>
            Criar(new AuditEntry("identity.login", "a" + new string('b', AuditEvent.ResourceTypeMaxLength), null, AuditOutcome.Success)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("senha com espaco")]
    [InlineData("ana@exemplo.com")]
    [InlineData("ibs_token/com+simbolos=")]
    [InlineData("a;b")]
    public void Identificador_de_recurso_fora_do_formato_e_recusado(string recurso)
    {
        Assert.Throws<AuditValidationException>(() => Criar(new AuditEntry("identity.login", "user", recurso, AuditOutcome.Success)));
    }

    [Fact]
    public void Identificador_de_recurso_acima_do_limite_e_recusado()
    {
        Assert.Throws<AuditValidationException>(() =>
            Criar(new AuditEntry("identity.login", "user", new string('a', AuditEvent.ResourceIdMaxLength + 1), AuditOutcome.Success)));
    }

    [Fact]
    public void Resultado_inexistente_e_recusado()
    {
        Assert.Throws<AuditValidationException>(() => Criar(new AuditEntry("identity.login", "user", null, (AuditOutcome)99)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sessao")]
    [InlineData("com espaco")]
    public void Origem_do_ator_fora_do_formato_e_recusada(string origem)
    {
        Assert.Throws<AuditValidationException>(() => Criar(new AuditEntry("identity.login", "user", null, AuditOutcome.Success), origem: origem));
    }

    [Fact]
    public void Eventos_distintos_tem_identificadores_distintos()
    {
        var entrada = new AuditEntry("identity.login", "user", null, AuditOutcome.Success);

        Assert.NotEqual(Criar(entrada).Id, Criar(entrada).Id);
    }
}
