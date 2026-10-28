using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Tests;

/// <summary>Monta entidades de teste com dados sintéticos.</summary>
internal static class Fabrica
{
    public static readonly DateTimeOffset Agora = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public static TenantId NovoTenant() => new(Guid.NewGuid());

    public static ChannelAccount Conta(TenantId? tenant = null, Guid? organizacao = null) =>
        ChannelAccount.Create(tenant ?? NovoTenant(), organizacao ?? Guid.NewGuid(), ChannelProvider.WhatsAppCloud, "100200300", "Conta de teste", Agora);

    public static ChannelInstance Instancia(ChannelAccount? conta = null, string numero = "555000111", Locale? idioma = null)
    {
        conta ??= Conta();
        return ChannelInstance.Register(conta.TenantId, conta, numero, "+55 11 99999-0000", "Número de teste", idioma, Agora);
    }

    /// <summary>Uma instância já com as unidades autorizadas informadas.</summary>
    public static ChannelInstance InstanciaComUnidades(params Guid[] unidades)
    {
        var instancia = Instancia();
        foreach (var unidade in unidades)
        {
            instancia.AuthorizeUnit(unidade, null, Agora);
        }

        return instancia;
    }
}
