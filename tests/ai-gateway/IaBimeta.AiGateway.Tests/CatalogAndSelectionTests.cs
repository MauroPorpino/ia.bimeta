using IaBimeta.AiGateway.Application;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.AiGateway.Tests;

/// <summary>O catálogo de modelos por configuração: só entra o que faz sentido, e o que não faz falha na criação (na partida), não numa chamada.</summary>
public class ModelCatalogTests
{
    private static AiModelDescriptor Modelo(
        string provedor = "provider-a",
        string modelo = "model-1",
        string perfil = "profile-1",
        int janela = 100_000,
        int saida = 4_000,
        AiModelPrices? precos = null,
        AiModelStatus status = AiModelStatus.Active) =>
        new(provedor, modelo, perfil, janela, saida, precos ?? GatewayHarness.Prices, status);

    [Fact]
    public void Um_catalogo_valido_busca_pelo_provedor_e_pelo_modelo_diferenciando_maiusculas()
    {
        var catalogo = AiModelCatalog.Create(new[] { Modelo(), Modelo("provider-b", "model-9") });

        Assert.Equal("model-1", catalogo.Find("provider-a", "model-1")!.ModelId);
        Assert.Equal("provider-b", catalogo.Find("provider-b", "model-9")!.ProviderId);
        Assert.Null(catalogo.Find("provider-a", "model-9")); // o modelo de outro provedor não vale neste
        Assert.Null(catalogo.Find("provider-a", "MODEL-1"));
        Assert.Null(catalogo.Find("Provider-A", "model-1"));
        Assert.Null(catalogo.Find(null, "model-1"));
        Assert.Null(catalogo.Find("provider-a", null));
        Assert.Equal(new[] { "provider-a", "provider-b" }, catalogo.ProviderIds);
    }

    [Fact]
    public void A_ordem_do_catalogo_e_estavel_e_os_desativados_ficam_fora_dos_ativos()
    {
        var catalogo = AiModelCatalog.Create(new[]
        {
            Modelo("provider-b", "model-2"), Modelo("provider-a", "model-2", status: AiModelStatus.Disabled), Modelo("provider-a", "model-1"),
        });

        Assert.Equal(new[] { "provider-a/model-1", "provider-a/model-2", "provider-b/model-2" }, catalogo.Models.Select(m => $"{m.ProviderId}/{m.ModelId}").ToArray());
        Assert.Equal(new[] { "model-1" }, catalogo.ActiveModelsOf("provider-a").Select(m => m.ModelId).ToArray());
        Assert.Empty(catalogo.ActiveModelsOf("provider-z"));
    }

    [Fact]
    public void Um_catalogo_vazio_e_valido_e_nao_encontra_modelo_algum()
    {
        var catalogo = AiModelCatalog.Create(Array.Empty<AiModelDescriptor>());

        Assert.Empty(catalogo.Models);
        Assert.Null(catalogo.Find("provider-a", "model-1"));
    }

    [Fact]
    public void O_mesmo_modelo_do_mesmo_provedor_duas_vezes_e_recusado_mas_o_mesmo_nome_em_outro_provedor_nao()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(), Modelo() }));

        var catalogo = AiModelCatalog.Create(new[] { Modelo("provider-a", "mesmo"), Modelo("provider-b", "mesmo") });
        Assert.Equal(2, catalogo.Models.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Provider-A")]
    [InlineData("provider a")]
    [InlineData("-provider")]
    [InlineData("provedor/a")]
    public void Identificador_de_provedor_invalido_e_recusado(string provedor)
    {
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(provedor: provedor) }));
        Assert.False(AiModelCatalog.IsValidProviderId(provedor));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" modelo")]
    [InlineData("modelo com espaço")]
    [InlineData("modelo\n")]
    public void Identificador_de_modelo_invalido_e_recusado(string modelo)
    {
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(modelo: modelo) }));
    }

    [Fact]
    public void Perfil_janela_e_teto_de_saida_invalidos_sao_recusados()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(perfil: "") }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(janela: 0) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(saida: 0) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(janela: 1_000, saida: 1_001) }));
        AiModelCatalog.Create(new[] { Modelo(janela: 1_000, saida: 1_000) }); // o teto igual à janela ainda é coerente
    }

    [Fact]
    public void Preco_negativo_ou_moeda_invalida_e_recusado()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("USD", -0.01m, 0, 0, 0)) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("USD", 0, -1, 0, 0)) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("USD", 0, 0, -1, 0)) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("USD", 0, 0, 0, -1)) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("usd", 1, 1, 1, 1)) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("DOLAR", 1, 1, 1, 1)) }));
        AiModelCatalog.Create(new[] { Modelo(precos: new AiModelPrices("BRL", 0, 0, 0, 0)) }); // preço zero é válido (modelo gratuito)
    }

    [Fact]
    public void Situacao_desconhecida_e_entrada_nula_sao_recusadas()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new[] { Modelo(status: (AiModelStatus)99) }));
        Assert.Throws<AiGatewayConfigurationException>(() => AiModelCatalog.Create(new AiModelDescriptor[] { null! }));
        Assert.Throws<ArgumentNullException>(() => AiModelCatalog.Create(null!));
    }
}

