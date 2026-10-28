using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Tests;

public sealed class ChannelInstanceTests
{
    private static readonly DateTimeOffset Depois = Fabrica.Agora.AddMinutes(5);

    [Fact]
    public void Nasce_inativa_sem_unidades_na_organizacao_da_conta_e_na_versao_1()
    {
        var conta = Fabrica.Conta();

        var instancia = ChannelInstance.Register(conta.TenantId, conta, " 555000111 ", "  +55 11 99999-0000 ", "  Atendimento  ", null, Fabrica.Agora);

        Assert.False(instancia.Id.IsEmpty);
        Assert.Equal(conta.TenantId, instancia.TenantId);
        Assert.Equal(conta.Id, instancia.AccountId);
        Assert.Equal(conta.OrganizationId, instancia.OrganizationId);
        Assert.Equal(ChannelProvider.WhatsAppCloud, instancia.Provider);
        Assert.Equal("555000111", instancia.PhoneNumberId);
        Assert.Equal("+55 11 99999-0000", instancia.DisplayPhoneNumber);
        Assert.Equal("Atendimento", instancia.Name);
        Assert.Equal(ChannelStatus.Inactive, instancia.Status);
        Assert.Empty(instancia.AuthorizedUnits);
        Assert.Null(instancia.DefaultBusinessUnitId);
        Assert.Null(instancia.DefaultDepartmentId);
        Assert.Null(instancia.DefaultLocale);
        Assert.Equal(1, instancia.Version);
    }

    [Fact]
    public void Guarda_o_idioma_padrao_do_numero()
    {
        var instancia = Fabrica.Instancia(idioma: Locale.Parse("es-ES"));

        Assert.Equal(Locale.Parse("es-ES"), instancia.DefaultLocale);
    }

    [Fact]
    public void Recusa_conta_de_outro_tenant()
    {
        var conta = Fabrica.Conta();

        Assert.Throws<ChannelsValidationException>(() =>
            ChannelInstance.Register(Fabrica.NovoTenant(), conta, "1", null, "Canal", null, Fabrica.Agora));
    }

