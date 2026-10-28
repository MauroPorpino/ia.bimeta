using IaBimeta.Channels.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>
/// A rota de entrada: do identificador do número no provedor (que vem no webhook) até a instância e o tenant. É o único
/// registro do módulo consultado ANTES de saber o tenant, porque o webhook só traz o identificador do provedor e o tenant
/// nunca vem de dado externo. Por isso é uma tabela estreita, só com as chaves de roteamento (nada de configuração nem de
/// credencial), sem filtro de tenant na leitura e com integridade garantida pelo banco: o identificador é único na
/// plataforma e uma chave estrangeira composta liga a rota à instância e ao mesmo identificador, de modo que ela não pode
/// apontar para outro tenant nem divergir do cadastro.
/// <para>
/// A rota é imutável e nasce junto com a instância. Achá-la NÃO autentica o webhook: a assinatura ainda precisa ser validada
/// com o segredo da conta antes de qualquer contexto de tenant ser criado (gated por BOUNDARIES.md PRV-2).
/// </para>
/// </summary>
public sealed class ChannelRoute : ITenantOwned
{
    private ChannelRoute(TenantId tenantId, ChannelProvider provider, string externalId, ChannelInstanceId channelInstanceId, DateTimeOffset createdAt)
    {
        TenantId = tenantId;
        Provider = provider;
        ExternalId = externalId;
        ChannelInstanceId = channelInstanceId;
        CreatedAt = createdAt;
    }

    public TenantId TenantId { get; }

    public ChannelProvider Provider { get; }

    /// <summary>O identificador do número no provedor (Phone Number ID).</summary>
    public string ExternalId { get; }

    public ChannelInstanceId ChannelInstanceId { get; }

    public DateTimeOffset CreatedAt { get; }

    public static ChannelRoute For(ChannelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new ChannelRoute(instance.TenantId, instance.Provider, instance.PhoneNumberId, instance.Id, instance.CreatedAt);
    }
}
