namespace IaBimeta.SharedKernel;

/// <summary>
/// Entidade que pertence a um tenant (TEN-3). Toda tabela do tenant tem <c>tenant_id NOT NULL</c>, chaves e
/// índices compostos começando por ele, e o DbContext do módulo aplica filtro global por este campo.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
