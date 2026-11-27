using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;

namespace IaBimeta.Tenancy.Application;

/// <summary>
/// Implementação da porta do processamento automático (Etapa 02, ADR 0043): lê o registro do tenant a cada consulta (sem cache: a suspensão vale na próxima verificação). Falha fechada:
/// empresa inexistente, suspensa ou em provisionamento NÃO é operacional. A reativação carrega o instante que serve de corte (o que nasceu antes dele não é processado automaticamente).
/// Registrada pelo <c>AddTenancy</c>, então vale igual na Api e no Worker.
/// </summary>
public sealed class TenantOperationGate : ITenantOperationGate
{
    private readonly ITenantRepository _tenants;

    public TenantOperationGate(ITenantRepository tenants)
    {
        _tenants = tenants;
    }

    public async Task<TenantOperationState> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        // Consulta de projeção sem rastreamento (P1-02): a suspensão feita em outro escopo vale na próxima consulta, mesmo neste DbContext.
        var estado = await _tenants.FindOperationStateAsync(tenantId, cancellationToken);
        return estado is { Status: TenantStatus.Active }
            ? new TenantOperationState(true, estado.ReactivatedAt)
            : new TenantOperationState(false, null);
    }
}
