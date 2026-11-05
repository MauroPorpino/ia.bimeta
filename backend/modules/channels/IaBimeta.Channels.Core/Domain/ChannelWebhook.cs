using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>
/// O ponto de entrada do webhook de uma conta do provedor (ADR 0011, A.1, item 2): um identificador OPACO e imprevisível, que aparece na URL e leva à conta e ao segredo de assinatura dela SEM ler o
/// corpo. É consultado antes de o tenant ser conhecido (a URL é tudo o que há), por isso é uma tabela estreita, só com chaves de roteamento, sem filtro de tenant na leitura e com a escrita protegida
/// pela verificação do <c>SaveChanges</c> (a mesma exceção da rota de entrada). Achar o identificador NÃO autentica o webhook: a assinatura é verificada com o segredo da conta antes de qualquer
/// conteúdo ser interpretado. O identificador é um segredo operacional: nunca vai a log nem a resposta de listagem.
/// </summary>
public sealed class ChannelWebhook : ITenantOwned
{
    private ChannelWebhook(TenantId tenantId, Guid accountId, Guid webhookId, DateTimeOffset createdAt)
    {
        TenantId = tenantId;
        AccountId = accountId;
        WebhookId = webhookId;
        CreatedAt = createdAt;
    }

    public TenantId TenantId { get; }

    public Guid AccountId { get; }

    /// <summary>O identificador opaco da URL: 128 bits aleatórios (não derivado de nada que se saiba).</summary>
    public Guid WebhookId { get; }

    public DateTimeOffset CreatedAt { get; }

    public static ChannelWebhook For(TenantId tenantId, Guid accountId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return new ChannelWebhook(tenantId, accountId, Guid.NewGuid(), now);
    }
}
