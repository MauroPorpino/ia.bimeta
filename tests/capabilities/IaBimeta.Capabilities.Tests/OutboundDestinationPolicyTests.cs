using System.Net;
using IaBimeta.Capabilities.Domain;

namespace IaBimeta.Capabilities.Tests;

/// <summary>A política de destino (SSRF): o que NUNCA pode ser alcançado, o que só o operador libera e o que é público.</summary>
public class OutboundDestinationPolicyTests
{
    private static OutboundDestinationPolicy Default() => new(new OutboundOptions());

    private static OutboundDestinationPolicy With(Action<OutboundOptions> configure)
    {
        var opcoes = new OutboundOptions();
        configure(opcoes);
        return new OutboundDestinationPolicy(opcoes);
    }

    [Theory]
    [InlineData("https://erp.example.com/api")]
    [InlineData("https://erp.example.com:443/api")]
    [InlineData("https://93.184.216.34/api")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/api")]
    public void Destinos_publicos_https_sao_aceitos(string url) => Assert.Null(Default().CheckUri(new Uri(url)));

    [Theory]
    [InlineData("http://erp.example.com/api", "scheme-not-allowed")]
    [InlineData("ftp://erp.example.com/api", "scheme-not-allowed")]
    [InlineData("file:///etc/passwd", "scheme-not-allowed")]
    [InlineData("https://user:senha@erp.example.com/api", "credentials-in-url")]
    [InlineData("https://erp.example.com:8443/api", "port-not-allowed")]
    [InlineData("https://localhost/api", "internal-hostname")]
    [InlineData("https://LOCALHOST./api", "internal-hostname")]
    [InlineData("https://app.localhost/api", "internal-hostname")]
    [InlineData("https://impressora.local/api", "internal-hostname")]
    [InlineData("https://srv.internal/api", "internal-hostname")]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/", "internal-hostname")]
    [InlineData("https://erp/api", "internal-hostname")]
    public void Esquemas_portas_credenciais_e_nomes_internos_sao_recusados(string url, string motivo) => Assert.Equal(motivo, Default().CheckUri(new Uri(url)));

    [Theory]
    [InlineData("https://127.0.0.1/", "private-address")]
    [InlineData("https://127.1.2.3/", "private-address")]
    [InlineData("https://[::1]/", "private-address")]
    [InlineData("https://10.0.0.5/", "private-address")]
    [InlineData("https://172.16.0.1/", "private-address")]
    [InlineData("https://172.31.255.255/", "private-address")]
    [InlineData("https://192.168.1.10/", "private-address")]
    [InlineData("https://100.64.0.1/", "private-address")]
    [InlineData("https://[fc00::1]/", "private-address")]
    [InlineData("https://[fd12:3456::1]/", "private-address")]
    [InlineData("https://169.254.169.254/latest/meta-data/", "blocked-address")]
    [InlineData("https://169.254.170.2/", "blocked-address")]
    [InlineData("https://100.100.100.200/", "blocked-address")]
    [InlineData("https://192.0.0.192/", "blocked-address")]
    [InlineData("https://[fd00:ec2::254]/", "blocked-address")]
    [InlineData("https://[fe80::1]/", "blocked-address")]
    [InlineData("https://0.0.0.0/", "blocked-address")]
    [InlineData("https://224.0.0.1/", "blocked-address")]
    [InlineData("https://240.0.0.1/", "blocked-address")]
    [InlineData("https://255.255.255.255/", "blocked-address")]
    [InlineData("https://[::]/", "blocked-address")]
    [InlineData("https://[64:ff9b::7f00:1]/", "blocked-address")]
    [InlineData("https://[2002:7f00:1::1]/", "blocked-address")]
    public void Enderecos_literais_internos_e_reservados_sao_recusados(string url, string motivo) => Assert.Equal(motivo, Default().CheckUri(new Uri(url)));

    [Theory]
    [InlineData("https://2130706433/")]
    [InlineData("https://0x7f.1/")]
    [InlineData("https://0177.0.0.1/")]
    [InlineData("https://127.1/")]
    public void Formas_alternativas_de_escrever_o_loopback_sao_recusadas(string url) => Assert.NotNull(Default().CheckUri(new Uri(url)));

    [Theory]
    [InlineData("::ffff:127.0.0.1", "private-address")]
    [InlineData("::ffff:10.1.2.3", "private-address")]
    [InlineData("::ffff:169.254.169.254", "blocked-address")]
    [InlineData("::ffff:192.168.0.1", "private-address")]
    public void IPv4_mapeado_em_IPv6_vale_como_o_IPv4_de_dentro(string endereco, string motivo) => Assert.Equal(motivo, Default().CheckAddress(IPAddress.Parse(endereco)));

    [Fact]
    public void O_operador_libera_uma_rede_privada_so_no_CIDR_configurado()
    {
        var politica = With(o => o.AllowedPrivateCidrs.Add("10.20.0.0/16"));

        Assert.Null(politica.CheckAddress(IPAddress.Parse("10.20.5.9")));
        Assert.Equal("private-address", politica.CheckAddress(IPAddress.Parse("10.21.0.1")));
        Assert.Equal("private-address", politica.CheckAddress(IPAddress.Parse("10.0.0.1")));
        Assert.Equal("private-address", politica.CheckAddress(IPAddress.Parse("127.0.0.1")));
    }

    [Theory]
    [InlineData("169.254.0.0/16", "169.254.169.254")]
    [InlineData("169.254.169.254/32", "169.254.169.254")]
    [InlineData("0.0.0.0/0", "169.254.169.254")]
    [InlineData("0.0.0.0/0", "224.0.0.1")]
    [InlineData("100.64.0.0/10", "100.100.100.200")]
    [InlineData("fd00::/8", "fd00:ec2::254")]
    [InlineData("fe80::/10", "fe80::1")]
    public void Metadados_de_nuvem_e_link_local_nunca_sao_liberaveis_nem_pelo_operador(string cidr, string endereco)
    {
        var politica = With(o => o.AllowedPrivateCidrs.Add(cidr));

        Assert.Equal("blocked-address", politica.CheckAddress(IPAddress.Parse(endereco)));
    }

    [Fact]
    public void Http_e_portas_extras_so_por_configuracao_do_operador()
    {
        var politica = With(o =>
        {
            o.AllowHttp = true;
            o.AllowedPorts.AddRange([80, 8443]);
        });

        Assert.Null(politica.CheckUri(new Uri("http://erp.example.com/api")));
        Assert.Null(politica.CheckUri(new Uri("https://erp.example.com:8443/api")));
        Assert.Equal("port-not-allowed", politica.CheckUri(new Uri("https://erp.example.com/api")));
        Assert.Equal("port-not-allowed", politica.CheckUri(new Uri("https://erp.example.com:9000/api")));
    }

    [Fact]
    public void Uri_relativa_ou_nula_e_recusada() => Assert.Equal("invalid-url", Default().CheckUri(null));

    [Fact]
    public void Opcoes_com_CIDR_ou_porta_invalidos_falham_na_partida()
    {
        Assert.Throws<InvalidOperationException>(() => new CapabilitiesOptions { Outbound = { AllowedPrivateCidrs = ["nao-e-cidr"] } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CapabilitiesOptions { Outbound = { AllowedPorts = [70000] } }.Validate());
        new CapabilitiesOptions().Validate();
    }
}