/// <summary>
/// A seleção de provedor e modelo por tenant (ADR 0017, seção D e critério 2): o tenant A com o provedor A só chama o adaptador A; o tenant B, só o B;
/// modelo fora do catálogo é recusado; <c>PlatformManaged</c> é recusado; e nada cai em outro provedor.
/// </summary>
public class ModelSelectionTests
{
    private static AiTenantConfiguration Config(
        TenantId tenant,
        string provedor,
        string modelo,
        AiCredentialSource origem = AiCredentialSource.Byok,
        AiTenantLimits? limites = null) =>
        new(tenant, provedor, modelo, origem, limites ?? GatewayHarness.GenerousLimits);

    [Fact]
    public void Cada_tenant_recebe_o_adaptador_e_o_modelo_da_propria_configuracao()
    {
        var h = new GatewayHarness();

        var a = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "model-a1"));
        var b = h.Selector.Select(GatewayHarness.TenantB, Config(GatewayHarness.TenantB, GatewayHarness.ProviderBId, "model-b1"));

        Assert.True(a.IsSelected);
        Assert.Same(h.ProviderA, a.Provider);
        Assert.Equal("model-a1", a.Model!.ModelId);
        Assert.True(b.IsSelected);
        Assert.Same(h.ProviderB, b.Provider);
        Assert.Equal("model-b1", b.Model!.ModelId);
        Assert.NotSame(a.Provider, b.Provider);
    }

    [Fact]
    public void Sem_configuracao_o_tenant_nao_tem_selecao_e_nada_e_presumido()
    {
        var h = new GatewayHarness();

        var selecao = h.Selector.Select(GatewayHarness.TenantA, null);

        Assert.Equal(AiSelectionOutcome.NotConfigured, selecao.Outcome);
        Assert.Equal(AiFailureCodes.NotConfigured, selecao.Code);
        Assert.Null(selecao.Provider);
        Assert.Null(selecao.Model);
    }

    [Fact]
    public void A_configuracao_de_outro_tenant_nunca_e_usada_nem_devolvida()
    {
        var h = new GatewayHarness();

        var selecao = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantB, GatewayHarness.ProviderBId, "model-b1"));

        Assert.Equal(AiSelectionOutcome.TenantMismatch, selecao.Outcome);
        Assert.Null(selecao.Configuration);
        Assert.Null(selecao.Provider);
        Assert.Null(selecao.Model);
    }

    [Theory]
    [InlineData(AiCredentialSource.PlatformManaged)]
    [InlineData((AiCredentialSource)0)]
    [InlineData((AiCredentialSource)99)]
    public void PlatformManaged_e_qualquer_origem_desconhecida_sao_recusadas_mesmo_com_modelo_valido(AiCredentialSource origem)
    {
        var h = new GatewayHarness();

        var selecao = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "model-a1", origem));

        Assert.Equal(AiSelectionOutcome.CredentialSourceNotSupported, selecao.Outcome);
        Assert.Null(selecao.Provider);
        Assert.Null(selecao.Model);
    }

    [Fact]
    public void Modelo_fora_do_catalogo_ou_de_outro_provedor_e_recusado_sem_fallback()
    {
        var h = new GatewayHarness();

        var inexistente = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "model-inexistente"));
        var deOutroProvedor = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "model-b1"));
        var provedorInexistente = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, "provider-z", "model-a1"));
        var maiusculas = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "MODEL-A1"));

        foreach (var selecao in new[] { inexistente, deOutroProvedor, provedorInexistente, maiusculas })
        {
            Assert.Equal(AiSelectionOutcome.ModelNotSupported, selecao.Outcome);
            Assert.Equal(AiFailureCodes.ModelNotSupported, selecao.Code);
            Assert.Null(selecao.Provider);
        }
    }

    [Fact]
    public void Modelo_desativado_no_catalogo_e_recusado()
    {
        var h = new GatewayHarness();

        var selecao = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "model-a2"));

        Assert.Equal(AiSelectionOutcome.ModelDisabled, selecao.Outcome);
        Assert.Null(selecao.Provider);
    }

    [Fact]
    public void Provedor_do_catalogo_sem_adaptador_registrado_e_recusado()
    {
        var h = new GatewayHarness();

        var selecao = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderWithoutAdapterId, "model-c1"));

        Assert.Equal(AiSelectionOutcome.ProviderNotRegistered, selecao.Outcome);
        Assert.Null(selecao.Provider);
    }

    [Fact]
    public void Limites_invalidos_sao_recusados()
    {
        var h = new GatewayHarness();

        foreach (var limites in new[]
        {
            GatewayHarness.GenerousLimits with { MaxConcurrentCalls = 0 },
            GatewayHarness.GenerousLimits with { MaxCallsPerPeriod = 0 },
            GatewayHarness.GenerousLimits with { MaxTokensPerPeriod = 0 },
            GatewayHarness.GenerousLimits with { Period = TimeSpan.Zero },
            GatewayHarness.GenerousLimits with { MaxInputMessages = -1 },
            GatewayHarness.GenerousLimits with { MaxOutputTokens = 0 },
        })
        {
            var selecao = h.Selector.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, GatewayHarness.ProviderAId, "model-a1", limites: limites));

            Assert.Equal(AiSelectionOutcome.InvalidLimits, selecao.Outcome);
        }
    }

    [Fact]
    public void O_registro_de_adaptadores_recusa_identificador_repetido_ou_invalido()
    {
        Assert.Throws<AiGatewayConfigurationException>(() => new AiProviderRegistry(new IAiProvider[] { new FakeAiProvider("provider-a"), new FakeAiProvider("provider-a") }));
        Assert.Throws<AiGatewayConfigurationException>(() => new AiProviderRegistry(new IAiProvider[] { new FakeAiProvider("Provider A") }));

        var registro = new AiProviderRegistry(new IAiProvider[] { new FakeAiProvider("provider-a"), new FakeAiProvider("provider-b") });
        Assert.Equal(new[] { "provider-a", "provider-b" }, registro.ProviderIds.OrderBy(i => i, StringComparer.Ordinal).ToArray());
        Assert.Null(registro.Find("provider-z"));
        Assert.Null(registro.Find(null));
    }

    [Fact]
    public void A_escolha_do_catalogo_e_so_do_catalogo_um_provedor_novo_nao_exige_mudanca_no_Core()
    {
        // Um terceiro provedor sintético entra só com adaptador e catálogo; o seletor o atende sem conhecê-lo pelo nome.
        var novo = new FakeAiProvider("provider-novo");
        var catalogo = AiModelCatalog.Create(new[] { GatewayHarness.Model("provider-novo", "modelo-x", "perfil-x", AiModelStatus.Active) });
        var seletor = new AiModelSelector(catalogo, new AiProviderRegistry(new IAiProvider[] { novo }));

        var selecao = seletor.Select(GatewayHarness.TenantA, Config(GatewayHarness.TenantA, "provider-novo", "modelo-x"));

        Assert.True(selecao.IsSelected);
        Assert.Same(novo, selecao.Provider);
    }
}
