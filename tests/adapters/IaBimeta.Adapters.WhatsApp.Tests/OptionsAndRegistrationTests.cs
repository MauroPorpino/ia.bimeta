using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>A CONFIGURAÇÃO do adaptador (HTTPS obrigatório, versão da Graph API, limites) e o registro por DI: a configuração inválida falha na partida, nunca no meio do processamento.</summary>
public sealed class OptionsAndRegistrationTests
{
    [Fact]
    public void Os_padroes_sao_a_Graph_API_oficial_em_HTTPS_na_versao_da_tabela_oficial()
    {
        var opcoes = new WhatsAppOptions();

        opcoes.Validate();

        Assert.Equal("https://graph.facebook.com/", opcoes.BaseAddress);
        Assert.Equal("v26.0", opcoes.GraphApiVersion);
        Assert.Equal(WhatsAppOptions.DefaultGraphApiVersion, opcoes.GraphApiVersion);
        Assert.Equal("https://graph.facebook.com/v26.0/", opcoes.VersionedBase.ToString());
    }

    [Theory]
    [InlineData("https://graph.facebook.com/")]
    [InlineData("https://graph.facebook.com")]
    [InlineData("http://localhost:5099/")]
    [InlineData("http://127.0.0.1:5099/")]
    [InlineData("http://[::1]:5099/")]
    [InlineData("https://localhost:5099/")]
    public void Enderecos_https_e_http_em_laco_local_sao_aceitos(string endereco)
    {
        new WhatsAppOptions { BaseAddress = endereco }.Validate();
    }

    [Theory]
    [InlineData("http://graph.facebook.com/")]
    [InlineData("http://example.com/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.0.10:8080/")]
    [InlineData("http://localhost.evil.example/")]
    [InlineData("http://127.0.0.1.evil.example/")]
    [InlineData("ftp://graph.facebook.com/")]
    [InlineData("graph.facebook.com")]
    [InlineData("/v26.0/")]
    [InlineData("")]
    [InlineData("https://user:senha@graph.facebook.com/")]
    [InlineData("https://graph.facebook.com/?access_token=x")]
    [InlineData("https://graph.facebook.com/#frag")]
    public void Endereco_http_fora_do_laco_local_ou_com_usuario_consulta_ou_fragmento_e_recusado(string endereco)
    {
        var opcoes = new WhatsAppOptions { BaseAddress = endereco };

        Assert.Throws<InvalidOperationException>(opcoes.Validate);
    }

    [Theory]
    [InlineData("v26.0")]
    [InlineData("v25.0")]
    [InlineData("v27.0")]
    [InlineData("v100.10")]
    public void Versoes_no_formato_vNN_ponto_N_sao_aceitas(string versao)
    {
        new WhatsAppOptions { GraphApiVersion = versao }.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("26.0")]
    [InlineData("v26")]
    [InlineData("v26.0/")]
    [InlineData("v26.0/../x")]
    [InlineData("../v26.0")]
    [InlineData("v26.0?x=1")]
    [InlineData("V26.0")]
    [InlineData("v1234.0")]
    public void Versao_fora_do_formato_e_recusada_para_nao_injetar_caminho(string versao)
    {
        var opcoes = new WhatsAppOptions { GraphApiVersion = versao };

        Assert.Throws<InvalidOperationException>(opcoes.Validate);
    }

    [Fact]
    public void Limites_invalidos_sao_recusados()
    {
        Assert.Throws<InvalidOperationException>(new WhatsAppOptions { RequestTimeout = TimeSpan.Zero }.Validate);
        Assert.Throws<InvalidOperationException>(new WhatsAppOptions { RequestTimeout = TimeSpan.FromMinutes(6) }.Validate);
        Assert.Throws<InvalidOperationException>(new WhatsAppOptions { MaxResponseBytes = 100 }.Validate);
        Assert.Throws<InvalidOperationException>(new WhatsAppOptions { MaxResponseBytes = 100_000_000 }.Validate);
        Assert.Throws<InvalidOperationException>(new WhatsAppOptions { ConnectTimeout = TimeSpan.Zero }.Validate);
        Assert.Throws<InvalidOperationException>(new WhatsAppOptions { PooledConnectionLifetime = TimeSpan.FromSeconds(-1) }.Validate);
    }

    [Fact]
    public void O_adaptador_recusa_na_construcao_uma_configuracao_http_nao_loopback()
    {
        var opcoes = new WhatsAppOptions { BaseAddress = "http://graph.facebook.com/" };

        Assert.Throws<InvalidOperationException>(() => new WhatsAppCloudAdapter(new HttpClient(new FakeHandler()), opcoes));
        Assert.Throws<InvalidOperationException>(() => WhatsAppCloudAdapter.Create(opcoes));
    }

    [Fact]
    public async Task Alterar_o_objeto_de_opcoes_depois_da_construcao_nao_redireciona_o_token_para_um_endereco_sem_validacao()
    {
        // O adaptador guarda uma cópia dos valores JÁ VALIDADOS: o token da conta nunca sai em claro nem para um endereço que não passou por Validate().
        var opcoes = new WhatsAppOptions();
        var handler = new FakeHandler();
        using var adaptador = new WhatsAppCloudAdapter(new HttpClient(handler), opcoes);

        opcoes.BaseAddress = "http://servidor-malicioso.exemplo/";
        opcoes.GraphApiVersion = "v1.0";
        opcoes.RequestTimeout = TimeSpan.FromTicks(1);
        await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal($"https://graph.facebook.com/v26.0/{Sends.PhoneNumberId}/messages", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public void O_registro_por_DI_expoe_o_adaptador_como_IChannelProviderAdapter_do_WhatsApp_com_efeito_nenhum()
    {
        var services = new ServiceCollection();
        services.AddWhatsAppAdapter(o => o.GraphApiVersion = "v27.0");
        using var provider = services.BuildServiceProvider();

        var adaptador = provider.GetRequiredService<IChannelProviderAdapter>();

        Assert.IsType<WhatsAppCloudAdapter>(adaptador);
        Assert.Equal(ChannelProvider.WhatsAppCloud, adaptador.Provider);
        Assert.Equal(ProviderEffectStrategy.None, adaptador.EffectStrategy);
        Assert.Equal("v27.0", provider.GetRequiredService<WhatsAppOptions>().GraphApiVersion);
        Assert.Same(adaptador, provider.GetRequiredService<IChannelProviderAdapter>());
    }

    [Fact]
    public void O_registro_com_configuracao_invalida_falha_na_partida()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddWhatsAppAdapter(o => o.BaseAddress = "http://graph.facebook.com/"));
        Assert.Throws<InvalidOperationException>(() => services.AddWhatsAppAdapter(o => o.GraphApiVersion = "latest"));
        Assert.Empty(services);
    }

    [Fact]
    public void O_registro_sem_configuracao_usa_os_padroes()
    {
        var services = new ServiceCollection();
        services.AddWhatsAppAdapter();
        using var provider = services.BuildServiceProvider();

        Assert.Equal("v26.0", provider.GetRequiredService<WhatsAppOptions>().GraphApiVersion);
        Assert.NotNull(provider.GetRequiredService<IChannelProviderAdapter>());
    }
}
