using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts;
using IaBimeta.Tenancy.Domain;

namespace IaBimeta.Tenancy.Application;

public sealed record TenantOperationSnapshot(TenantStatus Status, DateTimeOffset? ReactivatedAt);

/// <summary>Porta de persistência do registro de tenants (dado da plataforma, sem filtro de tenant).</summary>
public interface ITenantRepository
{
    /// <exception cref="DuplicateTenantSlugException">Já existe um tenant com o mesmo slug.</exception>
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);

    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<Tenant?> FindByIdAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// O estado operacional PERSISTIDO (situação e marca de reativação), lido do banco a cada chamada, SEM rastreamento: nunca devolve a entidade que o contexto já rastreia (que pode estar desatualizada depois
    /// de uma suspensão feita em outro escopo). Nulo: empresa inexistente (quem decide trata como não operacional). A implementação padrão (para fakes de teste sem banco) lê pela entidade.
    /// </summary>
    async Task<TenantOperationSnapshot?> FindOperationStateAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        var tenant = await FindByIdAsync(tenantId, cancellationToken);
        return tenant is null ? null : new TenantOperationSnapshot(tenant.Status, tenant.ReactivatedAt);
    }

    /// <summary>
    /// Conclusão do provisionamento como transição CONDICIONAL e ATÔMICA no banco (P1-06): <c>UPDATE ... WHERE status = Provisioning</c>. Só Provisioning vira Active; Suspended, Active ou qualquer outro estado nunca é
    /// sobrescrito, mesmo que o contexto tenha uma entidade rastreada desatualizada. Devolve verdadeiro só se ESTA chamada fez a transição (falso: já não estava em provisionamento: idempotente). Atualiza a entidade rastreada, se houver.
    /// </summary>
    async Task<bool> TryCompleteProvisioningAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Implementação padrão (fakes de teste sem banco): lê, confere e grava. A real (EF) é o UPDATE condicional.
        var tenant = await FindByIdAsync(tenantId, cancellationToken);
        if (tenant is not { Status: TenantStatus.Provisioning, Kind: TenantKind.Commercial })
        {
            return false;
        }

        tenant.CompleteProvisioning(now);
        await SaveChangesAsync(cancellationToken);
        return true;
    }

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Empresas contratantes (nunca o tenant da plataforma), com pesquisa por nome, razão social, CNPJ ou identificador, filtro de situação e paginação. Mais recentes primeiro.</summary>
    Task<(IReadOnlyList<Tenant> Items, int Total)> ListCommercialAsync(string? search, TenantStatus? status, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Todas as empresas contratantes (para os indicadores agregados). Sem usuários, sem dados de cliente.</summary>
    Task<IReadOnlyList<Tenant>> ListAllCommercialAsync(CancellationToken cancellationToken);

    Task<Tenant?> FindPlatformAsync(CancellationToken cancellationToken);

    /// <summary>Grava, na mesma transação, o tenant alterado e a mudança de situação (histórico só de inclusão).</summary>
    Task SaveStatusChangeAsync(TenantStatusChange change, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantStatusChange>> ListStatusChangesAsync(TenantId tenantId, int take, CancellationToken cancellationToken);
}
