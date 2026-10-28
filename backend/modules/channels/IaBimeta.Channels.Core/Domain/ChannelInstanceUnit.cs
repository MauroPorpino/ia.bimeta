using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>
/// Uma unidade autorizada a ser atendida por uma instância de canal. A unidade é um identificador opaco do Tenancy, sem chave
/// estrangeira entre esquemas (DAD-2): quem autoriza confere no Tenancy que ela existe e é da organização do canal. Registra
/// quem autorizou e quando; a retirada fica na trilha de auditoria.
/// </summary>
public sealed class ChannelInstanceUnit : ITenantOwned
{
    private ChannelInstanceUnit(TenantId tenantId, ChannelInstanceId channelInstanceId, Guid businessUnitId, Guid? authorizedBy, DateTimeOffset authorizedAt)
    {
        TenantId = tenantId;
        ChannelInstanceId = channelInstanceId;
        BusinessUnitId = businessUnitId;
        AuthorizedBy = authorizedBy;
        AuthorizedAt = authorizedAt;
    }

    public TenantId TenantId { get; }

    public ChannelInstanceId ChannelInstanceId { get; }

    public Guid BusinessUnitId { get; }

    /// <summary>O usuário que autorizou, ou <c>null</c> se foi um contexto de sistema (o seed de desenvolvimento, por exemplo).</summary>
    public Guid? AuthorizedBy { get; }

    public DateTimeOffset AuthorizedAt { get; }

    internal static ChannelInstanceUnit Create(TenantId tenantId, ChannelInstanceId channelInstanceId, Guid businessUnitId, Guid? authorizedBy, DateTimeOffset now) =>
        new(tenantId, channelInstanceId, businessUnitId, authorizedBy, now);
}
