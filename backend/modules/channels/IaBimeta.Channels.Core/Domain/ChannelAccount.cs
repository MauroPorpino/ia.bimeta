using IaBimeta.Channels.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>
/// A conta do tenant no provedor (no WhatsApp, a WABA). Pertence a uma organização jurídica e agrupa os números
/// (<see cref="ChannelInstance"/>) e as credenciais, que ficam por referência (<see cref="ChannelSecret"/>). A chave é
/// <c>(tenant_id, id)</c>; o identificador no provedor é único na plataforma toda, porque uma conta do provedor não pode
/// servir a dois tenants.
/// </summary>
public sealed class ChannelAccount : ITenantOwned
{
    public const int NameMaxLength = 120;

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private ChannelAccount(Guid id, TenantId tenantId, Guid organizationId, ChannelProvider provider, string providerAccountId, string name, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        OrganizationId = organizationId;
        Provider = provider;
        ProviderAccountId = providerAccountId;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    /// <summary>A organização dona da conta: é ela que decide quem pode ler e gerir (TEN-2).</summary>
    public Guid OrganizationId { get; }

    public ChannelProvider Provider { get; }

    /// <summary>O identificador da conta no provedor (WABA ID).</summary>
    public string ProviderAccountId { get; }

    public string Name { get; }

    public DateTimeOffset CreatedAt { get; }

    public static ChannelAccount Create(TenantId tenantId, Guid organizationId, ChannelProvider provider, string providerAccountId, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (organizationId == Guid.Empty)
        {
            throw new ChannelsValidationException("A conta precisa pertencer a uma organização.");
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ChannelsValidationException("O provedor é inválido.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new ChannelsValidationException($"O nome da conta deve ter de 1 a {NameMaxLength} caracteres.");
        }

        return new ChannelAccount(
            Guid.CreateVersion7(), tenantId, organizationId, provider, ProviderIdentifier.Normalize(providerAccountId, "da conta"), nomeValido, now);
    }
}