    [Theory]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("a/b")]
    public void Recusa_identificador_de_numero_invalido(string numero)
    {
        var conta = Fabrica.Conta();

        Assert.Throws<ChannelsValidationException>(() => ChannelInstance.Register(conta.TenantId, conta, numero, null, "Canal", null, Fabrica.Agora));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Recusa_nome_vazio(string nome)
    {
        var conta = Fabrica.Conta();

        Assert.Throws<ChannelsValidationException>(() => ChannelInstance.Register(conta.TenantId, conta, "1", null, nome, null, Fabrica.Agora));
    }

    [Theory]
    [InlineData("telefone")]
    [InlineData("+55 11 99999-0000 ext. 1")]
    public void Recusa_numero_de_exibicao_com_caractere_fora_do_conjunto(string exibicao)
    {
        var conta = Fabrica.Conta();

        Assert.Throws<ChannelsValidationException>(() => ChannelInstance.Register(conta.TenantId, conta, "1", exibicao, "Canal", null, Fabrica.Agora));
    }

    [Fact]
    public void Numero_de_exibicao_vazio_vira_nulo()
    {
        var conta = Fabrica.Conta();

        Assert.Null(ChannelInstance.Register(conta.TenantId, conta, "1", "   ", "Canal", null, Fabrica.Agora).DisplayPhoneNumber);
    }

    [Fact]
    public void Autorizar_unidade_registra_quem_autorizou_sobe_a_versao_e_e_idempotente()
    {
        var instancia = Fabrica.Instancia();
        var unidade = Guid.NewGuid();
        var autor = Guid.NewGuid();

        Assert.True(instancia.AuthorizeUnit(unidade, autor, Depois));
        var autorizada = Assert.Single(instancia.AuthorizedUnits);
        Assert.Equal(unidade, autorizada.BusinessUnitId);
        Assert.Equal(autor, autorizada.AuthorizedBy);
        Assert.Equal(instancia.Id, autorizada.ChannelInstanceId);
        Assert.Equal(instancia.TenantId, autorizada.TenantId);
        Assert.Equal(2, instancia.Version);
        Assert.Equal(Depois, instancia.UpdatedAt);

        Assert.False(instancia.AuthorizeUnit(unidade, autor, Depois.AddMinutes(1)));
        Assert.Single(instancia.AuthorizedUnits);
        Assert.Equal(2, instancia.Version);
        Assert.Equal(Depois, instancia.UpdatedAt);
    }

    [Fact]
    public void Autorizar_unidade_vazia_e_recusado()
    {
        Assert.Throws<ChannelsValidationException>(() => Fabrica.Instancia().AuthorizeUnit(Guid.Empty, null, Depois));
    }

    [Fact]
    public void Um_canal_pode_servir_varias_unidades()
    {
        var unidades = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var instancia = Fabrica.InstanciaComUnidades(unidades);

        Assert.Equal(unidades.Order(), instancia.AuthorizedUnits.Select(u => u.BusinessUnitId).Order());
        Assert.All(unidades, u => Assert.True(instancia.IsUnitAuthorized(u)));
        Assert.False(instancia.IsUnitAuthorized(Guid.NewGuid()));
    }

    [Fact]
    public void Unidade_padrao_precisa_estar_entre_as_autorizadas()
    {
        var autorizada = Guid.NewGuid();
        var instancia = Fabrica.InstanciaComUnidades(autorizada);

        Assert.Throws<ChannelsValidationException>(() => instancia.SetDefaults(Guid.NewGuid(), null, Depois));

        Assert.Null(instancia.DefaultBusinessUnitId);
        Assert.True(instancia.SetDefaults(autorizada, null, Depois));
        Assert.Equal(autorizada, instancia.DefaultBusinessUnitId);
    }

    [Fact]
    public void Sem_unidades_autorizadas_nenhuma_unidade_padrao_e_aceita()
    {
        Assert.Throws<ChannelsValidationException>(() => Fabrica.Instancia().SetDefaults(Guid.NewGuid(), null, Depois));
    }

    [Fact]
    public void Setor_padrao_exige_a_unidade_padrao()
    {
        var instancia = Fabrica.InstanciaComUnidades(Guid.NewGuid());

        Assert.Throws<ChannelsValidationException>(() => instancia.SetDefaults(null, Guid.NewGuid(), Depois));
    }

    [Fact]
    public void Padroes_com_identificador_vazio_sao_recusados()
    {
        var unidade = Guid.NewGuid();
        var instancia = Fabrica.InstanciaComUnidades(unidade);

        Assert.Throws<ChannelsValidationException>(() => instancia.SetDefaults(Guid.Empty, null, Depois));
        Assert.Throws<ChannelsValidationException>(() => instancia.SetDefaults(unidade, Guid.Empty, Depois));
    }

    [Fact]
    public void Defaults_repetidos_nao_mudam_nada_e_limpar_remove_o_padrao()
    {
        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();
        var instancia = Fabrica.InstanciaComUnidades(unidade);
        Assert.True(instancia.SetDefaults(unidade, setor, Depois));
        var versao = instancia.Version;

        Assert.False(instancia.SetDefaults(unidade, setor, Depois.AddMinutes(1)));
        Assert.Equal(versao, instancia.Version);

        Assert.True(instancia.SetDefaults(null, null, Depois.AddMinutes(2)));
        Assert.Null(instancia.DefaultBusinessUnitId);
        Assert.Null(instancia.DefaultDepartmentId);
        Assert.Equal(versao + 1, instancia.Version);
    }

    [Fact]
    public void Recusa_retirar_a_unidade_padrao_e_nao_escolhe_outra_no_lugar()
    {
        var padrao = Guid.NewGuid();
        var outra = Guid.NewGuid();
        var instancia = Fabrica.InstanciaComUnidades(padrao, outra);
        instancia.SetDefaults(padrao, null, Depois);

        Assert.Throws<ChannelsValidationException>(() => instancia.RevokeUnit(padrao, Depois));

        Assert.True(instancia.IsUnitAuthorized(padrao));
        Assert.Equal(padrao, instancia.DefaultBusinessUnitId);
    }

    [Fact]
    public void Retirar_a_unidade_depois_de_trocar_o_padrao_funciona()
    {
        var padrao = Guid.NewGuid();
        var outra = Guid.NewGuid();
        var instancia = Fabrica.InstanciaComUnidades(padrao, outra);
        instancia.SetDefaults(padrao, null, Depois);
        instancia.SetDefaults(outra, null, Depois);

        Assert.True(instancia.RevokeUnit(padrao, Depois));

        Assert.False(instancia.IsUnitAuthorized(padrao));
        Assert.Equal(outra, instancia.DefaultBusinessUnitId);
    }

    [Fact]
    public void Retirar_unidade_que_nao_estava_autorizada_nao_muda_nada()
    {
        var instancia = Fabrica.InstanciaComUnidades(Guid.NewGuid());
        var versao = instancia.Version;

        Assert.False(instancia.RevokeUnit(Guid.NewGuid(), Depois));

        Assert.Equal(versao, instancia.Version);
    }

    [Fact]
    public void Canal_ativo_nao_perde_a_ultima_unidade_mas_suspenso_pode()
    {
        var unica = Guid.NewGuid();
        var instancia = Fabrica.InstanciaComUnidades(unica);
        instancia.Activate(Depois);

        Assert.Throws<ChannelsValidationException>(() => instancia.RevokeUnit(unica, Depois));

        instancia.Suspend(Depois);
        Assert.True(instancia.RevokeUnit(unica, Depois));
        Assert.Empty(instancia.AuthorizedUnits);
    }

    [Fact]
    public void Ativar_exige_ao_menos_uma_unidade_autorizada()
    {
        var instancia = Fabrica.Instancia();

        Assert.Throws<ChannelsValidationException>(() => instancia.Activate(Depois));
        Assert.Equal(ChannelStatus.Inactive, instancia.Status);

        instancia.AuthorizeUnit(Guid.NewGuid(), null, Depois);
        Assert.True(instancia.Activate(Depois));
        Assert.Equal(ChannelStatus.Active, instancia.Status);
    }

    [Fact]
    public void Ativar_de_novo_nao_muda_nada_e_suspender_e_reativar_funciona()
    {
        var instancia = Fabrica.InstanciaComUnidades(Guid.NewGuid());
        Assert.True(instancia.Activate(Depois));
        var versao = instancia.Version;

        Assert.False(instancia.Activate(Depois));
        Assert.Equal(versao, instancia.Version);

        Assert.True(instancia.Suspend(Depois));
        Assert.Equal(ChannelStatus.Suspended, instancia.Status);
        Assert.False(instancia.Suspend(Depois));

        Assert.True(instancia.Activate(Depois));
        Assert.Equal(ChannelStatus.Active, instancia.Status);
    }

    [Fact]
    public void Canal_ainda_nao_ativado_nao_se_suspende()
    {
        Assert.Throws<ChannelsValidationException>(() => Fabrica.InstanciaComUnidades(Guid.NewGuid()).Suspend(Depois));
    }

    [Fact]
    public void Mudar_o_idioma_padrao_sobe_a_versao_so_quando_muda()
    {
        var instancia = Fabrica.Instancia(idioma: Locale.Parse("pt-BR"));

        Assert.False(instancia.ChangeDefaultLocale(Locale.Parse("pt-br"), Depois));
        Assert.Equal(1, instancia.Version);

        Assert.True(instancia.ChangeDefaultLocale(Locale.Parse("en-US"), Depois));
        Assert.Equal(Locale.Parse("en-US"), instancia.DefaultLocale);
        Assert.Equal(2, instancia.Version);

        Assert.True(instancia.ChangeDefaultLocale(null, Depois));
        Assert.Null(instancia.DefaultLocale);
    }

    [Fact]
    public void A_rota_de_entrada_copia_o_tenant_o_provedor_e_o_identificador_do_numero()
    {
        var instancia = Fabrica.Instancia(numero: "987654321");

        var rota = ChannelRoute.For(instancia);

        Assert.Equal(instancia.TenantId, rota.TenantId);
        Assert.Equal(instancia.Provider, rota.Provider);
        Assert.Equal("987654321", rota.ExternalId);
        Assert.Equal(instancia.Id, rota.ChannelInstanceId);
    }
}
