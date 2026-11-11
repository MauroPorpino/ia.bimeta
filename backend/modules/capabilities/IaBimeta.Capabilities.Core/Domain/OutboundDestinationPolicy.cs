using System.Net;
using System.Net.Sockets;

namespace IaBimeta.Capabilities.Domain;

/// <summary>
/// A política de destino das chamadas de saída (ADR 0035, §4): o que impede o SSRF. É PURA (sem rede), então vale igual no cadastro da conexão e, o que importa mais, no instante da conexão de rede (o
/// <c>ConnectCallback</c> do transporte confere cada endereço RESOLVIDO: não há janela entre verificar e conectar, então DNS rebinding não funciona). Regras: só <c>https</c> (<c>http</c> só por configuração do operador);
/// sem credencial na URL; nomes internos recusados; porta permitida; todo endereço de loopback, rede privada, CGNAT, link-local, multicast, reservado, NAT64, 6to4 e IPv4 mapeado em IPv6 recusado, e as faixas de
/// metadados de nuvem e link-local NUNCA são liberáveis, nem pelo operador. Um destino interno legítimo (ERP em VPN) só é alcançável se o operador liberou o CIDR.
/// </summary>
public sealed class OutboundDestinationPolicy
{
    // Liberáveis pelo operador (redes privadas e de loopback).
    private static readonly IPNetwork[] Overridable =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
    ];

    // NUNCA liberáveis: link-local (inclui 169.254.169.254, o metadado AWS/GCP/Azure), multicast, reservados, documentação, benchmark, NAT64, 6to4, Teredo,
    // e os metadados de outras nuvens (Alibaba 100.100.100.200, Oracle 192.0.0.192, AWS IPv6 fd00:ec2::254).
    private static readonly IPNetwork[] NeverAllowed =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("100.100.100.200/32"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/32"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("fd00:ec2::/32"),
    ];

    private static readonly string[] InternalSuffixes = [".localhost", ".local", ".internal", ".localdomain", ".lan", ".intranet", ".corp", ".home.arpa"];

    private readonly OutboundOptions _options;
    private readonly IPNetwork[] _allowedPrivate;

    public OutboundDestinationPolicy(OutboundOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _allowedPrivate = options.AllowedPrivateCidrs.Select(IPNetwork.Parse).ToArray();
    }

    /// <summary>Confere a URL de uma conexão (cadastro) ou de uma chamada. Devolve o código curto da recusa, ou <c>null</c> se pode.</summary>
    public string? CheckUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return "invalid-url";
        }

        var https = uri.Scheme == Uri.UriSchemeHttps;
        if (!https && !(uri.Scheme == Uri.UriSchemeHttp && _options.AllowHttp))
        {
            return "scheme-not-allowed";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "credentials-in-url";
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return "invalid-url";
        }

        if (!IsPortAllowed(uri.Port, https))
        {
            return "port-not-allowed";
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return IPAddress.TryParse(uri.DnsSafeHost, out var literal) ? CheckAddress(literal) : "invalid-url";
        }

        return IsInternalHostname(uri.IdnHost) ? "internal-hostname" : null;
    }

    public bool IsPortAllowed(int port, bool https)
    {
        if (_options.AllowedPorts.Count > 0)
        {
            return _options.AllowedPorts.Contains(port);
        }

        return https ? port == 443 : port == 80;
    }

    /// <summary>Confere UM endereço resolvido. Devolve o código da recusa, ou <c>null</c> se pode conectar.</summary>
    public string? CheckAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // IPv4 mapeado em IPv6 (::ffff:a.b.c.d) vale como o IPv4 de dentro: sem esta normalização, ::ffff:127.0.0.1 passaria.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var faixa in NeverAllowed)
        {
            if (faixa.Contains(address))
            {
                return "blocked-address";
            }
        }

        foreach (var faixa in Overridable)
        {
            if (faixa.Contains(address))
            {
                return _allowedPrivate.Any(liberada => liberada.Contains(address)) ? null : "private-address";
            }
        }

        // Todo o resto é endereço público roteável.
        return null;
    }

    /// <summary>Nomes que só existem dentro de uma rede: <c>localhost</c>, <c>*.local</c>, <c>*.internal</c>, os metadados de nuvem e qualquer rótulo único (sem ponto).</summary>
    public static bool IsInternalHostname(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var nome = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (nome.Length == 0 || nome == "localhost" || nome == "metadata" || nome == "metadata.google.internal" || !nome.Contains('.'))
        {
            return true;
        }

        return InternalSuffixes.Any(sufixo => nome.EndsWith(sufixo, StringComparison.Ordinal));
    }
}
