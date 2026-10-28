using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IaBimeta.ContactCenter.Infrastructure;

internal sealed class TenantIdConverter : ValueConverter<TenantId, Guid>
{
    public TenantIdConverter()
        : base(tenantId => tenantId.Value, valor => new TenantId(valor))
    {
    }
}
