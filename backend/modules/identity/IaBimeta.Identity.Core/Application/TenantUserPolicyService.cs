using IaBimeta.Identity.Domain;
using IaBimeta.SharedKernel;
using IaBimeta.Tenancy.Contracts.Queries;

namespace IaBimeta.Identity.Application;

/// <summary>O que mudou na política de usuários do tenant: serve à auditoria feita pelo chamador (o host), que sabe QUEM operou.</summary>
public sealed record TenantUserPolicyChange(string Slug, int? PreviousMaxUsers, bool PreviousRegistrationEnabled, int? MaxUsers, bool RegistrationEnabled);

/// <summary>
/// Limite contratado de usuários e habilitação do cadastro de um tenant (Etapa 01). É operação de PLATAFORMA e, por isso, não tem rota HTTP do tenant: quem a chama é o comando do host (e, na Etapa 02, a interface da administração da
/// plataforma). O tenant só CONSULTA a situação (<see cref="UserAdminService.GetUsageAsync"/>) e nunca a altera. A gravação roda sob a mesma trava de admissão do cadastro, na mesma conexão e transação (sem corrida com um ingresso em andamento).
/// </summary>
public sealed class TenantUserPolicyService
{
    private readonly ITenantDirectory _tenants;
    private readonly IUserAdminStore _store;
    private readonly TimeProvider _clock;

    public TenantUserPolicyService(ITenantDirectory tenants, IUserAdminStore store, TimeProvider clock)
    {
        _tenants = tenants;
        _store = store;
        _clock = clock;
    }

    /// <summary>A política atual da empresa, ou <c>null</c> se a empresa não existe.</summary>
    public async Task<(string Slug, TenantId TenantId, UserPolicy Policy)?> GetAsync(string slug, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants.FindBySlugAsync(slug ?? string.Empty, cancellationToken);
        return tenant is null ? null : (tenant.Slug, tenant.TenantId, await _store.GetPolicyAsync(tenant.TenantId, cancellationToken));
    }

    /// <param name="maxUsers">O limite contratado; <c>null</c> é ilimitado, uma exceção explícita.</param>
    /// <returns><c>null</c> se a empresa não existe.</returns>
    /// <exception cref="IdentityValidationException">Limite fora da faixa.</exception>
    public async Task<TenantUserPolicyChange?> SetAsync(string slug, int? maxUsers, bool registrationEnabled, CancellationToken cancellationToken = default)
    {
        TenantUserPolicy.ValidateMax(maxUsers);
        var tenant = await _tenants.FindBySlugAsync(slug ?? string.Empty, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var (anterior, atual) = await _store.SetPolicyAsync(tenant.TenantId, maxUsers, registrationEnabled, _clock.GetUtcNow(), cancellationToken);
        return new TenantUserPolicyChange(tenant.Slug, anterior.MaxUsers, anterior.RegistrationEnabled, atual.MaxUsers, atual.RegistrationEnabled);
    }

    /// <summary>Altera SÓ o que foi pedido (limite e/ou habilitação), sobre a política relida sob a trava. <paramref name="changeMaxUsers"/> com <paramref name="maxUsers"/> nulo é "ilimitado" (exceção explícita).</summary>
    /// <returns><c>null</c> se a empresa não existe.</returns>
    /// <exception cref="IdentityValidationException">Limite fora da faixa.</exception>
    public async Task<TenantUserPolicyChange?> ChangeAsync(string slug, bool changeMaxUsers, int? maxUsers, bool? registrationEnabled, CancellationToken cancellationToken = default)
    {
        if (changeMaxUsers)
        {
            TenantUserPolicy.ValidateMax(maxUsers);
        }

        var tenant = await _tenants.FindBySlugAsync(slug ?? string.Empty, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var (anterior, atual) = await _store.ChangePolicyAsync(tenant.TenantId, changeMaxUsers, maxUsers, registrationEnabled, _clock.GetUtcNow(), cancellationToken);
        return new TenantUserPolicyChange(tenant.Slug, anterior.MaxUsers, anterior.RegistrationEnabled, atual.MaxUsers, atual.RegistrationEnabled);
    }

    /// <summary>Provisionamento repetível: cria a política só se ainda não existe (nunca altera a de uma empresa que já tem). Devolve <c>true</c> se criou.</summary>
    public Task<bool> EnsureAsync(TenantId tenantId, int? maxUsers, bool registrationEnabled, CancellationToken cancellationToken = default)
    {
        TenantUserPolicy.ValidateMax(maxUsers);
        return _store.EnsurePolicyAsync(tenantId, maxUsers, registrationEnabled, _clock.GetUtcNow(), cancellationToken);
    }
}
