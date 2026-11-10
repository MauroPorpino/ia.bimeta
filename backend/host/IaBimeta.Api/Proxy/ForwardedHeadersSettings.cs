using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace IaBimeta.Api.Proxy;

/// <summary>
/// A confiança nos cabeçalhos de encaminhamento de um proxy reverso (seção <c>ForwardedHeaders</c>, ADR 0033). Atrás do proxy (o Caddy da implantação), o endereço da conexão é sempre o do proxy; sem confiar nos cabeçalhos
/// <c>X-Forwarded-For</c> e <c>X-Forwarded-Proto</c>, o limite de login por origem trataria todos os usuários como UM só endereço. <b>Desligado por padrão.</b> Ligado, SÓ os proxies e as redes listados (<c>KnownProxies</c> e
/// <c>KnownNetworks</c>, em CIDR) são confiáveis: o cabeçalho de qualquer outra origem é ignorado (um cliente não escolhe o próprio endereço), e a lista de confiáveis nunca herda o laço local por padrão. Ligado sem nenhum
/// endereço confiável, o Api <b>não sobe</b> (configuração contraditória).
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; set; }

    /// <summary>Endereços IP de proxies confiáveis (por exemplo, <c>172.29.89.2</c>).</summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>Redes confiáveis em notação CIDR (por exemplo, <c>172.29.89.0/24</c>).</summary>
    public List<string> KnownNetworks { get; set; } = [];

    /// <summary>Quantos saltos de proxy são lidos no <c>X-Forwarded-For</c> (um só proxy na implantação).</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <exception cref="InvalidOperationException">A configuração é inválida ou contraditória.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (KnownProxies.Count == 0 && KnownNetworks.Count == 0)
        {
            throw new InvalidOperationException("ForwardedHeaders:Enabled exige ao menos um endereço confiável em ForwardedHeaders:KnownProxies ou ForwardedHeaders:KnownNetworks: sem isso nenhum cabeçalho de encaminhamento seria confiável.");
        }

        if (ForwardLimit is < 1 or > 5)
        {
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit deve ficar entre 1 e 5.");
        }

        foreach (var proxy in KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                throw new InvalidOperationException("ForwardedHeaders:KnownProxies tem um endereço IP inválido.");
            }
        }

        foreach (var rede in KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(rede, out _))
            {
                throw new InvalidOperationException("ForwardedHeaders:KnownNetworks tem uma rede inválida (use CIDR, por exemplo 172.29.89.0/24).");
            }
        }
    }

    /// <summary>Aplica a configuração (já validada) às opções do ASP.NET Core: só <c>X-Forwarded-For</c> e <c>X-Forwarded-Proto</c>, e só de origem confiável.</summary>
    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = ForwardLimit;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var rede in KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(rede));
        }
    }
}
